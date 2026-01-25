using System.Diagnostics;

namespace MultiRoomAudio.Services;

/// <summary>
/// Runs startup diagnostics for PulseAudio connectivity and configuration.
/// Logs diagnostic information to help troubleshoot audio issues.
/// </summary>
public class StartupDiagnosticsService
{
    private readonly ILogger<StartupDiagnosticsService> _logger;
    private readonly EnvironmentService _environmentService;

    public StartupDiagnosticsService(
        ILogger<StartupDiagnosticsService> logger,
        EnvironmentService environmentService)
    {
        _logger = logger;
        _environmentService = environmentService;
    }

    /// <summary>
    /// Runs PulseAudio diagnostics when running in HAOS mode.
    /// Logs information about PulseAudio server, sockets, and available sinks.
    /// </summary>
    public void RunPulseAudioDiagnostics()
    {
        if (!_environmentService.IsHaos)
        {
            _logger.LogInformation("Running in standalone Docker mode");
            return;
        }

        _logger.LogInformation("Running as Home Assistant add-on");
        _logger.LogDebug("Supervisor token present: {HasToken}",
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")));

        // Log PulseAudio diagnostic info
        var pulseServer = Environment.GetEnvironmentVariable("PULSE_SERVER");
        _logger.LogInformation("PULSE_SERVER: {PulseServer}", pulseServer ?? "(not set)");

        // Check for PulseAudio socket directories
        CheckPulseAudioSockets();

        // Try to run pactl diagnostics
        RunPactlDiagnostics();
    }

    private void CheckPulseAudioSockets()
    {
        var pulseSocketPaths = new[] { "/run/pulse", "/var/run/pulse", "/tmp/pulse" };
        foreach (var path in pulseSocketPaths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                    _logger.LogInformation("PulseAudio socket directory {Path}: {FileCount} files", path, files.Length);
                    foreach (var file in files.Take(5))
                    {
                        _logger.LogDebug("  {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not enumerate {Path}: {Error}", path, ex.Message);
                }
            }
        }
    }

    private void RunPactlDiagnostics()
    {
        try
        {
            // Run pactl info for diagnostics
            RunPactlInfo();

            // Also list available sinks
            RunPactlListSinks();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not run pactl diagnostics: {Error}", ex.Message);
        }
    }

    private void RunPactlInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pactl",
            Arguments = "info",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            _logger.LogWarning("Failed to start pactl process for diagnostics. Is PulseAudio available?");
            return;
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);

        if (process.ExitCode == 0)
        {
            _logger.LogInformation("PulseAudio connected successfully");
            // Log key info lines
            foreach (var line in output.Split('\n').Where(l =>
                l.StartsWith("Server Name:") ||
                l.StartsWith("Default Sink:") ||
                l.StartsWith("Default Source:")))
            {
                _logger.LogInformation("  {Line}", line.Trim());
            }
        }
        else
        {
            _logger.LogWarning("PulseAudio connection failed: {Error}", error.Trim());
        }
    }

    private void RunPactlListSinks()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pactl",
            Arguments = "list sinks short",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var sinkProcess = Process.Start(psi);
        if (sinkProcess == null)
        {
            _logger.LogWarning("Failed to start pactl process for sink diagnostics. Is PulseAudio available?");
            return;
        }

        var sinkOutput = sinkProcess.StandardOutput.ReadToEnd();
        sinkProcess.WaitForExit(5000);

        if (sinkProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(sinkOutput))
        {
            _logger.LogInformation("PulseAudio sinks available:");
            foreach (var line in sinkOutput.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                _logger.LogInformation("  {Sink}", line.Trim());
            }
        }
    }
}
