using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Server self-diagnosis. Mirrors <c>HomeController.Status / DbHealth /
/// Diagnose</c> for the dashboard. Read-only — safe to call freely.
/// </summary>
[McpServerToolType]
public class HomeTools
{
    private readonly ConnectionFactory _db;
    private readonly RaService _ra;
    private readonly ProcessManagerService _pm;
    private readonly DbInitializationService _dbInit;
    private readonly IConfiguration _config;
    private readonly IOptionsMonitor<VmangosSettings> _vm;
    private readonly IOptionsMonitor<RemoteAccessSettings> _raSet;
    private readonly ILogger<HomeTools> _log;

    public HomeTools(
        ConnectionFactory db,
        RaService ra,
        ProcessManagerService pm,
        DbInitializationService dbInit,
        IConfiguration config,
        IOptionsMonitor<VmangosSettings> vm,
        IOptionsMonitor<RemoteAccessSettings> raSet,
        ILogger<HomeTools> log)
    {
        _db = db;
        _ra = ra;
        _pm = pm;
        _dbInit = dbInit;
        _config = config;
        _vm = vm;
        _raSet = raSet;
        _log = log;
    }

    [McpServerTool(Name = "home_status")]
    [Description(
        "Live server status: process flags for mangosd/realmd, RA connection state, " +
        "parsed `.server info` (players online, max, uptime, core revision), and " +
        "DB row counts (accounts, characters, GMs, banned accounts).")]
    public async Task<string> Status()
    {
        var mangosd = _pm.GetMangosdStatus();
        var realmd = _pm.GetRealmdStatus();

        string? serverInfoRaw = null;
        int playersOnline = 0, maxOnline = 0;
        string? uptime = null, coreRevision = null;

        try
        {
            if (_ra.IsConnected || mangosd.IsRunning)
            {
                serverInfoRaw = await _ra.SendCommandAsync(".server info");
                ParseServerInfo(serverInfoRaw, out playersOnline, out maxOnline, out uptime, out coreRevision);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "home_status: failed to get .server info from RA");
        }

        int totalAccounts = 0, gmAccounts = 0, bannedAccounts = 0, totalCharacters = 0;
        try
        {
            using var realmdConn = _db.Realmd();
            totalAccounts = await realmdConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account");
            gmAccounts = await realmdConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT id) FROM account_access WHERE gmlevel > 0");
            bannedAccounts = await realmdConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM account_banned WHERE active = 1");
        }
        catch (Exception ex) { _log.LogWarning(ex, "home_status: realmd query failed"); }

        try
        {
            using var charConn = _db.Characters();
            totalCharacters = await charConn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM characters");
        }
        catch (Exception ex) { _log.LogWarning(ex, "home_status: characters query failed"); }

