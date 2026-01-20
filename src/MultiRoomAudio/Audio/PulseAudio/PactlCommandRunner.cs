using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiRoomAudio.Audio.PulseAudio;

/// <summary>
/// Result of a pactl command execution.
/// </summary>
public record PactlResult(int ExitCode, string Output, string Error)
{
    /// <summary>
    /// Whether the command succeeded (exit code 0).
    /// </summary>
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Shared utility for running PulseAudio (pactl) commands with retry logic and proper error handling.
/// Centralizes command execution to ensure consistent behavior across all PulseAudio-related code.
/// </summary>
public static partial class PactlCommandRunner
{
    private static ILogger? _logger;

    /// <summary>
    /// Maximum retries for pactl commands when PulseAudio is temporarily unavailable.
    /// </summary>
    private const int DefaultMaxRetries = 3;

    /// <summary>
    /// Delay between pactl retry attempts in milliseconds.
    /// </summary>
    private const int DefaultRetryDelayMs = 500;

    /// <summary>
    /// Default timeout for pactl commands in milliseconds.
    /// </summary>
    private const int DefaultTimeoutMs = 5000;

    /// <summary>
    /// Pattern for valid PulseAudio sink/module/card names.
    /// Matches alphanumeric names with dots, hyphens, underscores, colons, and plus signs.
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_\-.:+]+$", RegexOptions.Compiled)]
    private static partial Regex ValidNamePattern();

    /// <summary>
    /// Characters that are dangerous in shell commands and must be rejected.
    /// </summary>
    private static readonly char[] DangerousChars =
        [';', '&', '|', '$', '`', '(', ')', '{', '}', '[', ']', '<', '>', '!', '\\', '"', '\'', '\n', '\r', '\0'];

    /// <summary>
    /// Configures the logger for command execution diagnostics.
    /// </summary>
    public static void SetLogger(ILogger? logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates a PulseAudio name (sink, card, profile, etc.) to prevent command injection.
    /// </summary>
    /// <param name="name">The name to validate.</param>
    /// <param name="errorMessage">Error message if validation fails.</param>
    /// <returns>True if the name is safe to use.</returns>
    public static bool ValidateName(string? name, out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "Name cannot be empty.";
            return false;
        }

        // Check for dangerous shell metacharacters
        if (name.IndexOfAny(DangerousChars) >= 0)
        {
            errorMessage = "Name contains invalid characters.";
            return false;
        }

        // Check maximum length
        if (name.Length > 200)
        {
            errorMessage = "Name exceeds maximum length of 200 characters.";
            return false;
        }

        // Validate against whitelist pattern
        if (!ValidNamePattern().IsMatch(name))
        {
            errorMessage = $"Name '{name}' does not match expected format (alphanumeric, dots, hyphens, underscores, colons, plus signs only).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a sink name, allowing null/empty for default sink.
    /// </summary>
    public static bool ValidateSinkName(string? sink, out string? errorMessage)
    {
        errorMessage = null;

        // Null or empty sink is allowed (will use default)
        if (string.IsNullOrWhiteSpace(sink))
        {
            return true;
        }

        return ValidateName(sink, out errorMessage);
    }

    /// <summary>
    /// Run a pactl command synchronously with retry logic.
    /// </summary>
    /// <param name="arguments">Command arguments (e.g., "list sinks").</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="retryDelayMs">Delay between retries in milliseconds.</param>
    /// <param name="timeoutMs">Command timeout in milliseconds.</param>
    /// <returns>Command output on success, null on failure.</returns>
    public static string? Run(
        string arguments,
        int maxRetries = DefaultMaxRetries,
        int retryDelayMs = DefaultRetryDelayMs,
        int timeoutMs = DefaultTimeoutMs)
    {
        var result = RunWithResult(arguments, maxRetries, retryDelayMs, timeoutMs);
        return result.Success ? result.Output : null;
    }

    /// <summary>
    /// Run a pactl command synchronously with retry logic, returning full result.
    /// </summary>
    /// <param name="arguments">Command arguments (e.g., "list sinks").</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="retryDelayMs">Delay between retries in milliseconds.</param>
    /// <param name="timeoutMs">Command timeout in milliseconds.</param>
    /// <returns>Command result with exit code, output, and error.</returns>
    public static PactlResult RunWithResult(
        string arguments,
        int maxRetries = DefaultMaxRetries,
        int retryDelayMs = DefaultRetryDelayMs,
        int timeoutMs = DefaultTimeoutMs)
    {
        Exception? lastException = null;
        string lastError = string.Empty;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pactl",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    lastError = "Failed to start pactl process";
                    continue;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(timeoutMs);

                if (process.ExitCode != 0)
                {
                    lastError = error.Trim();

                    // Check if this is a connection error that might be transient
                    if (IsTransientError(error) && attempt < maxRetries)
                    {
                        _logger?.LogDebug(
                            "pactl {Args} failed (attempt {Attempt}/{Max}): {Error}. Retrying...",
                            arguments, attempt, maxRetries, lastError);
                        Thread.Sleep(retryDelayMs);
                        continue;
                    }

                    _logger?.LogWarning("pactl {Args} failed: {Error}", arguments, lastError);
                    return new PactlResult(process.ExitCode, output, lastError);
                }

                // Success
                return new PactlResult(0, output, string.Empty);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < maxRetries)
                {
                    _logger?.LogDebug(ex,
                        "pactl {Args} threw exception (attempt {Attempt}/{Max}). Retrying...",
                        arguments, attempt, maxRetries);
                    Thread.Sleep(retryDelayMs);
                }
            }
        }

