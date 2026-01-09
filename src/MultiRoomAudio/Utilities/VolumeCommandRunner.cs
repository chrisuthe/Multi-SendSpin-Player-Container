using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Runs PulseAudio (pactl) commands for volume control.
/// </summary>
public partial class VolumeCommandRunner
{
    private readonly ILogger<VolumeCommandRunner> _logger;

    /// <summary>
    /// Pattern for valid PulseAudio sink names.
    /// Matches alphanumeric names with dots, hyphens, and underscores.
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]+$", RegexOptions.Compiled)]
    private static partial Regex ValidSinkPattern();

    /// <summary>
    /// Characters that are dangerous in shell commands and must be rejected.
    /// </summary>
    private static readonly char[] DangerousChars = { ';', '&', '|', '$', '`', '(', ')', '{', '}', '[', ']', '<', '>', '!', '\\', '"', '\'', '\n', '\r', '\0' };

    public VolumeCommandRunner(ILogger<VolumeCommandRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates a sink name to prevent command injection.
    /// </summary>
    /// <param name="sink">The sink name to validate.</param>
    /// <param name="errorMessage">Error message if validation fails.</param>
    /// <returns>True if the sink name is safe to use.</returns>
    public static bool ValidateSinkName(string? sink, out string? errorMessage)
    {
        errorMessage = null;

        // Null or empty sink is allowed (will use default)
        if (string.IsNullOrWhiteSpace(sink))
        {
            return true;
        }

        // Check for dangerous shell metacharacters
        if (sink.IndexOfAny(DangerousChars) >= 0)
        {
            errorMessage = "Sink name contains invalid characters.";
            return false;
        }

        // Check maximum length
        if (sink.Length > 200)
        {
            errorMessage = "Sink name exceeds maximum length of 200 characters.";
            return false;
        }

        // Validate against whitelist pattern
        if (!ValidSinkPattern().IsMatch(sink))
        {
            errorMessage = $"Sink name '{sink}' does not match expected format.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Get current volume as percentage (0-100) for the specified sink or default.
    /// </summary>
    public async Task<int?> GetVolumeAsync(string? sink = null, CancellationToken cancellationToken = default)
    {
        if (!ValidateSinkName(sink, out var validationError))
        {
            _logger.LogWarning("Invalid sink name rejected: {Error}", validationError);
            return null;
        }

        try
        {
            var sinkArg = string.IsNullOrEmpty(sink) ? "@DEFAULT_SINK@" : sink;
            var result = await RunCommandAsync(
                "pactl",
                ["get-sink-volume", sinkArg],
                cancellationToken);

            if (result.ExitCode != 0)
                return null;

            // Parse output like "front-left: 65536 / 100%"
            var match = Regex.Match(result.Output, @"(\d+)%");
            if (match.Success)
            {
                var volumePercent = int.Parse(match.Groups[1].Value);
                _logger.LogDebug("VOLUME [Hardware] Sink '{Sink}' current volume: {Volume}%", sinkArg, volumePercent);
                return volumePercent;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get volume for sink {Sink}", sink ?? "default");
            return null;
        }
    }

    /// <summary>
    /// Set volume as percentage (0-100) for the specified sink or default.
    /// </summary>
    public async Task<bool> SetVolumeAsync(string? sink, int volume, CancellationToken cancellationToken = default)
    {
        if (!ValidateSinkName(sink, out var validationError))
        {
            _logger.LogWarning("Invalid sink name rejected: {Error}", validationError);
            return false;
        }

        volume = Math.Clamp(volume, 0, 100);

        try
        {
            var sinkArg = string.IsNullOrEmpty(sink) ? "@DEFAULT_SINK@" : sink;
            _logger.LogInformation("VOLUME [Hardware] Setting sink '{Sink}' to {Volume}%", sinkArg, volume);
            var result = await RunCommandAsync(
                "pactl",
                ["set-sink-volume", sinkArg, $"{volume}%"],
                cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set volume for sink {Sink} to {Volume}", sink ?? "default", volume);
            return false;
        }
    }

    /// <summary>
    /// Set mute state for the specified sink or default.
    /// </summary>
    public async Task<bool> SetMuteAsync(string? sink, bool muted, CancellationToken cancellationToken = default)
    {
        if (!ValidateSinkName(sink, out var validationError))
        {
            _logger.LogWarning("Invalid sink name rejected: {Error}", validationError);
            return false;
        }

        try
        {
            var sinkArg = string.IsNullOrEmpty(sink) ? "@DEFAULT_SINK@" : sink;
            var muteArg = muted ? "1" : "0";
            var result = await RunCommandAsync(
                "pactl",
                ["set-sink-mute", sinkArg, muteArg],
                cancellationToken);
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set mute for sink {Sink} to {Muted}", sink ?? "default", muted);
            return false;
        }
    }

    private async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Use ArgumentList for proper escaping - prevents shell injection
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var argsForLogging = string.Join(" ", arguments);

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);

            var output = outputTask.Result;
            var error = errorTask.Result;

            if (!string.IsNullOrWhiteSpace(error))
            {
                _logger.LogDebug("Command stderr for {Command} {Args}: {Error}", command, argsForLogging, error);
            }

            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Command failed: {Command} {Args}", command, argsForLogging);
            return (-1, string.Empty);
        }
    }
}