        return McpResult.Success(new
        {
            mangosd = new
            {
                mangosd.IsRunning, mangosd.Pid, mangosd.ProcessName,
                uptimeSeconds = mangosd.Uptime.HasValue ? (long)mangosd.Uptime.Value.TotalSeconds : (long?)null
            },
            realmd = new
            {
                realmd.IsRunning, realmd.Pid, realmd.ProcessName,
                uptimeSeconds = realmd.Uptime.HasValue ? (long)realmd.Uptime.Value.TotalSeconds : (long?)null
            },
            raConnected = _ra.IsConnected,
            playersOnline,
            maxOnline,
            uptime,
            coreRevision,
            serverInfoRaw,
            totalAccounts,
            totalCharacters,
            gmAccounts,
            bannedAccounts
        }).ToJson();
    }

    [McpServerTool(Name = "home_db_health")]
    [Description(
        "Per-database connectivity probe. Returns up/down/latency for mangos, " +
        "characters, realmd, logs, vmangos_admin and whether the admin schema " +
        "initialised successfully. Heavier than home_status — call when you suspect " +
        "a DB problem, not on every poll.")]
    public async Task<string> DbHealth()
    {
        try
        {
            var report = await _dbInit.CheckHealthAsync();
            return McpResult.Success(report).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "home_db_health failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "home_diagnose")]
    [Description(
        "Comprehensive self-diagnosis: probes every subsystem (config, processes, " +
        "RA connectivity, DBC files, world DB rows) and returns per-check status " +
        "(ok/warning/error) with detail + fix suggestions. Use this when the " +
        "dashboard is red and you need an actionable diagnosis.")]
    public async Task<string> Diagnose()
    {
        var checks = new List<DiagnosticCheck>();
        var ra = _raSet.CurrentValue;
        var vm = _vm.CurrentValue;

        // ── 1. Configuration ──
        var hasOverride = !string.IsNullOrEmpty(_config["Vmangos:BinDirectory"]);
        checks.Add(new DiagnosticCheck
        {
            Category = "config",
            Name = "Configuration Override",
            Status = hasOverride ? "ok" : "warning",
            Detail = hasOverride ? "server-config.json present" : "No override file — defaults in use",
            Fix = hasOverride ? null : "Configure via Settings → Save"
        });

        var raIsDefault = string.IsNullOrEmpty(ra.Username)
            || ra.Username == "ADMIN" || ra.Password == "CHANGE_ME";
        checks.Add(new DiagnosticCheck
        {
            Category = "config",
            Name = "RA Credentials",
            Status = raIsDefault ? "error" : "ok",
            Detail = raIsDefault ? "RA credentials are placeholder/default" : "configured",
            Fix = raIsDefault
                ? "Create an RA account in mangosd console and update Settings"
                : null
        });

        // ── 2. Process detection ──
        var procDiag = _pm.GetDiagnostics();
        checks.Add(new DiagnosticCheck
        {
            Category = "process",
            Name = "World Server (mangosd)",
            Status = procDiag.MangosdRunning ? (procDiag.MangosdNameMismatch ? "warning" : "ok") : "error",
            Detail = procDiag.MangosdRunning
                ? $"Running as '{procDiag.ResolvedMangosd}' (PID {procDiag.MangosdPid})"
                : "Not running",
            Fix = procDiag.MangosdRunning ? procDiag.MangosdHint : "Start mangosd via process_start_mangosd or systemctl"
        });
        checks.Add(new DiagnosticCheck
        {
            Category = "process",
            Name = "Auth Server (realmd)",
            Status = procDiag.RealmdRunning ? (procDiag.RealmdNameMismatch ? "warning" : "ok") : "error",
            Detail = procDiag.RealmdRunning
                ? $"Running as '{procDiag.ResolvedRealmd}' (PID {procDiag.RealmdPid})"
                : "Not running",
            Fix = procDiag.RealmdRunning ? procDiag.RealmdHint : "Start realmd via process_start_realmd or systemctl"
        });

        // ── 3. RA connectivity ──
        string raStatus, raDetail;
        string? raFix = null;
        if (raIsDefault) { raStatus = "error"; raDetail = "Credentials not configured"; raFix = "Set in Settings."; }
        else if (!_ra.IsConnected && !procDiag.MangosdRunning) { raStatus = "warning"; raDetail = "mangosd offline"; }
        else if (!_ra.IsConnected) { raStatus = "warning"; raDetail = "RA socket disconnected (auto-reconnects on next call)"; }
        else { raStatus = "ok"; raDetail = "Connected"; }
        checks.Add(new DiagnosticCheck { Category = "ra", Name = "RA Connectivity", Status = raStatus, Detail = raDetail, Fix = raFix });

        // ── 4. DBC files ──
        bool dbcPresent = false;
        try
        {
            dbcPresent = !string.IsNullOrEmpty(vm.DbcPath) && System.IO.Directory.Exists(vm.DbcPath)
                && System.IO.Directory.EnumerateFiles(vm.DbcPath, "*.dbc").Any();
        }
        catch { /* ignore */ }
        checks.Add(new DiagnosticCheck
        {
            Category = "dbc",
            Name = "DBC Files",
            Status = dbcPresent ? "ok" : "warning",
            Detail = dbcPresent ? $"Present in {vm.DbcPath}" : $"Not found at {vm.DbcPath}",
            Fix = dbcPresent ? null : "Run extract-client-data.sh or set DbcPath"
        });

        // ── 5. World DB sanity ──
        try
        {
            using var conn = _db.Mangos();
            var itemCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM item_template");
            var creatureCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM creature_template");
            checks.Add(new DiagnosticCheck
            {
                Category = "world",
                Name = "World DB Row Counts",
                Status = (itemCount > 100 && creatureCount > 100) ? "ok" : "warning",
                Detail = $"items={itemCount}, creatures={creatureCount}",
                Fix = (itemCount > 100 && creatureCount > 100) ? null : "Run init-database.sh --standard or --full"
            });
        }
        catch (Exception ex)
        {
            checks.Add(new DiagnosticCheck
            {
                Category = "world",
                Name = "World DB Row Counts",
                Status = "error",
                Detail = "Failed to query: " + ex.Message,
                Fix = "Check mariadb connectivity"
            });
        }

        return McpResult.Success(new
        {
            checks,
            summary = new
            {
                total = checks.Count,
                errors = checks.Count(c => c.Status == "error"),
                warnings = checks.Count(c => c.Status == "warning"),
                ok = checks.Count(c => c.Status == "ok")
            }
        }).ToJson();
    }

    private static void ParseServerInfo(string raw, out int online, out int maxOnline, out string? uptime, out string? revision)
    {
        online = 0; maxOnline = 0; uptime = null; revision = null;
        if (string.IsNullOrEmpty(raw)) return;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(t) || t.StartsWith("mangos>")) continue;
            var idx = t.IndexOf(':');
            if (idx <= 0) continue;
            var k = t.Substring(0, idx).Trim();
            var v = t.Substring(idx + 1).Trim();
            if (k.Equals("Players online", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var n))
            {
                var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1) int.TryParse(parts[0], out online);
                if (parts.Length >= 2) int.TryParse(parts[1], out maxOnline);
                _ = n;
            }
            else if (k.Equals("Uptime", StringComparison.OrdinalIgnoreCase)) uptime = v;
            else if (k.Equals("Core revision", StringComparison.OrdinalIgnoreCase)) revision = v;
        }
    }

    private sealed class DiagnosticCheck
    {
        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Detail { get; set; } = "";
        public string? Fix { get; set; }
    }
}
