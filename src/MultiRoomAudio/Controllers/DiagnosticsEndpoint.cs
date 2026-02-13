using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MultiRoomAudio.Audio.PulseAudio;
using MultiRoomAudio.Models;
using MultiRoomAudio.Services;
using Sendspin.SDK.Audio;

namespace MultiRoomAudio.Controllers;

/// <summary>
/// REST API endpoints for system diagnostics download.
/// </summary>
public static class DiagnosticsEndpoint
{
    // Regex pattern for IP address redaction
    private static readonly Regex IpAddressPattern = new(
        @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Registers diagnostics API endpoints with the application.
    /// </summary>
    /// <param name="app">The WebApplication to register endpoints on.</param>
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/diagnostics")
            .WithTags("Diagnostics")
            .WithOpenApi();

        // Phase definitions for progress tracking
        var phases = new[]
        {
            (Id: "summary", Name: "Summary"),
            (Id: "host", Name: "Host/Environment Info"),
            (Id: "pulseaudio", Name: "PulseAudio Info"),
            (Id: "usb", Name: "USB Devices"),
            (Id: "haos", Name: "Home Assistant Info"),
            (Id: "state", Name: "Application State"),
            (Id: "players", Name: "Player States"),
            (Id: "devices", Name: "Device Info"),
            (Id: "relays", Name: "Relay Board Info"),
            (Id: "configs", Name: "Config Files")
        };

        // GET /api/diagnostics/stream - SSE endpoint for progress + content
        group.MapGet("/stream", async (
            PlayerManagerService playerManager,
            EnvironmentService environment,
            TriggerService triggerService,
            CustomSinksService sinksService,
            HttpContext context,
            CancellationToken ct) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var sb = new StringBuilder();

            async Task SendProgressAsync(int current, int total, string phaseName)
            {
                ct.ThrowIfCancellationRequested();
                var json = System.Text.Json.JsonSerializer.Serialize(new { current, total, phase = phaseName });
                await context.Response.WriteAsync($"event: progress\ndata: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }

            async Task SendCompleteAsync(string content)
            {
                ct.ThrowIfCancellationRequested();
                // Send content as base64 to avoid SSE parsing issues
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
                await context.Response.WriteAsync($"event: complete\ndata: {base64}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }

            try
            {
                int phaseNum = 0;
                int totalPhases = phases.Length;

                // Phase 1: Summary
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[0].Name);
                AppendSummary(sb, playerManager, environment, triggerService, sinksService);

                // Phase 2: Host/Environment
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[1].Name);
                sb.AppendLine("=== HOST / ENVIRONMENT INFO ===");
                sb.AppendLine();
                await AppendHostInfoAsync(sb, ct);

                // Phase 3: PulseAudio
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[2].Name);
                await AppendPulseAudioInfoAsync(sb, ct);

                // Phase 4: USB
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[3].Name);
                await AppendUsbInfoAsync(sb, ct);

                // Phase 5: HAOS (conditional)
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[4].Name);
                await AppendHaosInfoAsync(sb, environment, ct);

                // Phase 6: Application State
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[5].Name);
                AppendApplicationState(sb, environment);

                // Phase 7: Players
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[6].Name);
                AppendPlayerStates(sb, playerManager);

