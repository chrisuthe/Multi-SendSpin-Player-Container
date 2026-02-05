using System.Diagnostics;
using System.Text.RegularExpressions;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Services;

/// <summary>
/// Background service that subscribes to PulseAudio sink change events.
/// Monitors volume and mute changes from hardware buttons.
/// </summary>
public partial class PaSinkEventService : BackgroundService
{
    private readonly ILogger<PaSinkEventService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private Process? _subscribeProcess;
    private readonly object _processLock = new();

    /// <summary>
    /// Event raised when a sink's volume or mute state changes.
    /// </summary>
    public event EventHandler<SinkChangeEventArgs>? SinkChanged;

    /// <summary>
    /// Whether the service is currently running and connected to PulseAudio.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Regex to parse sink change events from pactl subscribe output.
    /// Matches: Event 'change' on sink #5
    /// </summary>
    [GeneratedRegex(@"Event 'change' on sink #(\d+)", RegexOptions.Compiled)]
    private static partial Regex SinkChangeEventPattern();

    /// <summary>
    /// Regex to parse volume from pactl output.
    /// Matches: front-left: 65536 / 100% / 0.00 dB
    /// </summary>
    [GeneratedRegex(@"(\d+)%", RegexOptions.Compiled)]
    private static partial Regex VolumePercentPattern();

    /// <summary>
    /// Regex to parse mute state from pactl output.
    /// Matches: Mute: yes or Mute: no
    /// </summary>
    [GeneratedRegex(@"Mute:\s*(yes|no)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MuteStatePattern();

    /// <summary>
    /// Regex to parse sink name from pactl list sinks short output.
    /// Format: index\tname\tmodule\tformat\tstate
    /// </summary>
    [GeneratedRegex(@"^\s*(\d+)\s+([^\s]+)", RegexOptions.Compiled)]
    private static partial Regex SinkListShortPattern();

    public PaSinkEventService(
        ILogger<PaSinkEventService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaSinkEventService starting - will monitor PulseAudio sink events");

        // Wait a bit for PulseAudio to be ready
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSubscribeLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("PaSinkEventService stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PaSinkEventService error, will retry in 5 seconds");
                IsConnected = false;

                try
                {
                    await Task.Delay(5000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        StopSubscribeProcess();
        IsConnected = false;
    }

    private async Task RunSubscribeLoopAsync(CancellationToken stoppingToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pactl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("subscribe");

        lock (_processLock)
        {
            _subscribeProcess = new Process { StartInfo = psi };
            _subscribeProcess.Start();
        }

        _logger.LogInformation("Started pactl subscribe (PID: {Pid})", _subscribeProcess.Id);
        IsConnected = true;

        // Read lines from stdout
        using var reader = _subscribeProcess.StandardOutput;

        while (!stoppingToken.IsCancellationRequested && !_subscribeProcess.HasExited)
        {
            var line = await reader.ReadLineAsync(stoppingToken);

            if (line == null)
            {
                // EOF - process ended
                break;
            }

            // Parse sink change events
            var match = SinkChangeEventPattern().Match(line);
            if (match.Success)
            {
                var sinkIndex = int.Parse(match.Groups[1].Value);
                _logger.LogDebug("Detected sink change event for sink #{SinkIndex}", sinkIndex);

                // Query the current state of the sink
                await OnSinkChangeDetectedAsync(sinkIndex, stoppingToken);
            }
        }

        // Wait for process to exit
        if (!_subscribeProcess.HasExited)
        {
            try
            {
                _subscribeProcess.Kill();
            }
            catch
            {
                // Ignore
            }
        }

        await _subscribeProcess.WaitForExitAsync(stoppingToken);

        _logger.LogInformation("pactl subscribe process exited with code {ExitCode}", _subscribeProcess.ExitCode);
        IsConnected = false;

        lock (_processLock)
        {
            _subscribeProcess.Dispose();
            _subscribeProcess = null;
        }
    }

    private async Task OnSinkChangeDetectedAsync(int sinkIndex, CancellationToken cancellationToken)
    {
        try
        {
            // Get sink name from index
            var sinkName = await GetSinkNameByIndexAsync(sinkIndex, cancellationToken);
            if (string.IsNullOrEmpty(sinkName))
            {
                _logger.LogDebug("Could not find sink name for index {SinkIndex}", sinkIndex);
                return;
            }

            // Query volume and mute state
            var (volume, muted) = await GetSinkStateAsync(sinkName, cancellationToken);

            if (volume.HasValue)
            {
                var args = new SinkChangeEventArgs(
                    SinkIndex: sinkIndex,
                    SinkName: sinkName,
                    VolumePercent: volume.Value,
                    IsMuted: muted,
                    Timestamp: DateTime.UtcNow
                );

                _logger.LogDebug("Raising SinkChanged event: Sink={Sink}, Volume={Volume}%, Muted={Muted}",
                    sinkName, volume, muted);

                SinkChanged?.Invoke(this, args);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling sink change for index {SinkIndex}", sinkIndex);
        }
    }

    private async Task<string?> GetSinkNameByIndexAsync(int sinkIndex, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunPactlAsync(["list", "sinks", "short"], cancellationToken);
            if (result.ExitCode != 0)
                return null;

            var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var match = SinkListShortPattern().Match(line);
                if (match.Success)
                {
                    var index = int.Parse(match.Groups[1].Value);
                    if (index == sinkIndex)
                    {
                        return match.Groups[2].Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting sink name for index {SinkIndex}", sinkIndex);
        }

        return null;
    }

    private async Task<(int? Volume, bool Muted)> GetSinkStateAsync(string sinkName, CancellationToken cancellationToken)
    {
        try
        {
            // Get volume
            var volumeResult = await RunPactlAsync(["get-sink-volume", sinkName], cancellationToken);
            int? volume = null;
            if (volumeResult.ExitCode == 0)
            {
                var match = VolumePercentPattern().Match(volumeResult.Output);
                if (match.Success)
                {
                    volume = int.Parse(match.Groups[1].Value);
                }
            }

            // Get mute state
            var muteResult = await RunPactlAsync(["get-sink-mute", sinkName], cancellationToken);
            var muted = false;
            if (muteResult.ExitCode == 0)
            {
                var match = MuteStatePattern().Match(muteResult.Output);
                if (match.Success)
                {
                    muted = match.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                }
            }

            return (volume, muted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting sink state for {SinkName}", sinkName);
            return (null, false);
        }
    }

    private async Task<(int ExitCode, string Output)> RunPactlAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pactl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);

            return (process.ExitCode, outputTask.Result);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "pactl command failed: {Args}", string.Join(" ", arguments));
            return (-1, string.Empty);
        }
    }

    private void StopSubscribeProcess()
    {
        lock (_processLock)
        {
            if (_subscribeProcess != null && !_subscribeProcess.HasExited)
            {
                try
                {
                    _subscribeProcess.Kill();
                    _logger.LogDebug("Killed pactl subscribe process");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error killing pactl subscribe process");
                }
            }
        }
    }

    public override void Dispose()
    {
        StopSubscribeProcess();
        base.Dispose();
    }
}
