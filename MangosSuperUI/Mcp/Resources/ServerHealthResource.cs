using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Resources;

/// <summary>
/// Server-health snapshot as a single MCP resource. Aggregates
/// home_status (process flags + DB counts), process_status (mangosd/realmd
/// PIDs and uptimes), and home_db_health (per-DB connectivity + admin init).
/// </summary>
[McpServerResourceType]
public class ServerHealthResource
{
    private readonly RaService _ra;
    private readonly ProcessManagerService _pm;
    private readonly ConnectionFactory _db;
    private readonly DbInitializationService _dbInit;

    public ServerHealthResource(RaService ra, ProcessManagerService pm, ConnectionFactory db, DbInitializationService dbInit)
    {
        _ra = ra;
        _pm = pm;
        _db = db;
        _dbInit = dbInit;
    }

    [McpServerResource(Name = "health", UriTemplate = "mcp://msui/health", MimeType = "application/json")]
    [System.ComponentModel.Description(
        "Aggregated server health: mangosd + realmd process state, RA " +
        "connectivity, parsed .server info, DB row counts, per-DB ping. " +
        "Returns JSON the agent can `@-mention` to anchor a conversation.")]
    public async Task<ReadResourceResult> GetHealth(CancellationToken ct)
    {
        var mangosd = _pm.GetMangosdStatus();
        var realmd = _pm.GetRealmdStatus();

        string? serverInfoRaw = null;
        int playersOnline = 0, maxOnline = 0;
        try
        {
            if (_ra.IsConnected || mangosd.IsRunning)
            {
                serverInfoRaw = await _ra.SendCommandAsync(".server info", ct);
                var lines = serverInfoRaw.Split('\n');
                foreach (var line in lines)
                {
                    var t = line.Trim().TrimEnd('\r');
                    if (t.StartsWith("Players online", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = t.Split(':')[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 1) int.TryParse(parts[0], out playersOnline);
                        if (parts.Length >= 2) int.TryParse(parts[1], out maxOnline);
                    }
                }
            }
        }
        catch { /* best-effort */ }

        int totalAccounts = 0, totalCharacters = 0, gmAccounts = 0, bannedAccounts = 0;
        try
        {
            using var realmdConn = _db.Realmd();
            totalAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account");
            gmAccounts = await realmdConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT id) FROM account_access WHERE gmlevel > 0");
            bannedAccounts = await realmdConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM account_banned WHERE active = 1");
        }
        catch { /* best-effort */ }
        try
        {
            using var charsConn = _db.Characters();
            totalCharacters = await charsConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM characters");
        }
        catch { /* best-effort */ }

        var dbHealth = await SafeDbHealthAsync(ct);

        var payload = new
        {
            capturedAt = DateTimeOffset.UtcNow,
            mangosd = new
            {
                running = mangosd.IsRunning,
                pid = mangosd.Pid,
                processName = mangosd.ProcessName,
                uptimeSeconds = mangosd.Uptime.HasValue ? (long)mangosd.Uptime.Value.TotalSeconds : (long?)null
            },
            realmdState = new
            {
                running = realmd.IsRunning,
                pid = realmd.Pid,
                processName = realmd.ProcessName,
                uptimeSeconds = realmd.Uptime.HasValue ? (long)realmd.Uptime.Value.TotalSeconds : (long?)null
            },
            raConnected = _ra.IsConnected,
            playersOnline,
            maxOnline,
            totalAccounts,
            totalCharacters,
            gmAccounts,
            bannedAccounts,
            dbHealth = dbHealth
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents { Uri = "mcp://msui/health", Text = json, MimeType = "application/json" }
            }
        };
    }

    private async Task<object?> SafeDbHealthAsync(CancellationToken ct)
    {
        try { return await _dbInit.CheckHealthAsync(); }
        catch { return null; }
    }
}