                // Phase 8: Devices
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[7].Name);
                AppendDeviceInfo(sb);

                // Phase 9: Relays
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[8].Name);
                AppendRelayBoardInfo(sb, triggerService);

                // Phase 10: Configs
                phaseNum++;
                await SendProgressAsync(phaseNum, totalPhases, phases[9].Name);
                await AppendConfigFilesAsync(sb, environment, ct);

                // Send complete with content
                await SendCompleteAsync(sb.ToString());
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or cancelled - this is expected, just return
            }
            catch (Exception ex)
            {
                try
                {
                    await context.Response.WriteAsync($"event: error\ndata: {ex.Message}\n\n");
                    await context.Response.Body.FlushAsync();
                }
                catch { /* Client may have disconnected */ }
            }
        })
        .WithName("StreamDiagnostics")
        .WithDescription("Stream diagnostics generation progress via SSE");

        // GET /api/diagnostics/download - Download comprehensive diagnostics file (direct, no progress)
        group.MapGet("/download", async (
            PlayerManagerService playerManager,
            EnvironmentService environment,
            TriggerService triggerService,
            CustomSinksService sinksService,
            HttpContext context) =>
        {
            var sb = new StringBuilder();

            // === SUMMARY SECTION ===
            AppendSummary(sb, playerManager, environment, triggerService, sinksService);

            // === SYSTEM COMMANDS SECTION ===
            await AppendSystemCommandsAsync(sb);

            // === APPLICATION STATE SECTION ===
            AppendApplicationState(sb, environment);

            // === HAOS INFO SECTION (only in HAOS mode) ===
            await AppendHaosInfoAsync(sb, environment);

            // === PLAYER STATES SECTION ===
            AppendPlayerStates(sb, playerManager);

            // === DEVICE INFO SECTION ===
            AppendDeviceInfo(sb);

            // === RELAY BOARD INFO SECTION ===
            AppendRelayBoardInfo(sb, triggerService);

            // === CONFIG FILES SECTION ===
            await AppendConfigFilesAsync(sb, environment);

            // Set response headers for file download
            var filename = $"multiroom-diagnostics-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.txt";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";

            return Results.Text(sb.ToString(), "text/plain");
        })
        .WithName("DownloadDiagnostics")
        .WithDescription("Download comprehensive system diagnostics file for troubleshooting");
    }

    // Split out host info for streaming
    private static async Task AppendHostInfoAsync(StringBuilder sb, CancellationToken ct = default)
    {
        // Kernel info (shared with host)
        sb.AppendLine("--- uname -a ---");
        var uname = await RunCommandAsync("uname", "-a", ct);
        sb.AppendLine(uname);
        sb.AppendLine();

        // CPU info including virtualization detection
        sb.AppendLine("--- lscpu (summary) ---");
        var lscpu = await RunCommandAsync("lscpu", "", ct);
        if (!lscpu.StartsWith("("))
        {
            var interestingKeys = new[] { "Architecture", "CPU(s)", "Model name", "Hypervisor", "Virtualization" };
            var lines = lscpu.Split('\n')
                .Where(line => interestingKeys.Any(key => line.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            sb.AppendLine(lines.Count > 0 ? string.Join("\n", lines) : lscpu);
        }
        else
        {
            sb.AppendLine(lscpu);
        }
        sb.AppendLine();

        // Memory info
        sb.AppendLine("--- Memory ---");
        var meminfo = await RunCommandAsync("sh", "-c \"grep -E '^(MemTotal|MemFree|MemAvailable):' /proc/meminfo\"", ct);
        sb.AppendLine(meminfo);
        sb.AppendLine();

        // Virtualization detection
        sb.AppendLine("--- Virtualization ---");
        var virt = await RunCommandAsync("systemd-detect-virt", "", ct);
        sb.AppendLine($"systemd-detect-virt: {virt}");
        sb.AppendLine();

        // Container OS
        sb.AppendLine("--- Container OS ---");
        var osRelease = await RunCommandAsync("sh", "-c \"grep -E '^(PRETTY_NAME|ID|VERSION_ID)=' /etc/os-release\"", ct);
        sb.AppendLine(osRelease);
        sb.AppendLine();
    }

    private static async Task AppendPulseAudioInfoAsync(StringBuilder sb, CancellationToken ct = default)
    {
        sb.AppendLine("=== PULSEAUDIO INFO ===");
        sb.AppendLine();

        sb.AppendLine("--- pactl info ---");
        var pactlInfo = await RunCommandAsync("pactl", "info", ct);
        sb.AppendLine(RedactSensitiveData(pactlInfo));
        sb.AppendLine();

        sb.AppendLine("--- pactl list cards ---");
        var pactlCards = await RunCommandAsync("pactl", "list cards", ct);
        sb.AppendLine(RedactSensitiveData(pactlCards));
        sb.AppendLine();

        sb.AppendLine("--- pactl list sinks ---");
        var pactlSinks = await RunCommandAsync("pactl", "list sinks", ct);
        sb.AppendLine(RedactSensitiveData(pactlSinks));
        sb.AppendLine();

        sb.AppendLine("--- pactl list modules ---");
        var pactlModules = await RunCommandAsync("pactl", "list modules", ct);
        sb.AppendLine(RedactSensitiveData(pactlModules));
        sb.AppendLine();
    }

    private static async Task AppendUsbInfoAsync(StringBuilder sb, CancellationToken ct = default)
    {
        sb.AppendLine("=== USB DEVICES ===");
        sb.AppendLine();

        sb.AppendLine("--- lsusb ---");
        var lsusb = await RunCommandAsync("lsusb", "", ct);
        sb.AppendLine(RedactSensitiveData(lsusb));
        sb.AppendLine();
    }

    private static void AppendSummary(
        StringBuilder sb,
        PlayerManagerService playerManager,
        EnvironmentService environment,
        TriggerService triggerService,
        CustomSinksService sinksService)
    {
        sb.AppendLine("================================================================================");
        sb.AppendLine("                    MULTI-ROOM AUDIO DIAGNOSTICS REPORT");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        // System Info
        sb.AppendLine("--- SYSTEM INFO ---");
        sb.AppendLine($"App Version:    {GetVersion()}");
        sb.AppendLine($"Build SHA:      {GetBuildSha()}");
        sb.AppendLine($"Build Date:     {GetBuildDate()}");
        sb.AppendLine($"SDK Version:    {GetSdkVersion()}");
        sb.AppendLine($"Running Mode:   {GetRunningMode(environment)}");
        sb.AppendLine($"Uptime:         {GetUptime()}");
        sb.AppendLine();

        // Quick Device Summary (hardware devices only, excludes custom sinks)
        sb.AppendLine("--- AUDIO DEVICES ---");
        try
        {
            var allDevices = PulseAudioDeviceEnumerator.GetOutputDevices().ToList();
            var customSinkNames = sinksService.GetAllSinks().Sinks.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var devices = allDevices.Where(d => !customSinkNames.Contains(d.Name)).ToList();
            var players = playerManager.GetAllPlayers().Players;

            foreach (var device in devices)
            {
                var assignedPlayer = players.FirstOrDefault(p => p.Device == device.Id);
                var playerInfo = assignedPlayer != null
                    ? $" -> {assignedPlayer.Name} ({assignedPlayer.State})"
                    : "";
                var status = device.IsDefault ? "[DEFAULT]" : "";
                sb.AppendLine($"  {device.Name} {status}{playerInfo}");
            }

            if (devices.Count == 0)
            {
                sb.AppendLine("  (no audio devices found)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error enumerating devices: {ex.Message})");
        }
        sb.AppendLine();

        // Relay Boards Summary
        sb.AppendLine("--- RELAY BOARDS ---");
        try
        {
            var status = triggerService.GetStatus();
            if (status.Enabled && status.Boards.Count > 0)
            {
                foreach (var board in status.Boards)
                {
                    var channelsInUse = board.Triggers.Count(t => t.CustomSinkName != null);
                    sb.AppendLine($"  {board.BoardId} - {board.BoardType} - {board.State} - {channelsInUse}/{board.ChannelCount} channels");
                }
            }
            else if (!status.Enabled)
            {
                sb.AppendLine("  (trigger feature disabled)");
            }
            else
            {
                sb.AppendLine("  (no relay boards configured)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error getting relay board info: {ex.Message})");
        }
        sb.AppendLine();

        // Totals
        sb.AppendLine("--- TOTALS ---");
        try
        {
            var allDevices = PulseAudioDeviceEnumerator.GetOutputDevices().ToList();
            var players = playerManager.GetAllPlayers();
            var sinks = sinksService.GetAllSinks();
            var triggerStatus = triggerService.GetStatus();

            // Filter out custom sinks from hardware device count
            var customSinkNames = sinks.Sinks.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hardwareDeviceCount = allDevices.Count(d => !customSinkNames.Contains(d.Name));

            var playersWithDevices = players.Players.Count(p => !string.IsNullOrEmpty(p.Device));
            var playingCount = players.Players.Count(p => p.State == PlayerState.Playing);
            var connectedBoards = triggerStatus.Boards.Count(b => b.State == TriggerFeatureState.Connected);
            var assignedChannels = triggerStatus.Boards.SelectMany(b => b.Triggers).Count(t => t.CustomSinkName != null);

            sb.AppendLine($"  Audio Devices:  {hardwareDeviceCount} total, {playersWithDevices} with players");
            sb.AppendLine($"  Custom Sinks:   {sinks.Count}");
            sb.AppendLine($"  Players:        {players.Count} total, {playingCount} playing");
            sb.AppendLine($"  Relay Boards:   {connectedBoards} connected, {assignedChannels} channels assigned");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error getting totals: {ex.Message})");
        }
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine();
    }

    private static async Task AppendSystemCommandsAsync(StringBuilder sb)
    {
        sb.AppendLine("=== HOST / ENVIRONMENT INFO ===");
        sb.AppendLine();

        // Kernel info (shared with host)
        sb.AppendLine("--- uname -a ---");
        var uname = await RunCommandAsync("uname", "-a");
        sb.AppendLine(uname);
        sb.AppendLine();

        // CPU info including virtualization detection
        sb.AppendLine("--- lscpu (summary) ---");
        var lscpu = await RunCommandAsync("lscpu", "");
        // Filter to just the interesting lines
        if (!lscpu.StartsWith("("))
        {
            var interestingKeys = new[] { "Architecture", "CPU(s)", "Model name", "Hypervisor", "Virtualization" };
            var lines = lscpu.Split('\n')
                .Where(line => interestingKeys.Any(key => line.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            sb.AppendLine(lines.Count > 0 ? string.Join("\n", lines) : lscpu);
        }
        else
        {
            sb.AppendLine(lscpu);
        }
        sb.AppendLine();

        // Memory info
        sb.AppendLine("--- Memory ---");
        var meminfo = await RunCommandAsync("sh", "-c \"grep -E '^(MemTotal|MemFree|MemAvailable):' /proc/meminfo\"");
        sb.AppendLine(meminfo);
        sb.AppendLine();

        // Virtualization detection
        sb.AppendLine("--- Virtualization ---");
        var virt = await RunCommandAsync("systemd-detect-virt", "");
        sb.AppendLine($"systemd-detect-virt: {virt}");
        sb.AppendLine();

        // Container OS
        sb.AppendLine("--- Container OS ---");
        var osRelease = await RunCommandAsync("sh", "-c \"grep -E '^(PRETTY_NAME|ID|VERSION_ID)=' /etc/os-release\"");
        sb.AppendLine(osRelease);
        sb.AppendLine();

        sb.AppendLine("=== PULSEAUDIO INFO ===");
        sb.AppendLine();

        // pactl info
        sb.AppendLine("--- pactl info ---");
        var pactlInfo = await RunCommandAsync("pactl", "info");
        sb.AppendLine(RedactSensitiveData(pactlInfo));
        sb.AppendLine();

        // pactl list cards
        sb.AppendLine("--- pactl list cards ---");
        var pactlCards = await RunCommandAsync("pactl", "list cards");
        sb.AppendLine(RedactSensitiveData(pactlCards));
        sb.AppendLine();

        // pactl list sinks
        sb.AppendLine("--- pactl list sinks ---");
        var pactlSinks = await RunCommandAsync("pactl", "list sinks");
        sb.AppendLine(RedactSensitiveData(pactlSinks));
        sb.AppendLine();

        // pactl list modules
        sb.AppendLine("--- pactl list modules ---");
        var pactlModules = await RunCommandAsync("pactl", "list modules");
        sb.AppendLine(RedactSensitiveData(pactlModules));
        sb.AppendLine();

        sb.AppendLine("=== USB DEVICES ===");
        sb.AppendLine();

        // lsusb
        sb.AppendLine("--- lsusb ---");
        var lsusb = await RunCommandAsync("lsusb", "");
        sb.AppendLine(RedactSensitiveData(lsusb));
        sb.AppendLine();
    }

    private static async Task AppendHaosInfoAsync(StringBuilder sb, EnvironmentService environment, CancellationToken ct = default)
    {
        if (!environment.IsHaos)
        {
            return;
        }

        sb.AppendLine("=== HOME ASSISTANT INFO ===");
        sb.AppendLine();

        var token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            sb.AppendLine("(SUPERVISOR_TOKEN not available)");
            sb.AppendLine();
            return;
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        // Supervisor info
        sb.AppendLine("--- Supervisor Info ---");
        var supervisorInfo = await FetchSupervisorApiAsync(httpClient, "/supervisor/info", ct);
        sb.AppendLine(RedactSensitiveData(supervisorInfo));
        sb.AppendLine();

        // Host info
        sb.AppendLine("--- Host Info ---");
        var hostInfo = await FetchSupervisorApiAsync(httpClient, "/host/info", ct);
        sb.AppendLine(RedactSensitiveData(hostInfo));
        sb.AppendLine();

        // Core info
        sb.AppendLine("--- Core Info ---");
        var coreInfo = await FetchSupervisorApiAsync(httpClient, "/core/info", ct);
        sb.AppendLine(RedactSensitiveData(coreInfo));
        sb.AppendLine();

        // Audio info
        sb.AppendLine("--- Audio Info ---");
        var audioInfo = await FetchSupervisorApiAsync(httpClient, "/audio/info", ct);
        sb.AppendLine(RedactSensitiveData(audioInfo));
        sb.AppendLine();
    }

    private static async Task<string> FetchSupervisorApiAsync(HttpClient httpClient, string endpoint, CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"http://supervisor{endpoint}", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                // Pretty print the JSON for readability
                try
                {
                    var parsed = System.Text.Json.JsonDocument.Parse(json);
                    return System.Text.Json.JsonSerializer.Serialize(parsed, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    return json;
                }
            }
            return $"(HTTP {(int)response.StatusCode}: {response.ReasonPhrase})";
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    private static void AppendApplicationState(StringBuilder sb, EnvironmentService environment)
    {
        sb.AppendLine("=== APPLICATION STATE ===");
        sb.AppendLine();

        sb.AppendLine($"Environment:     {environment.EnvironmentName}");
        sb.AppendLine($"Is HAOS:         {environment.IsHaos}");
        sb.AppendLine($"Mock Hardware:   {environment.IsMockHardware}");
        sb.AppendLine($"Advanced Formats:{environment.EnableAdvancedFormats}");
        sb.AppendLine($"Audio Backend:   {environment.AudioBackend}");
        sb.AppendLine($"Config Path:     {environment.ConfigPath}");
        sb.AppendLine($"Log Path:        {environment.LogPath}");
        sb.AppendLine();
    }

    private static void AppendPlayerStates(StringBuilder sb, PlayerManagerService playerManager)
    {
        sb.AppendLine("=== PLAYER STATES ===");
        sb.AppendLine();

        try
        {
            var players = playerManager.GetAllPlayers();

            if (players.Count == 0)
            {
                sb.AppendLine("(no players configured)");
                sb.AppendLine();
                return;
            }

            foreach (var player in players.Players)
            {
                sb.AppendLine($"Player: {player.Name}");
                sb.AppendLine($"  State:         {player.State}");
                sb.AppendLine($"  Device:        {player.Device ?? "(none)"}");
                sb.AppendLine($"  Volume:        {player.Volume}%");
                sb.AppendLine($"  Muted:         {player.IsMuted}");
                sb.AppendLine($"  Auto-Resume:   {player.AutoResume}");
                sb.AppendLine($"  Server:        {RedactSensitiveData(player.ServerUrl ?? "(none)")}");

                // Get sync stats if available
                var stats = playerManager.GetPlayerStats(player.Name);
                if (stats != null)
                {
                    // Sync stats
                    sb.AppendLine($"  Sync Error:    {stats.Sync.SyncErrorMs:F1} ms");
                    sb.AppendLine($"  In Tolerance:  {stats.Sync.IsWithinTolerance}");
                    sb.AppendLine($"  Buffer Level:  {stats.Buffer.BufferedMs} ms");
                    sb.AppendLine($"  Underruns:     {stats.Buffer.Underruns}");
                    sb.AppendLine($"  Overruns:      {stats.Buffer.Overruns}");

                    // Clock sync stats
                    sb.AppendLine($"  Clock Synced:  {stats.ClockSync.IsSynchronized}");
                    sb.AppendLine($"  Clock Offset:  {stats.ClockSync.ClockOffsetMs:F2} ms");
                    sb.AppendLine($"  Uncertainty:   {stats.ClockSync.UncertaintyMs:F2} ms");
                    sb.AppendLine($"  Drift Rate:    {stats.ClockSync.DriftRatePpm:F2} ppm (reliable: {stats.ClockSync.IsDriftReliable})");
                    sb.AppendLine($"  Timing Source: {stats.ClockSync.TimingSource}");
                    sb.AppendLine($"  Output Latency:{stats.ClockSync.OutputLatencyMs} ms");
                }

                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(error getting player states: {ex.Message})");
            sb.AppendLine();
        }
    }

    private static void AppendDeviceInfo(StringBuilder sb)
    {
        sb.AppendLine("=== DEVICE INFO ===");
        sb.AppendLine();

        try
        {
            var devices = PulseAudioDeviceEnumerator.GetOutputDevices().ToList();

            if (devices.Count == 0)
            {
                sb.AppendLine("(no audio devices found)");
                sb.AppendLine();
                return;
            }

            foreach (var device in devices)
            {
                sb.AppendLine($"Device: {device.Name}");
                sb.AppendLine($"  ID:            {device.Id}");
                sb.AppendLine($"  Is Default:    {device.IsDefault}");
                sb.AppendLine($"  Channels:      {device.MaxChannels}");
                sb.AppendLine($"  Sample Rate:   {device.DefaultSampleRate}");
                if (device.Capabilities != null)
                {
                    var rates = device.Capabilities.SupportedSampleRates;
                    var depths = device.Capabilities.SupportedBitDepths;
                    if (rates.Length > 0)
                        sb.AppendLine($"  Sample Rates:  {string.Join(", ", rates)}");
                    if (depths.Length > 0)
                        sb.AppendLine($"  Bit Depths:    {string.Join(", ", depths)}");
                }
                if (device.Alias != null)
                    sb.AppendLine($"  Alias:         {device.Alias}");
                if (device.Hidden)
                    sb.AppendLine($"  Hidden:        true");
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(error getting device info: {ex.Message})");
            sb.AppendLine();
        }
    }

    private static void AppendRelayBoardInfo(StringBuilder sb, TriggerService triggerService)
    {
        sb.AppendLine("=== RELAY BOARD INFO ===");
        sb.AppendLine();

        try
        {
            var status = triggerService.GetStatus();

            sb.AppendLine($"Feature Enabled: {status.Enabled}");
            sb.AppendLine($"Total Channels:  {status.TotalChannels}");
            sb.AppendLine();

            if (status.Boards.Count == 0)
            {
                sb.AppendLine("(no relay boards configured)");
                sb.AppendLine();
                return;
            }

            foreach (var board in status.Boards)
            {
                sb.AppendLine($"Board: {board.BoardId}");
                sb.AppendLine($"  Type:          {board.BoardType}");
                sb.AppendLine($"  State:         {board.State}");
                sb.AppendLine($"  Channels:      {board.ChannelCount}");

                if (!string.IsNullOrEmpty(board.ErrorMessage))
                {
                    sb.AppendLine($"  Error:         {board.ErrorMessage}");
                }

                sb.AppendLine($"  Startup:       {board.StartupBehavior}");
                sb.AppendLine($"  Shutdown:      {board.ShutdownBehavior}");

                sb.AppendLine("  Channel Mappings:");
                foreach (var trigger in board.Triggers)
                {
                    var assignedTo = trigger.CustomSinkName ?? "(unassigned)";
                    var stateStr = trigger.RelayState == RelayState.On ? "ON" : "OFF";
                    sb.AppendLine($"    Ch {trigger.Channel}: {stateStr} -> {assignedTo}");
                }

                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(error getting relay board info: {ex.Message})");
            sb.AppendLine();
        }
    }

    private static async Task AppendConfigFilesAsync(StringBuilder sb, EnvironmentService environment, CancellationToken ct = default)
    {
        sb.AppendLine("=== CONFIG FILES ===");
        sb.AppendLine();

        var configFiles = new[]
        {
            ("players.yaml", environment.PlayersConfigPath),
            ("devices.yaml", environment.DevicesConfigPath),
            ("sinks.yaml", Path.Combine(environment.ConfigPath, "sinks.yaml")),
            ("card-profiles.yaml", Path.Combine(environment.ConfigPath, "card-profiles.yaml")),
            ("triggers.yaml", Path.Combine(environment.ConfigPath, "triggers.yaml"))
        };

        foreach (var (name, path) in configFiles)
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine($"--- {name} ---");

            if (File.Exists(path))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(path, ct);
                    sb.AppendLine(RedactSensitiveData(content));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"(error reading file: {ex.Message})");
                }
            }
            else
            {
                sb.AppendLine("(file not found)");
            }

            sb.AppendLine();
        }
    }

    // Default timeout for shell commands (10 seconds)
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private static async Task<string> RunCommandAsync(string command, string arguments, CancellationToken ct = default)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            // Create a combined cancellation token with timeout
            using var timeoutCts = new CancellationTokenSource(CommandTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

                await process.WaitForExitAsync(linkedCts.Token);

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
                {
                    return $"(command failed: {error.Trim()})";
                }

                return string.IsNullOrWhiteSpace(output) ? "(no output)" : output.TrimEnd();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Timeout occurred - kill the process
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return $"(command timed out after {CommandTimeout.TotalSeconds}s)";
            }
            catch (OperationCanceledException)
            {
                // User cancelled - kill the process
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw; // Re-throw to propagate cancellation
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Re-throw cancellation
        }
        catch (Exception ex)
        {
            return $"(command not available: {ex.Message})";
        }
    }

    private static string RedactSensitiveData(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Redact SUPERVISOR_TOKEN
        var result = Regex.Replace(
            input,
            @"SUPERVISOR_TOKEN[=:]\s*\S+",
            "SUPERVISOR_TOKEN=[REDACTED]",
            RegexOptions.IgnoreCase);

        // Redact IP addresses
        result = IpAddressPattern.Replace(result, "[IP_REDACTED]");

        return result;
    }

    private static string GetVersion()
    {
        var envVersion = Environment.GetEnvironmentVariable("APP_VERSION");
        if (!string.IsNullOrEmpty(envVersion) && envVersion != "dev")
        {
            return envVersion;
        }

        var assembly = typeof(DiagnosticsEndpoint).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString(3) ?? "dev";
    }

    private static string GetBuildSha()
    {
        return Environment.GetEnvironmentVariable("APP_BUILD_SHA") ?? "unknown";
    }

    private static string GetBuildDate()
    {
        return Environment.GetEnvironmentVariable("APP_BUILD_DATE") ?? "unknown";
    }

    private static string GetSdkVersion()
    {
        try
        {
            // Get SDK version from a known SDK type (works with single-file publish)
            var sdkAssembly = typeof(IAudioPipeline).Assembly;
            var version = sdkAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? sdkAssembly.GetName().Version?.ToString()
                ?? "unknown";
            // Strip source link hash if present (e.g., "6.3.4+abc123" -> "6.3.4")
            var plusIndex = version.IndexOf('+');
            return plusIndex > 0 ? version[..plusIndex] : version;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GetRunningMode(EnvironmentService environment)
    {
        if (environment.IsHaos)
            return "Home Assistant OS (Add-on)";

        // Check if running in Docker by looking for /.dockerenv or cgroup
        if (File.Exists("/.dockerenv") ||
            (File.Exists("/proc/1/cgroup") && File.ReadAllText("/proc/1/cgroup").Contains("docker")))
        {
            return "Docker (Standalone)";
        }

        return "Standalone App";
    }

    private static string GetUptime()
    {
        var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
        return uptime.ToString(@"d\.hh\:mm\:ss");
    }
}