        // All retries exhausted
        if (lastException != null)
        {
            _logger?.LogError(lastException, "Failed to run pactl {Args} after {Attempts} attempts",
                arguments, maxRetries);
            return new PactlResult(-1, string.Empty, lastException.Message);
        }

        _logger?.LogWarning("pactl {Args} failed after {Attempts} attempts: {Error}",
            arguments, maxRetries, lastError);
        return new PactlResult(-1, string.Empty, lastError);
    }

    /// <summary>
    /// Run a pactl command asynchronously with argument list (safer than string arguments).
    /// </summary>
    /// <param name="arguments">Command arguments as array (e.g., ["set-sink-volume", "@DEFAULT_SINK@", "50%"]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="retryDelayMs">Delay between retries in milliseconds.</param>
    /// <returns>Command result with exit code, output, and error.</returns>
    public static async Task<PactlResult> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken = default,
        int maxRetries = 1,
        int retryDelayMs = DefaultRetryDelayMs)
    {
        Exception? lastException = null;
        string lastError = string.Empty;
        var argsForLogging = string.Join(" ", arguments);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pactl",
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

                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await Task.WhenAll(outputTask, errorTask);
                await process.WaitForExitAsync(cancellationToken);

                var output = outputTask.Result;
                var error = errorTask.Result;

                _logger?.LogDebug("pactl {Args} -> exit {ExitCode}", argsForLogging, process.ExitCode);

                if (process.ExitCode != 0)
                {
                    lastError = error.Trim();

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _logger?.LogDebug("pactl stderr: {Error}", error);
                    }

                    // Check if this is a connection error that might be transient
                    if (IsTransientError(error) && attempt < maxRetries)
                    {
                        _logger?.LogDebug(
                            "pactl {Args} failed (attempt {Attempt}/{Max}): {Error}. Retrying...",
                            argsForLogging, attempt, maxRetries, lastError);
                        await Task.Delay(retryDelayMs, cancellationToken);
                        continue;
                    }

                    return new PactlResult(process.ExitCode, output, lastError);
                }

                return new PactlResult(0, output, string.Empty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger?.LogDebug(ex, "pactl command failed: {Args}", argsForLogging);

                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs, cancellationToken);
                }
            }
        }

        if (lastException != null)
        {
            return new PactlResult(-1, string.Empty, lastException.Message);
        }

        return new PactlResult(-1, string.Empty, lastError);
    }

    /// <summary>
    /// Checks if an error is transient and worth retrying.
    /// </summary>
    private static bool IsTransientError(string error)
    {
        return error.Contains("Connection refused") ||
               error.Contains("Connection failure") ||
               error.Contains("No PulseAudio daemon running");
    }
}
