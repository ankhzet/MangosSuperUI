using System.ComponentModel;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Process-level control for mangosd and realmd. Delegates to ProcessManagerService,
/// which in the docker-compose stack uses Process.Kill() directly (UI shares mangosd's
/// PID namespace). For systemd hosts, ProcessManagerService falls back to the configured
/// shell command templates.
/// </summary>
[McpServerToolType]
public class ProcessTools
{
    private readonly ProcessManagerService _pm;
    private readonly ILogger<ProcessTools> _logger;

    public ProcessTools(ProcessManagerService pm, ILogger<ProcessTools> logger)
    {
        _pm = pm;
        _logger = logger;
    }

    [McpServerTool(Name = "process_status")]
    [McpCapability(McpCapability.Read)]
    [Description(
        "Return the live status of mangosd and realmd: running? PID? start time? uptime? " +
        "Also flags name-mismatch warnings (the configured process name vs. what /proc shows). " +
        "Use this before deciding to restart anything.")]
    public string Status()
    {
        var mangosd = _pm.GetMangosdStatus();
        var realmd = _pm.GetRealmdStatus();

        return McpResult.Success(new
        {
            mangosd = new
            {
                running = mangosd.IsRunning,
                pid = mangosd.Pid,
                processName = mangosd.ProcessName,
                uptimeSeconds = mangosd.Uptime.HasValue ? (long)mangosd.Uptime.Value.TotalSeconds : (long?)null,
                startTimeUtc = mangosd.StartTime?.ToString("u")
            },
            realmd = new
            {
                running = realmd.IsRunning,
                pid = realmd.Pid,
                processName = realmd.ProcessName,
                uptimeSeconds = realmd.Uptime.HasValue ? (long)realmd.Uptime.Value.TotalSeconds : (long?)null,
                startTimeUtc = realmd.StartTime?.ToString("u")
            }
        }).ToJson();
    }

    [McpServerTool(Name = "process_diagnostics")]
    [McpCapability(McpCapability.Read)]
    [Description(
        "Detailed diagnostics: configured process names vs. what /proc actually reports, " +
        "name-mismatch warnings, and PIDs. Useful when process_status says 'not running' but " +
        "you suspect it is — this exposes the resolution path.")]
    public string Diagnostics()
    {
        var diag = _pm.GetDiagnostics();
        return McpResult.Success(diag).ToJson();
    }

    [McpServerTool(Name = "process_start_mangosd")]
    [McpCapability(McpCapability.Process)]
    [Description("Start the world server (mangosd). Returns the launcher output (or empty in Docker).")]
    public async Task<string> StartMangosd()
    {
        var result = await SafeRun(() => _pm.StartMangosdAsync(), "start_mangosd");
        return McpResult.Success(new { output = result }).ToJson();
    }

    [McpServerTool(Name = "process_stop_mangosd")]
    [McpCapability(McpCapability.Process)]
    [Description("Stop the world server (mangosd). Players get disconnected. Realmd is unaffected.")]
    public async Task<string> StopMangosd()
    {
        var result = await SafeRun(() => _pm.StopMangosdAsync(), "stop_mangosd");
        return McpResult.Success(new { output = result }).ToJson();
    }

    [McpServerTool(Name = "process_restart_mangosd")]
    [McpCapability(McpCapability.Process)]
    [Description(
        "Restart the world server (mangosd) by stopping and starting it. " +
        "Does NOT auto-save first — call ra_save_all first if you want a clean restart. " +
        "Returns once the start command has been issued.")]
    public async Task<string> RestartMangosd()
    {
        var result = await SafeRun(() => _pm.RestartMangosdAsync(), "restart_mangosd");
        return McpResult.Success(new { output = result }).ToJson();
    }

    [McpServerTool(Name = "process_start_realmd")]
    [McpCapability(McpCapability.Process)]
    [Description("Start the auth server (realmd). Players can log in once mangosd is also running.")]
    public async Task<string> StartRealmd()
    {
        var result = await SafeRun(() => _pm.StartRealmdAsync(), "start_realmd");
        return McpResult.Success(new { output = result }).ToJson();
    }

    [McpServerTool(Name = "process_stop_realmd")]
    [McpCapability(McpCapability.Process)]
    [Description("Stop the auth server (realmd). Players can't log in or reconnect.")]
    public async Task<string> StopRealmd()
    {
        var result = await SafeRun(() => _pm.StopRealmdAsync(), "stop_realmd");
        return McpResult.Success(new { output = result }).ToJson();
    }

    [McpServerTool(Name = "process_restart_realmd")]
    [McpCapability(McpCapability.Process)]
    [Description("Restart the auth server (realmd).")]
    public async Task<string> RestartRealmd()
    {
        var result = await SafeRun(() => _pm.RestartRealmdAsync(), "restart_realmd");
        return McpResult.Success(new { output = result }).ToJson();
    }

    private async Task<McpResult> SafeRun(Func<Task<string>> action, string name)
    {
        try
        {
            var r = await action();
            return McpResult.Success(new { output = r });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Process action {Action} failed", name);
            return McpResult.Failure(ErrorCodes.Internal, ex.Message, retryable: false);
        }
    }
}
