using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Account + realm CRUD. Read endpoints use the `read` capability;
/// `account_realm_update` requires `write_db`. Wraps
/// <c>AccountsController.List/Summary/Detail</c> +
/// <c>RealmController.List/Update</c>.
/// </summary>
[McpServerToolType]
public class AccountWriteTools
{
    private readonly ConnectionFactory _db;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<AccountWriteTools> _log;

    public AccountWriteTools(ConnectionFactory db, AuditService audit,
        McpCallContext ctx, ILogger<AccountWriteTools> log)
    {
        _db = db;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "account_list")]
    [Description(
        "Paginated, filterable account list. Mirrors the Accounts filter bar " +
        "(username/IP substring, GM level, banned/muted/locked/online/gm status).")]
    public async Task<string> List(
        [Description("Username or last_ip substring.")] string? query = null,
        [Description("Filter to a specific GM level (0-7).")] int? gmLevel = null,
        [Description("Filter: 'banned', 'muted', 'locked', 'online', or 'gm'.")] string? status = null,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 200.")] int pageSize = 50)
    {
        try
        {
            using var conn = _db.Realmd();
            var where = new List<string>();
            var p = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(query))
            {
                where.Add("(a.username LIKE @term OR a.last_ip LIKE @term)");
                p.Add("term", $"%{query.Trim()}%");
            }
            if (gmLevel.HasValue) { where.Add("a.gmlevel = @gmLevel"); p.Add("gmLevel", gmLevel.Value); }
            switch (status)
            {
                case "banned": where.Add("EXISTS (SELECT 1 FROM account_banned ab WHERE ab.id = a.id AND ab.active = 1)"); break;
                case "muted":  where.Add("a.mutetime > UNIX_TIMESTAMP()"); break;
                case "locked": where.Add("a.locked = 1"); break;
                case "online": where.Add("a.online = 1"); break;
                case "gm":     where.Add("a.gmlevel > 0"); break;
            }

            var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM account a {whereClause}", p);
            p.Add("limit", Math.Clamp(pageSize, 1, 200));
            p.Add("offset", (Math.Max(1, page) - 1) * pageSize);

            var accounts = (await conn.QueryAsync<dynamic>($@"
                SELECT a.id, a.username, a.last_ip AS lastIp, a.last_login AS lastLogin,
                       a.gmlevel AS gmLevel, a.locked, a.mutetime AS muteTime, a.online,
                       a.email, a.os, a.platform,
                       (SELECT COUNT(*) FROM characters.characters c WHERE c.account = a.id) AS characterCount,
                       EXISTS (SELECT 1 FROM account_banned ab WHERE ab.id = a.id AND ab.active = 1) AS isBanned
                FROM account a {whereClause}
                ORDER BY a.id ASC
                LIMIT @limit OFFSET @offset", p)).ToList();

            return McpResult.Success(new
            {
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize),
                accounts
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "account_list failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "account_summary")]
    [Description(
        "One-shot totals for the account filter bar (total / online / gm / " +
        "locked / muted / banned).")]
    public async Task<string> Summary()
    {
        try
        {
            using var realmd = _db.Realmd();
            var totals = await realmd.QueryFirstAsync<(long Total, long Online, long Gm, long Locked, long Muted)>(@"
                SELECT COUNT(*) AS Total,
                       SUM(CASE WHEN online = 1 THEN 1 ELSE 0 END) AS Online,
                       SUM(CASE WHEN gmlevel > 0 THEN 1 ELSE 0 END) AS Gm,
                       SUM(CASE WHEN locked = 1 THEN 1 ELSE 0 END) AS Locked,
                       SUM(CASE WHEN mutetime > UNIX_TIMESTAMP() THEN 1 ELSE 0 END) AS Muted
                FROM account");
            var banned = await realmd.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT id) FROM account_banned WHERE active = 1");
            return McpResult.Success(new
            {
                total = (int)totals.Total,
                online = (int)totals.Online,
                gm = (int)totals.Gm,
                locked = (int)totals.Locked,
                muted = (int)totals.Muted,
                banned
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "account_summary failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "account_detail")]
    [Description(
        "Full account record by id: characters on the account, ban history, " +
        "and last 20 audit_log actions targeting the account.")]
    public async Task<string> Detail(
        [Description("Account id.")] int id)
    {
        if (id <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "id must be positive").ToJson();
        try
        {
            using var realmd = _db.Realmd();
            var account = await realmd.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT id, username, last_ip AS lastIp, last_login AS lastLogin,
                         gmlevel AS gmLevel, locked, mutetime AS muteTime, online,
                         email, os, platform, joindate AS joinDate
                  FROM account WHERE id = @id",
                new { id });
            if (account is null)
                return McpResult.Success(new { found = false, id }).ToJson();

            using var chars = _db.Characters();
            var characters = (await chars.QueryAsync<dynamic>(@"
                SELECT guid, name, level, race, class AS classId, gender, online,
                       played_time_total AS playedTotal, logout_time AS logoutTime
                FROM characters WHERE account = @id ORDER BY guid", new { id })).ToList();

            var bans = (await realmd.QueryAsync<dynamic>(
                @"SELECT id, bandate, unbandate, banreason, active, bannedby
                  FROM account_banned WHERE id = @id ORDER BY bandate DESC LIMIT 20",
                new { id })).ToList();

            var audit = (await realmd.QueryAsync<dynamic>(@"
                SELECT id, `timestamp`, category, action, target_name AS targetName,
                       ra_command AS raCommand, success, notes
                FROM audit_log
                WHERE (target_type = 'account' AND target_id = @id) OR operator = (SELECT username FROM account WHERE id = @id)
                ORDER BY `timestamp` DESC LIMIT 20",
                new { id })).ToList();

            return McpResult.Success(new { found = true, id, account, characters, bans, audit }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "account_detail failed for id {Id}", id);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "account_realm_list")]
    [Description(
        "All realms from realmlist, with online player + account counts. " +
        "Each realm exposes icon (type), flags (bitmask), timezone, population.")]
    public async Task<string> RealmList()
    {
        try
        {
            using var conn = _db.Realmd();
            var realms = (await conn.QueryAsync<dynamic>(@"
                SELECT id, name, address, port, icon, realmflags AS realmFlags,
                       timezone, population, realmbuilds AS realmBuilds
                FROM realmlist ORDER BY id")).ToList();
            using var charConn = _db.Characters();
            var onlinePlayers = await charConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM characters WHERE online = 1");
            var onlineAccounts = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM account WHERE online = 1");
            var totalAccounts = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM account");

            return McpResult.Success(new
            {
                realms,
                stats = new { onlinePlayers, onlineAccounts, totalAccounts }
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "account_realm_list failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "account_realm_update")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Update a realm's editable fields. Captures the before state and writes " +
        "an audit_log row per call. Idempotent — only fields that actually changed " +
        "appear in the audit entry.")]
    public async Task<string> RealmUpdate(
        [Description("Realm id to update.")] int id,
        [Description("New display name.")] string name,
        [Description("New IP/hostname.")] string address,
        [Description("New port.")] int port,
        [Description("Realm icon (0=Normal, 1=PvP, 4=Normal*, 6=RP, 8=RP-PvP).")] int icon,
        [Description("Realm flags bitmask.")] int realmFlags,
        [Description("Realm timezone id (see realmlist docs).")] int timezone,
        [Description("Population float (0..3).")] float population)
    {
        if (id <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "id must be positive").ToJson();

        try
        {
            using var conn = _db.Realmd();
            var before = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT id, name, address, port, icon, realmflags AS realmFlags,
                         timezone, population, realmbuilds AS realmBuilds
                  FROM realmlist WHERE id = @id", new { id });
            if (before is null)
                return McpResult.Failure(ErrorCodes.NotFound, $"Realm {id} not found").ToJson();

            await conn.ExecuteAsync(
                @"UPDATE realmlist SET name=@name, address=@address, port=@port,
                                       icon=@icon, realmflags=@realmFlags,
                                       timezone=@timezone, population=@population
                  WHERE id = @id",
                new { id, name, address, port, icon, realmFlags, timezone, population });

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "system",
                Action = "realm_update",
                TargetType = "realm",
                TargetName = name,
                TargetId = id,
                StateBefore = System.Text.Json.JsonSerializer.Serialize(before),
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new { name, address, port, icon, realmFlags, timezone, population }),
                Success = true,
                Notes = "Realm configuration updated via MCP"
            });

            return McpResult.Success(new { success = true, realmId = id }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "account_realm_update failed for id {Id}", id);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
