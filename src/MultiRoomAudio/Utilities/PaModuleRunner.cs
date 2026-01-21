using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Runs PulseAudio (pactl) commands for loading/unloading audio modules.
/// Provides secure command execution to prevent shell injection attacks.
/// </summary>
public partial class PaModuleRunner : IPaModuleRunner
{
    private readonly ILogger<PaModuleRunner> _logger;

    /// <summary>
    /// Pattern for valid PulseAudio sink/module names.
    /// Matches alphanumeric names with dots, hyphens, and underscores.
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]+$", RegexOptions.Compiled)]
    private static partial Regex ValidNamePattern();

    /// <summary>
    /// Characters that are dangerous in shell commands and must be rejected.
    /// </summary>
    private static readonly char[] DangerousChars =
        [';', '&', '|', '$', '`', '(', ')', '{', '}', '[', ']', '<', '>', '!', '\\', '"', '\'', '\n', '\r', '\0'];

    public PaModuleRunner(ILogger<PaModuleRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates a sink/module name to prevent command injection.
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
            errorMessage = $"Name '{name}' does not match expected format (alphanumeric, dots, hyphens, underscores only).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sanitizes a description string for use in device.description property.
    /// PulseAudio has a known bug where property values with spaces fail to parse
    /// (see https://gitlab.freedesktop.org/pulseaudio/pulseaudio/-/issues/615).
    /// We replace spaces with underscores as the standard workaround.
    /// The original description with spaces is preserved in YAML/UI.
    /// </summary>
    private static string SanitizeDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return description;

        // Sanitize for PulseAudio property values
        // PulseAudio has a bug where spaces in property values cause "Failed to parse proplist"
        // See: https://gitlab.freedesktop.org/pulseaudio/pulseaudio/-/issues/615
        // Replace spaces with underscores as the workaround
        return description
            .Replace("\\", "")      // Remove backslashes (escape character)
            .Replace("\"", "")      // Remove double quotes
            .Replace("'", "")       // Remove single quotes
            .Replace(" ", "_")      // Replace spaces with underscores (PulseAudio bug workaround)
            .Replace("&", "_and_")  // Replace & with _and_ for readability
            .Replace("\n", "_")     // Replace newlines with underscores
            .Replace("\r", "")      // Remove carriage returns
            .Replace("\0", "")      // Remove null chars
            .Trim();
    }

    /// <summary>
    /// Load module-combine-sink to merge multiple audio outputs.
    /// </summary>
    /// <param name="sinkName">Unique name for the new combined sink.</param>
    /// <param name="slaves">List of slave sink names to combine.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Module index on success, null on failure.</returns>
    public async Task<int?> LoadCombineSinkAsync(
        string sinkName,
        IEnumerable<string> slaves,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        // Validate sink name
        if (!ValidateName(sinkName, out var error))
        {
            _logger.LogWarning("Invalid sink name rejected: {Error}", error);
            throw new ArgumentException(error, nameof(sinkName));
        }

        // Validate all slave names
        var slaveList = slaves.ToList();
        if (slaveList.Count < 2)
        {
            throw new ArgumentException("At least 2 slave sinks are required.", nameof(slaves));
        }

        foreach (var slave in slaveList)
        {
            if (!ValidateName(slave, out error))
            {
                throw new ArgumentException($"Invalid slave sink name: {error}", nameof(slaves));
            }
        }

        // Build module arguments
        var args = new List<string>
        {
            "load-module",
            "module-combine-sink",
            $"sink_name={sinkName}",
            $"slaves={string.Join(",", slaveList)}"
        };

        // Add description if provided
        if (!string.IsNullOrWhiteSpace(description))
        {
            var safeDesc = SanitizeDescription(description);
            // No quotes needed - spaces are replaced with underscores due to PulseAudio bug
            args.Add($"sink_properties=device.description={safeDesc}");
        }

        _logger.LogInformation("Loading combine-sink '{SinkName}' with {SlaveCount} slaves. Args: {Args}",
            sinkName, slaveList.Count, string.Join(" | ", args));

        var result = await RunPactlAsync(args.ToArray(), cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogError("Failed to load combine-sink '{SinkName}': {Error}", sinkName, result.Error);
            return null;
        }

        // Parse module index from output
        if (int.TryParse(result.Output.Trim(), out var moduleIndex))
        {
            _logger.LogInformation("Successfully loaded combine-sink '{SinkName}' with module index {Index}", sinkName, moduleIndex);
            return moduleIndex;
        }

        _logger.LogWarning("Loaded combine-sink '{SinkName}' but could not parse module index from: {Output}", sinkName, result.Output);
        return null;
    }

    /// <summary>
    /// Load module-remap-sink to extract/remap channels from a multi-channel device.
    /// </summary>
    /// <param name="sinkName">Unique name for the new remapped sink.</param>
    /// <param name="masterSink">Master sink to extract channels from.</param>
    /// <param name="channels">Number of output channels.</param>
    /// <param name="channelMap">Output channel map (e.g., "front-left,front-right").</param>
    /// <param name="masterChannelMap">Source channel map from master.</param>
    /// <param name="remix">Whether to enable remixing.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Module index on success, null on failure.</returns>
    public async Task<int?> LoadRemapSinkAsync(
        string sinkName,
        string masterSink,
        int channels,
        string channelMap,
        string masterChannelMap,
        bool remix = false,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        // Validate sink name
        if (!ValidateName(sinkName, out var error))
        {
            _logger.LogWarning("Invalid sink name rejected: {Error}", error);
            throw new ArgumentException(error, nameof(sinkName));
        }

        // Validate master sink name
        if (!ValidateName(masterSink, out error))
        {
            throw new ArgumentException($"Invalid master sink name: {error}", nameof(masterSink));
        }

        // Validate channels
        if (channels < 1 || channels > 8)
        {
            throw new ArgumentException("Channels must be between 1 and 8.", nameof(channels));
        }

        // Validate channel maps (basic validation - PulseAudio will do full validation)
        if (string.IsNullOrWhiteSpace(channelMap))
        {
            throw new ArgumentException("Channel map is required.", nameof(channelMap));
        }

        if (string.IsNullOrWhiteSpace(masterChannelMap))
        {
            throw new ArgumentException("Master channel map is required.", nameof(masterChannelMap));
        }

        // Build module arguments
        var args = new List<string>
        {
            "load-module",
            "module-remap-sink",
            $"sink_name={sinkName}",
            $"master={masterSink}",
            $"channels={channels}",
            $"channel_map={channelMap}",
            $"master_channel_map={masterChannelMap}",
            // PulseAudio remix parameter: true = allow channel remixing, false = no remixing
            // Pass the value directly - when user sets remix=false, we send remix=false to PulseAudio
            $"remix={remix.ToString().ToLowerInvariant()}"
        };

        // Add description if provided
        if (!string.IsNullOrWhiteSpace(description))
        {
            var safeDesc = SanitizeDescription(description);
            // No quotes needed - spaces are replaced with underscores due to PulseAudio bug
            args.Add($"sink_properties=device.description={safeDesc}");
        }

        _logger.LogInformation("Loading remap-sink '{SinkName}' from master '{Master}' with {Channels} channels. Args: {Args}",
            sinkName, masterSink, channels, string.Join(" | ", args));

        var result = await RunPactlAsync(args.ToArray(), cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogError("Failed to load remap-sink '{SinkName}': {Error}", sinkName, result.Error);
            return null;
        }

        // Parse module index from output
        if (int.TryParse(result.Output.Trim(), out var moduleIndex))
        {
            _logger.LogInformation("Successfully loaded remap-sink '{SinkName}' with module index {Index}", sinkName, moduleIndex);
            return moduleIndex;
        }

        _logger.LogWarning("Loaded remap-sink '{SinkName}' but could not parse module index from: {Output}", sinkName, result.Output);
        return null;
    }

    /// <summary>
    /// Unload a PulseAudio module by its index.
    /// </summary>
    /// <param name="moduleIndex">The module index to unload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successfully unloaded.</returns>
    public async Task<bool> UnloadModuleAsync(int moduleIndex, CancellationToken cancellationToken = default)
    {
        if (moduleIndex < 0)
        {
            _logger.LogWarning("Invalid module index: {Index}", moduleIndex);
            return false;
        }

        _logger.LogInformation("Unloading module {Index}", moduleIndex);

        var result = await RunPactlAsync(["unload-module", moduleIndex.ToString()], cancellationToken);

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Successfully unloaded module {Index}", moduleIndex);
            return true;
        }

        _logger.LogError("Failed to unload module {Index}: {Error}", moduleIndex, result.Error);
        return false;
    }

    /// <summary>
    /// Check if a specific module is currently loaded.
    /// </summary>
    /// <param name="moduleIndex">The module index to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the module is loaded.</returns>
    public async Task<bool> IsModuleLoadedAsync(int moduleIndex, CancellationToken cancellationToken = default)
    {
        var modules = await ListModulesAsync(cancellationToken);
        return modules.Any(m => m.Index == moduleIndex);
    }

    /// <summary>
    /// List all loaded PulseAudio modules.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of loaded modules.</returns>
    public async Task<IReadOnlyList<PaModule>> ListModulesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunPactlAsync(["list", "modules", "short"], cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning("Failed to list modules: {Error}", result.Error);
            return [];
        }

        var modules = new List<PaModule>();
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Format: index\tmodule_name\targuments
            var parts = line.Split('\t');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var index))
            {
                var name = parts[1];
                var args = parts.Length >= 3 ? parts[2] : "";
                modules.Add(new PaModule(index, name, args));
            }
        }

        return modules;
    }

    /// <summary>
    /// Check if a sink with the given name exists in PulseAudio.
    /// </summary>
    /// <param name="sinkName">The sink name to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the sink exists.</returns>
    public async Task<bool> SinkExistsAsync(string sinkName, CancellationToken cancellationToken = default)
    {
        if (!ValidateName(sinkName, out _))
            return false;

        var result = await RunPactlAsync(["list", "sinks", "short"], cancellationToken);

        if (result.ExitCode != 0)
            return false;

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2 && parts[1].Equals(sinkName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunPactlAsync(
        string[] arguments,
        CancellationToken cancellationToken)
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

            _logger.LogDebug("pactl {Args} -> exit {ExitCode}", argsForLogging, process.ExitCode);

            if (!string.IsNullOrWhiteSpace(error) && process.ExitCode != 0)
            {
                _logger.LogDebug("pactl stderr: {Error}", error);
            }

            return (process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "pactl command failed: {Args}", argsForLogging);
            return (-1, string.Empty, ex.Message);
        }
    }
}

/// <summary>
/// Represents a loaded PulseAudio module.
/// </summary>
public record PaModule(int Index, string Name, string Arguments);
