using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static MultiRoomAudio.Audio.PulseAudio.PulseAudioNative;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// Event args for sink appearance/disappearance events.
/// </summary>
/// <param name="Index">PulseAudio sink index.</param>
public record SinkEventArgs(uint Index);

/// <summary>
/// Singleton service that subscribes to PulseAudio sink events.
/// Uses a dedicated threaded mainloop to receive callbacks for device changes.
/// </summary>
/// <remarks>
/// This service enables automatic player restart when USB audio devices are
/// reconnected. When a sink appears, subscribers can check if any players
/// waiting for their device can be restarted.
/// </remarks>
public class PulseAudioSubscriptionService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<PulseAudioSubscriptionService> _logger;
    private readonly object _lock = new();

    // PulseAudio handles
    private IntPtr _mainloop = IntPtr.Zero;
    private IntPtr _context = IntPtr.Zero;

    // CRITICAL: Store callbacks as fields to prevent GC collection.
    // If these are local variables, the GC may collect them while PA still holds references.
    private ContextNotifyCallback? _contextStateCallback;
    private SubscriptionCallback? _subscriptionCallback;
    private ContextSuccessCallback? _subscribeSuccessCallback;

    private volatile bool _disposed;
    private volatile bool _ready;

    // Events for sink changes
    /// <summary>
    /// Fired when a PulseAudio sink appears (e.g., USB device plugged in).
    /// </summary>
    public event EventHandler<SinkEventArgs>? SinkAppeared;

    /// <summary>
    /// Fired when a PulseAudio sink disappears (e.g., USB device unplugged).
    /// </summary>
    public event EventHandler<SinkEventArgs>? SinkDisappeared;

    private const int ConnectionTimeoutMs = 10000;

    public PulseAudioSubscriptionService(ILogger<PulseAudioSubscriptionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Whether the subscription service is connected and receiving events.
    /// </summary>
    public bool IsReady => _ready && !_disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run initialization on thread pool to avoid blocking startup
        return Task.Run(() =>
        {
            try
            {
                InitializeSubscriptions();
            }
            catch (Exception ex)
            {
                // Log but don't throw - this is an optional feature
                _logger.LogWarning(ex, "PulseAudio subscription service failed to start. Device auto-reconnect will not be available.");
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return DisposeAsync().AsTask();
    }

    private void InitializeSubscriptions()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            try
            {
                _mainloop = ThreadedMainloopNew();
                if (_mainloop == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to create PulseAudio mainloop");

                var api = ThreadedMainloopGetApi(_mainloop);
                if (api == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to get PulseAudio mainloop API");

                _context = ContextNew(api, "MultiRoomAudio-DeviceMonitor");
                if (_context == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to create PulseAudio context");

                // Store callback to prevent GC
                _contextStateCallback = OnContextStateChanged;
                ContextSetStateCallback(_context, _contextStateCallback, IntPtr.Zero);

                if (ThreadedMainloopStart(_mainloop) < 0)
                    throw new InvalidOperationException("Failed to start PulseAudio mainloop");

                ThreadedMainloopLock(_mainloop);
                try
                {
                    if (ContextConnect(_context, null, 0, IntPtr.Zero) < 0)
                        throw new InvalidOperationException("Failed to connect to PulseAudio");

                    // Wait for context ready
                    var timeout = DateTime.UtcNow.AddMilliseconds(ConnectionTimeoutMs);
                    while (!_ready && !_disposed)
                    {
                        var state = ContextGetState(_context);
                        if (state == ContextState.Failed || state == ContextState.Terminated)
                            throw new InvalidOperationException($"PulseAudio context failed: {GetContextError(_context)}");

                        if (DateTime.UtcNow > timeout)
                            throw new TimeoutException("Timeout waiting for PulseAudio context");

                        ThreadedMainloopWait(_mainloop);
                    }

                    // Now subscribe to sink events
                    SetupSubscription();
                }
                finally
                {
                    ThreadedMainloopUnlock(_mainloop);
                }

                _logger.LogInformation("PulseAudio subscription service started - monitoring sink events for device auto-reconnect");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PulseAudio subscription service");
                CleanupResources();
                throw;
            }
        }
    }

    private void SetupSubscription()
    {
        // Store callbacks to prevent GC
        _subscriptionCallback = OnSubscriptionEvent;
        _subscribeSuccessCallback = OnSubscribeSuccess;

        // Set the subscription callback first (before subscribing)
        ContextSetSubscribeCallback(_context, _subscriptionCallback, IntPtr.Zero);

        // Subscribe to sink events (includes sink creation/removal)
        var op = ContextSubscribe(_context, SubscriptionMask.Sink, _subscribeSuccessCallback, IntPtr.Zero);
        if (op != IntPtr.Zero)
            OperationUnref(op);
    }

    private void OnContextStateChanged(IntPtr context, IntPtr userdata)
    {
        var state = ContextGetState(context);
        _logger.LogDebug("PulseAudio subscription context state: {State}", state);

        if (state == ContextState.Ready)
        {
            _ready = true;
            ThreadedMainloopSignal(_mainloop, 0);
        }
        else if (state == ContextState.Failed || state == ContextState.Terminated)
        {
            _ready = false;
            ThreadedMainloopSignal(_mainloop, 0);

            if (!_disposed)
                _logger.LogWarning("PulseAudio subscription context disconnected: {State}", state);
        }
    }

    private void OnSubscribeSuccess(IntPtr context, int success, IntPtr userdata)
    {
        if (success != 0)
            _logger.LogDebug("Successfully subscribed to PulseAudio sink events");
        else
            _logger.LogWarning("Failed to subscribe to PulseAudio sink events: {Error}", GetContextError(context));
    }

    private void OnSubscriptionEvent(IntPtr context, uint eventType, uint index, IntPtr userdata)
    {
        // Extract the facility (what type of object) and event type (what happened)
        // NOTE: Use SubscriptionEventFacility (not SubscriptionMask) - PA uses different values
        // for subscription masks vs event facilities!
        var facility = (SubscriptionEventFacility)(eventType & (uint)SubscriptionEventType.FacilityMask);
        var type = (SubscriptionEventType)(eventType & (uint)SubscriptionEventType.TypeMask);

        // Only handle sink events
        if (facility != SubscriptionEventFacility.Sink)
            return;

        _logger.LogDebug("PulseAudio sink event: {EventType} index={Index}", type, index);

        // IMPORTANT: This callback runs on PA's mainloop thread.
        // Fire events asynchronously to avoid blocking the mainloop.
        // Heavy work (like device matching and player restart) happens on the subscriber's thread.
        var args = new SinkEventArgs(index);

        switch (type)
        {
            case SubscriptionEventType.New:
                _logger.LogInformation("PulseAudio sink appeared: index={Index}", index);
                ThreadPool.QueueUserWorkItem(_ => SinkAppeared?.Invoke(this, args));
                break;

            case SubscriptionEventType.Remove:
                _logger.LogDebug("PulseAudio sink removed: index={Index}", index);
                ThreadPool.QueueUserWorkItem(_ => SinkDisappeared?.Invoke(this, args));
                break;
        }
    }

    private void CleanupResources()
    {
        _ready = false;

        if (_context != IntPtr.Zero && _mainloop != IntPtr.Zero)
        {
            // Check if we're on the mainloop thread to avoid deadlock
            var inThread = ThreadedMainloopInThread(_mainloop) != 0;
            if (!inThread)
                ThreadedMainloopLock(_mainloop);
            try
            {
                ContextDisconnect(_context);
            }
            finally
            {
                if (!inThread)
                    ThreadedMainloopUnlock(_mainloop);
            }
            ContextUnref(_context);
            _context = IntPtr.Zero;
        }

        if (_mainloop != IntPtr.Zero)
        {
            ThreadedMainloopStop(_mainloop);
            ThreadedMainloopFree(_mainloop);
            _mainloop = IntPtr.Zero;
        }

        // Clear callbacks to allow GC
        _contextStateCallback = null;
        _subscriptionCallback = null;
        _subscribeSuccessCallback = null;
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
        }

        CleanupResources();
        _logger.LogInformation("PulseAudio subscription service disposed");
        return ValueTask.CompletedTask;
    }
}
