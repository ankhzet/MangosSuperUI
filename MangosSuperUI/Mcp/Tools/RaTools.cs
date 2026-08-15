using System.ComponentModel;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Wraps mangosd's RemoteAdmin (RA) console for LLM clients. Every call goes
/// through AuditService.ExecuteAndLogAsync so the operator, command, response,
/// and state delta are recorded in audit_log just like UI-driven RA actions.
/// </summary>
[McpServerToolType]
public class RaTools
{
    private readonly RaService _ra;
    private readonly AuditService _audit;
    private readonly MangosSuperUI.Mcp.Auth.McpCallContext _ctx;
    private readonly ILogger<RaTools> _logger;

    public RaTools(RaService ra, AuditService audit,
        MangosSuperUI.Mcp.Auth.McpCallContext ctx,
        ILogger<RaTools> logger)
    {
        _ra = ra;
        _audit = audit;
        _ctx = ctx;
        _logger = logger;
    }

    [McpServerTool(Name = "ra_send_command")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Send a raw RemoteAdmin command to mangosd. Returns the mangosd response verbatim " +
        "(RA prompt is stripped). Examples: 'server info', 'server plr', 'announce Hello world', " +
        "'kick player SomeName', 'ban account SomeName 30d reason'. Prefer the higher-level tools " +
        "(kick_player, ban_account, announce, etc.) for common actions — they validate inputs.")]
    public async Task<string> SendCommand(
        [Description("Full RA command line, without the trailing newline. Must not be empty.")] string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return McpResult.Failure(ErrorCodes.InvalidInput, "command cannot be empty").ToJson();

        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, command, operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: "MCP tool: ra_send_command");

        return success
            ? McpResult.Success(new { response }).ToJson()
            : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();
    }

    [McpServerTool(Name = "ra_server_info")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Run 'server info' on mangosd and return a compact, structured summary. " +
        "Use this first when you need to know whether the world server is reachable " +
        "and what its current state is (uptime, revision, online count).")]
    public async Task<string> ServerInfo()
    {
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, "server info", operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: "MCP tool: ra_server_info");

        if (!success)
            return McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();

        var parsed = ParseServerInfo(response);
        return McpResult.Success(new
        {
            online = parsed.GetValueOrDefault("Online players"),
            update = parsed.GetValueOrDefault("Last update time"),
            revision = parsed.GetValueOrDefault("MaNGOS revision"),
            uptime = parsed.GetValueOrDefault("Uptime"),
            server = parsed.GetValueOrDefault("Server"),
            raw = response
        }).ToJson();
    }

    [McpServerTool(Name = "ra_list_online")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "List currently connected players via the RA 'server plr' command. Returns up to " +
        "50 players. Each row includes the character name, account, IP, level, class, race, " +
        "zone, and online seconds. Use player_search for character-name lookups.")]
    public async Task<string> ListOnline()
    {
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, "server plr", operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: "MCP tool: ra_list_online");

        if (!success)
            return McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();

        return McpResult.Success(new { players = ParsePlayerList(response), raw = response }).ToJson();
    }

    [McpServerTool(Name = "ra_kick_player")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Kick a currently-online player by character name. Sends the RA 'kick player <name>' command. " +
        "Returns the RA response. The character must be online.")]
    public async Task<string> KickPlayer(
        [Description("Exact character name of the online player to kick.")] string characterName,
        [Description("Optional reason message stored in the audit log.")] string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson();

        var cmd = $"kick player {characterName}";
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, cmd, operatorIp: "mcp",
            notes: string.IsNullOrWhiteSpace(reason) ? "MCP tool: ra_kick_player" : $"reason={reason}");

        return success
            ? McpResult.Success(new { response }).ToJson()
            : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();
    }

    [McpServerTool(Name = "ra_ban_account")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Ban an account by username. Sends the RA 'ban account <name> <duration> <reason>' command. " +
        "Duration format follows mangosd (e.g. '30m', '2h', '7d', '0' for permanent). " +
        "Returns the RA response. To unban, use ra_unban_account.")]
    public async Task<string> BanAccount(
        [Description("Exact account username to ban.")] string accountName,
        [Description("Ban duration (e.g. '30m', '2h', '7d'). Use '0' for permanent.")] string duration,
        [Description("Ban reason recorded on the account.")] string reason)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(duration) || string.IsNullOrWhiteSpace(reason))
            return McpResult.Failure(ErrorCodes.InvalidInput, "accountName, duration and reason are all required").ToJson();

        var cmd = $"ban account {accountName} {duration} {reason}";
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, cmd, operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: "MCP tool: ra_ban_account");

        return success
            ? McpResult.Success(new { response }).ToJson()
            : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();
    }

    [McpServerTool(Name = "ra_unban_account")]
    [McpCapability(McpCapability.Ra)]
    [Description("Lift an active ban on an account by username. Sends 'ban account <name> 0 unban'.")]
    public async Task<string> UnbanAccount(
        [Description("Exact account username to unban.")] string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return McpResult.Failure(ErrorCodes.InvalidInput, "accountName cannot be empty").ToJson();

        var cmd = $"ban account {accountName} 0 unban";
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, cmd, operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: "MCP tool: ra_unban_account");

        return success
            ? McpResult.Success(new { response }).ToJson()
            : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();
    }

    [McpServerTool(Name = "ra_announce")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Broadcast a server-wide announcement to all online players. " +
        "Equivalent to 'announce <message>' in the RA console. Useful for GM announcements, " +
        "scheduled restarts, or event notices. Returns the RA response.")]
    public async Task<string> Announce(
        [Description("The message to broadcast. Keep it concise — appears in the player's chat frame.")] string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return McpResult.Failure(ErrorCodes.InvalidInput, "message cannot be empty").ToJson();

        var cmd = $"announce {message}";
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, cmd, operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: "MCP tool: ra_announce");

        return success
            ? McpResult.Success(new { response }).ToJson()
            : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();
    }

    [McpServerTool(Name = "ra_save_all")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Force-save all online players' data immediately. Sends 'saveall' to mangosd. " +
        "Normally the server saves every 5 minutes — call this right before a restart or " +
        "after large-scale changes you want persisted now.")]
    public async Task<string> SaveAll()
    {
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, "saveall", operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: "MCP tool: ra_save_all");

        return success
            ? McpResult.Success(new { response }).ToJson()
            : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();
    }

    [McpServerTool(Name = "ra_shutdown")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Shut down the world server (mangosd) after an optional delay (seconds). " +
        "Sends 'shutdown <seconds>'. The realmd process is unaffected — players will get " +
        "disconnected but the auth server stays up. Use process_restart_mangosd if you want " +
        "to bring it back up after.")]
    public async Task<string> Shutdown(
        [Description("Seconds to wait before shutdown. Use a small positive value (e.g. 5) to give players a heads-up.")] int seconds = 5)
    {
        var cmd = $"shutdown {Math.Max(0, seconds)}";
        var (response, success) = await _audit.ExecuteAndLogAsync(
            _ra, cmd, operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: $"MCP tool: ra_shutdown seconds={seconds}");

        return success
            ? McpResult.Success(new { response }).ToJson()
            : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true, hint: "ra_reconnect").ToJson();
    }

    [McpServerTool(Name = "ra_connection_status")]
    [McpCapability(McpCapability.Ra)]
    [Description("Returns whether the MCP server currently has a live RA connection to mangosd. " +
                 "True means a subsequent ra_send_command will be fast; false means the next call will reconnect.")]
    public string ConnectionStatus() => McpResult.Success(new { connected = _ra.IsConnected }).ToJson();

    // ----- parsing helpers -----

    private static Dictionary<string, string> ParseServerInfo(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("mangos>")) continue;

            var idx = trimmed.IndexOf(':');
            if (idx <= 0) continue;
            var key = trimmed.Substring(0, idx).Trim();
            var val = trimmed.Substring(idx + 1).Trim();
            if (!string.IsNullOrEmpty(key))
                dict[key] = val;
        }
        return dict;
    }

    private static List<object> ParsePlayerList(string raw)
    {
        var rows = new List<object>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("mangos>")) continue;
            if (!trimmed.Contains("|")) continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 7) continue;

            rows.Add(new
            {
                guid = parts[0].Trim(),
                name = parts[1].Trim(),
                account = parts[2].Trim(),
                ip = parts[3].Trim(),
                secondsOnline = parts[4].Trim(),
                level = parts[5].Trim(),
                @class = parts[6].Trim(),
                race = parts.Length > 7 ? parts[7].Trim() : null,
                zone = parts.Length > 8 ? parts[8].Trim() : null
            });
        }
        return rows;
    }
}
