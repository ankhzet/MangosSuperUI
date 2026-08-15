using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Read-only access to the mangos `logs_*` tables (character events, chat,
/// trades, transactions, warden, spam, behavior, battleground). Mirrors
/// <c>ServerLogsController</c> for the in-game Server Logs page.
/// </summary>
[McpServerToolType]
public class ServerLogTools
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<ServerLogTools> _log;

    public ServerLogTools(ConnectionFactory db, ILogger<ServerLogTools> log)
    {
        _db = db;
        _log = log;
    }

    [McpServerTool(Name = "log_overview")]
    [Description(
        "Row counts for every logs_* table. A value of -1 means the table " +
        "doesn't exist (likely mangos log schema not initialised). Cheap; " +
        "call first when the dashboard says logs are down.")]
    public async Task<string> Overview()
    {
        try
        {
            using var conn = _db.Logs();
            var counts = new Dictionary<string, int>();
            foreach (var table in new[] { "logs_characters", "logs_chat", "logs_trade", "logs_transactions",
                                          "logs_warden", "logs_spamdetect", "logs_behavior", "logs_battleground" })
            {
                try { counts[table] = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM `{table}`"); }
                catch { counts[table] = -1; }
            }
            return McpResult.Success(counts).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "log_overview failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "log_characters")]
    [Description(
        "Character events from logs_characters: logins, logouts, creates, " +
        "deletes, lost sockets. Filterable by type and name/ip substring.")]
    public async Task<string> Characters(
        [Description("Optional event-type filter, e.g. 'login', 'logout'.")] string? type = null,
        [Description("Optional LIKE substring over name/ip.")] string? search = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_characters",
            new[] { "time", "type", "guid", "account", "name", "ip", "clientHash" },
            BuildWhere(type, search, "name", "ip"), page, pageSize);
    }

    [McpServerTool(Name = "log_chat")]
    [Description(
        "Chat messages from logs_chat: say, whisper, group, guild, officer, " +
        "raid, BG, channel. Filterable by type and message/channel substring.")]
    public async Task<string> Chat(
        [Description("Optional chat-type filter.")] string? type = null,
        [Description("Optional LIKE substring over message or channelName.")] string? search = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_chat",
            new[] { "time", "type", "guid", "target", "channelId", "channelName", "message" },
            BuildWhere(type, search, "message", "channelName"), page, pageSize);
    }

    [McpServerTool(Name = "log_trades")]
    [Description(
        "Trade/economy events from logs_trade: auction, mail, loot, quest, GM, etc.")]
    public async Task<string> Trades(
        [Description("Optional trade-type filter.")] string? type = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_trade",
            new[] { "time", "type", "sender", "senderType", "senderEntry", "receiver", "amount", "data" },
            BuildWhere(type, null), page, pageSize);
    }

    [McpServerTool(Name = "log_transactions")]
    [Description(
        "Item/gold transactions from logs_transactions: auction bids, buyouts, " +
        "trades, mail, COD.")]
    public async Task<string> Transactions(
        [Description("Optional transaction-type filter.")] string? type = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_transactions",
            new[] { "time", "type", "guid1", "money1", "spell1", "items1", "guid2", "money2", "spell2", "items2" },
            BuildWhere(type, null), page, pageSize);
    }

    [McpServerTool(Name = "log_warden")]
    [Description(
        "Warden anticheat log entries from logs_warden.")]
    public async Task<string> Warden(
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_warden",
            new[] { "entry", "`check`", "action", "account", "guid", "map", "position_x AS posX",
                    "position_y AS posY", "position_z AS posZ", "date" },
            "WHERE 1=1", page, pageSize, orderBy: "date");
    }

    [McpServerTool(Name = "log_spam")]
    [Description("Spam detection log entries from logs_spamdetect.")]
    public async Task<string> Spam(
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_spamdetect",
            new[] { "time", "accountId", "guid", "message", "reason" },
            "WHERE 1=1", page, pageSize);
    }

    [McpServerTool(Name = "log_behavior")]
    [Description("Suspicious behavior detection entries from logs_behavior.")]
    public async Task<string> Behavior(
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_behavior",
            new[] { "id", "account", "detection", "data" },
            "WHERE 1=1", page, pageSize, orderBy: "id");
    }

    [McpServerTool(Name = "log_battlegrounds")]
    [Description("Battleground result log entries from logs_battleground.")]
    public async Task<string> Battlegrounds(
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        return await QueryPaged("logs_battleground",
            new[] { "time", "bgid", "bgtype", "bgteamcount", "bgduration",
                    "playerGuid", "team", "deaths", "honorBonus", "honorableKills" },
            "WHERE 1=1", page, pageSize);
    }

    // ---------- shared paging helper ----------

    private async Task<string> QueryPaged(
        string table, string[] columns, string whereClause,
        int page, int pageSize, string orderBy = "time")
    {
        var capped = Math.Clamp(pageSize, 1, 200);
        var offset = (Math.Max(1, page) - 1) * capped;
        try
        {
            using var conn = _db.Logs();
            var colList = string.Join(", ", columns);
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} {whereClause}");
            var rows = await conn.QueryAsync(
                $"SELECT {colList} FROM {table} {whereClause} ORDER BY {orderBy} DESC LIMIT @limit OFFSET @offset",
                new { limit = capped, offset });

            return McpResult.Success(new
            {
                rows,
                total,
                page,
                pageSize = capped,
                totalPages = (int)Math.Ceiling((double)total / capped)
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Table} query failed", table);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    private static string BuildWhere(string? type, string? search, params string[] searchCols)
    {
        var w = "WHERE 1=1";
        if (!string.IsNullOrEmpty(type)) w += " AND type = @type";
        if (!string.IsNullOrEmpty(search) && searchCols.Length > 0)
        {
            var subs = string.Join(" OR ", searchCols.Select(c => $"{c} LIKE @search"));
            w += $" AND ({subs})";
        }
        return w;
    }
}
