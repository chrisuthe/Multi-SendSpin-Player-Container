using System.Text.RegularExpressions;
using MultiRoomAudio.Models;

namespace MultiRoomAudio.Utilities;

/// <summary>
/// Parses and modifies /etc/pulse/default.pa for sink import functionality.
/// Thread-safe: uses locking for all file operations.
/// </summary>
public partial class DefaultPaParser
{
    private readonly ILogger<DefaultPaParser> _logger;
    private readonly string _defaultPaPath;
    private readonly object _fileLock = new();

    /// <summary>
    /// Regex pattern to match load-module lines for combine or remap sinks.
    /// Captures: (combine|remap) and the arguments.
    /// </summary>
    [GeneratedRegex(@"^\s*load-module\s+module-(combine|remap)-sink\s+(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LoadModulePattern();

    /// <summary>
    /// Regex pattern to extract key=value pairs from module arguments.
    /// Handles quoted values for properties like device.description="My Sink"
    /// </summary>
    [GeneratedRegex(@"(\w+)=(?:""([^""]*)""|([^\s]+))", RegexOptions.Compiled)]
    private static partial Regex KeyValuePattern();

    /// <summary>
    /// Marker comment added when we comment out a line.
    /// </summary>
    private const string CommentMarker = "# [MRA-IMPORTED] ";

    /// <summary>
    /// Backup file extension for safety before modifications.
    /// </summary>
    private const string BackupExtension = ".mra-backup";

    public DefaultPaParser(ILogger<DefaultPaParser> logger, string? defaultPaPath = null)
    {
        _logger = logger;
        _defaultPaPath = defaultPaPath ?? "/etc/pulse/default.pa";
    }

    /// <summary>
    /// Scan default.pa for combine-sink and remap-sink module load lines.
    /// </summary>
    /// <returns>List of detected sinks with their configuration.</returns>
    public List<DetectedSink> ScanForSinks()
    {
        var detected = new List<DetectedSink>();

        lock (_fileLock)
        {
            if (!File.Exists(_defaultPaPath))
            {
                _logger.LogDebug("default.pa not found at {Path}", _defaultPaPath);
                return detected;
            }

            try
            {
                var lines = File.ReadAllLines(_defaultPaPath);

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var lineNumber = i + 1; // 1-based line numbers

                    // Skip already commented lines
                    if (line.TrimStart().StartsWith('#'))
                        continue;

                    // Handle line continuations (\) - track both start and end line numbers
                    var fullLine = line;
                    var startLine = lineNumber;
                    while (fullLine.TrimEnd().EndsWith('\\') && i + 1 < lines.Length)
                    {
                        fullLine = fullLine.TrimEnd().TrimEnd('\\') + " " + lines[++i].Trim();
                    }
                    // End line is current position + 1 (1-based)
                    var endLine = i + 1;

                    var match = LoadModulePattern().Match(fullLine);
                    if (!match.Success)
                        continue;

                    var moduleType = match.Groups[1].Value.ToLowerInvariant();
                    var arguments = match.Groups[2].Value;

                    var sink = ParseModuleArguments(moduleType, arguments, startLine, endLine, fullLine);
                    if (sink != null)
                    {
                        detected.Add(sink);
                        if (startLine == endLine)
                        {
                            _logger.LogDebug("Found {Type}-sink '{Name}' at line {Line}", moduleType, sink.SinkName, startLine);
                        }
                        else
                        {
                            _logger.LogDebug("Found {Type}-sink '{Name}' at lines {StartLine}-{EndLine}", moduleType, sink.SinkName, startLine, endLine);
                        }
                    }
                }

                _logger.LogInformation("Scanned default.pa: found {Count} importable sinks", detected.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan default.pa at {Path}", _defaultPaPath);
            }
        }

        return detected;
    }

    private DetectedSink? ParseModuleArguments(string moduleType, string arguments, int startLine, int endLine, string rawLine)
    {
        var keyValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in KeyValuePattern().Matches(arguments))
        {
            var key = match.Groups[1].Value;
            // Use quoted value if present, otherwise use unquoted
            var value = !string.IsNullOrEmpty(match.Groups[2].Value)
                ? match.Groups[2].Value
                : match.Groups[3].Value;
            keyValues[key] = value;
        }

        // Extract sink name (required)
        if (!keyValues.TryGetValue("sink_name", out var sinkName) || string.IsNullOrWhiteSpace(sinkName))
        {
            _logger.LogDebug("Skipping line {Line}: no sink_name found", startLine);
            return null;
        }

        // Extract description from sink_properties
        string? description = null;
        if (keyValues.TryGetValue("sink_properties", out var properties))
        {
            var descMatch = Regex.Match(properties, @"device\.description=""?([^""]+)""?");
            if (descMatch.Success)
            {
                description = descMatch.Groups[1].Value;
            }
        }

        var type = moduleType == "combine" ? CustomSinkType.Combine : CustomSinkType.Remap;

        // Parse type-specific properties
        List<string>? slaves = null;
        string? masterSink = null;
        int? channels = null;
        string? channelMap = null;
        string? masterChannelMap = null;
        bool? remix = null;

        if (type == CustomSinkType.Combine)
        {
            if (keyValues.TryGetValue("slaves", out var slavesValue))
            {
                slaves = slavesValue.Split(',').Select(s => s.Trim()).ToList();
            }
        }
        else // Remap
        {
            keyValues.TryGetValue("master", out masterSink);

            if (keyValues.TryGetValue("channels", out var channelsValue) && int.TryParse(channelsValue, out var ch))
            {
                channels = ch;
            }

            keyValues.TryGetValue("channel_map", out channelMap);
            keyValues.TryGetValue("master_channel_map", out masterChannelMap);

            if (keyValues.TryGetValue("remix", out var remixValue))
            {
                remix = remixValue.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                        remixValue.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return new DetectedSink(
            LineNumber: startLine,
            EndLineNumber: endLine,
            RawLine: rawLine,
            Type: type,
            SinkName: sinkName,
            Description: description,
            Slaves: slaves,
            MasterSink: masterSink,
            Channels: channels,
            ChannelMap: channelMap,
            MasterChannelMap: masterChannelMap,
            Remix: remix
        );
    }

    /// <summary>
    /// Comment out a range of lines in default.pa by prepending our marker.
    /// Creates a backup file on first modification for recovery purposes.
    /// Thread-safe: uses file lock to prevent concurrent modifications.
    /// </summary>
    /// <param name="startLine">1-based line number where the entry starts.</param>
    /// <param name="endLine">1-based line number where the entry ends (inclusive).</param>
    /// <returns>True if successfully commented out.</returns>
    public bool CommentOutLines(int startLine, int endLine)
    {
        lock (_fileLock)
        {
            if (!File.Exists(_defaultPaPath))
            {
                _logger.LogWarning("Cannot comment out lines: default.pa not found at {Path}", _defaultPaPath);
                return false;
            }

            try
            {
                var lines = File.ReadAllLines(_defaultPaPath).ToList();
                var startIndex = startLine - 1; // Convert to 0-based
                var endIndex = endLine - 1;

                if (startIndex < 0 || endIndex >= lines.Count || startIndex > endIndex)
                {
                    _logger.LogWarning("Line range {Start}-{End} is invalid (file has {Total} lines)", startLine, endLine, lines.Count);
                    return false;
                }

                // Check if first line is already commented by us (assume all are if first is)
                if (lines[startIndex].StartsWith(CommentMarker))
                {
                    _logger.LogDebug("Lines {Start}-{End} are already commented out by us", startLine, endLine);
                    return true;
                }

                // Create backup on first modification (don't overwrite existing backup)
                EnsureBackupExistsUnsafe();

                // Comment out all lines in the range
                for (int i = startIndex; i <= endIndex; i++)
                {
                    if (!lines[i].StartsWith(CommentMarker))
                    {
                        lines[i] = CommentMarker + lines[i];
                    }
                }

                File.WriteAllLines(_defaultPaPath, lines);
                if (startLine == endLine)
                {
                    _logger.LogInformation("Commented out line {Line} in default.pa", startLine);
                }
                else
                {
                    _logger.LogInformation("Commented out lines {Start}-{End} in default.pa", startLine, endLine);
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Permission denied: cannot modify default.pa at {Path}", _defaultPaPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to comment out lines {Start}-{End} in default.pa", startLine, endLine);
                return false;
            }
        }
    }

    /// <summary>
    /// Restore a line that was previously commented out by us.
    /// Thread-safe: uses file lock to prevent concurrent modifications.
    /// </summary>
    /// <param name="lineNumber">1-based line number to restore.</param>
    /// <returns>True if successfully restored.</returns>
    public bool UncommentLine(int lineNumber)
    {
        lock (_fileLock)
        {
            if (!File.Exists(_defaultPaPath))
            {
                _logger.LogWarning("Cannot uncomment line: default.pa not found at {Path}", _defaultPaPath);
                return false;
            }

            try
            {
                var lines = File.ReadAllLines(_defaultPaPath).ToList();
                var index = lineNumber - 1;

                if (index < 0 || index >= lines.Count)
                {
                    _logger.LogWarning("Line number {Line} is out of range", lineNumber);
                    return false;
                }

                // Only remove our marker, not other comments
                if (!lines[index].StartsWith(CommentMarker))
                {
                    _logger.LogDebug("Line {Line} was not commented out by us", lineNumber);
                    return false;
                }

                // Create backup on first modification (don't overwrite existing backup)
                EnsureBackupExistsUnsafe();

                lines[index] = lines[index][CommentMarker.Length..];

                File.WriteAllLines(_defaultPaPath, lines);
                _logger.LogInformation("Restored line {Line} in default.pa", lineNumber);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("Permission denied: cannot modify default.pa at {Path}", _defaultPaPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to uncomment line {Line} in default.pa", lineNumber);
                return false;
            }
        }
    }

    /// <summary>
    /// Check if the default.pa file exists and is readable.
    /// </summary>
    public bool IsAvailable()
    {
        return File.Exists(_defaultPaPath);
    }

    /// <summary>
    /// Check if we have write permission to default.pa.
    /// </summary>
    public bool IsWritable()
    {
        if (!File.Exists(_defaultPaPath))
            return false;

        try
        {
            using var stream = File.Open(_defaultPaPath, FileMode.Open, FileAccess.Write, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a backup of default.pa if one doesn't already exist.
    /// The backup is only created once to preserve the original state before any modifications.
    /// Must be called while holding _fileLock.
    /// </summary>
    private void EnsureBackupExistsUnsafe()
    {
        var backupPath = _defaultPaPath + BackupExtension;
        if (File.Exists(backupPath))
        {
            // Backup already exists - don't overwrite it so we preserve the original state
            return;
        }

        try
        {
            File.Copy(_defaultPaPath, backupPath);
            _logger.LogInformation("Created backup of default.pa at {BackupPath}", backupPath);
        }
        catch (Exception ex)
        {
            // Log but don't fail - backup is a safety feature, not required
            _logger.LogWarning(ex, "Could not create backup of default.pa");
        }
    }
}

/// <summary>
/// Represents a sink detected in default.pa.
/// </summary>
/// <param name="LineNumber">1-based line number where the entry starts.</param>
/// <param name="EndLineNumber">1-based line number where the entry ends (same as LineNumber for single-line entries).</param>
/// <param name="RawLine">The complete line content (with continuations merged).</param>
/// <param name="Type">Type of sink (Combine or Remap).</param>
/// <param name="SinkName">The sink_name parameter value.</param>
/// <param name="Description">Optional description from sink_properties.</param>
/// <param name="Slaves">List of slave sinks (for combine-sink).</param>
/// <param name="MasterSink">Master sink name (for remap-sink).</param>
/// <param name="Channels">Number of channels (for remap-sink).</param>
/// <param name="ChannelMap">Output channel map (for remap-sink).</param>
/// <param name="MasterChannelMap">Master channel map (for remap-sink).</param>
/// <param name="Remix">Remix setting (for remap-sink).</param>
public record DetectedSink(
    int LineNumber,
    int EndLineNumber,
    string RawLine,
    CustomSinkType Type,
    string SinkName,
    string? Description,
    // Combine-specific
    List<string>? Slaves,
    // Remap-specific
    string? MasterSink,
    int? Channels,
    string? ChannelMap,
    string? MasterChannelMap,
    bool? Remix
);
