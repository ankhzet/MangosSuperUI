using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Read-only database queries against characters/realmd/mangos for LLM clients.
/// All queries mirror the existing PlayersController / AccountsController SQL
/// so behaviour stays consistent with the web UI.
/// </summary>
[McpServerToolType]
public class PlayerTools
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<PlayerTools> _logger;

    public PlayerTools(ConnectionFactory db, ILogger<PlayerTools> logger)
    {
        _db = db;
        _logger = logger;
    }

    [McpServerTool(Name = "player_search")]
    [Description(
        "Search characters by name prefix. Joins against realmd.account so you also see " +
        "the owning account username. Returns at most 15 matches. Use player_detail to " +
        "drill down once you have a guid.")]
    public async Task<string> Search(
        [Description("Name prefix — at least 3 characters for sensible results.")] string query,
        [Description("Max rows to return (default 15, hard cap 50).")] int limit = 15)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 3)
            return McpResult.Failure(ErrorCodes.InvalidInput, "query must be at least 3 characters",
                hint: "use at least 3 characters").ToJson();

        var capped = Math.Clamp(limit, 1, 50);

        try
        {
            using var conn = _db.Characters();
            var rows = await conn.QueryAsync<(int Guid, string Name, int Level, int Race, int ClassId, int Online, string AccountName)>(
                @"SELECT c.guid, c.name, c.level, c.race, c.class, c.online, a.username
                  FROM characters c
                  JOIN realmd.account a ON a.id = c.account
                  WHERE c.name LIKE @term
                  ORDER BY c.name
                  LIMIT @limit",
                new { term = query + "%", limit = capped });

            var results = rows.Select(r => new
            {
                guid = r.Guid,
                name = r.Name,
                level = r.Level,
                race = WoWLookups.RaceName(r.Race),
                raceId = r.Race,
                @class = WoWLookups.ClassName(r.ClassId),
                classId = r.ClassId,
                online = r.Online != 0,
                accountName = r.AccountName
            });

            return McpResult.Success(new { count = results.Count(), results }).ToJson();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "player_search failed for query {Query}", query);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "player_detail")]
    [Description(
        "Full player record by character guid: level, race, class, money (broken into gold/silver/copper), " +
        "online state, last position (map/zone/x/y/z), playtime, guild membership, and account metadata. " +
        "Returns {found:false} when the guid doesn't exist.")]
    public async Task<string> Detail(
        [Description("Character guid (integer, from player_search or accounts_list).")] int guid)
    {
        try
        {
            using var charConn = _db.Characters();

            var player = await charConn.QueryFirstOrDefaultAsync<PlayerDetailRow>(
                @"SELECT guid, name, account, level, race, class AS classId, gender, money, online,
                         zone, map, position_x AS posX, position_y AS posY, position_z AS posZ,
                         played_time_total AS playedTotal, played_time_level AS playedLevel,
                         create_time AS createTime, logout_time AS logoutTime
                  FROM characters WHERE guid = @guid",
                new { guid });

            if (player is null)
                return McpResult.Success(new { found = false }).ToJson();

            var guild = await charConn.QueryFirstOrDefaultAsync<(string GuildName, int GuildRank)>(
                @"SELECT g.name, gm.rank FROM guild_member gm
                  JOIN guild g ON g.guild_id = gm.guild_id
                  WHERE gm.guid = @guid",
                new { guid });

            using var realmdConn = _db.Realmd();
            var account = await realmdConn.QueryFirstOrDefaultAsync<AccountSummaryRow>(
                @"SELECT id, username, last_ip AS lastIp, last_login AS lastLogin,
                         gmlevel AS gmLevel, locked, mutetime AS muteTime, online
                  FROM account WHERE id = @id",
                new { id = player.Account });

            var result = new
            {
                found = true,
                player = new
                {
                    player.Guid,
                    player.Name,
                    accountId = player.Account,
                    player.Level,
                    race = WoWLookups.RaceName(player.Race),
                    raceId = player.Race,
                    @class = WoWLookups.ClassName(player.ClassId),
                    classId = player.ClassId,
                    gender = player.Gender == 0 ? "Male" : "Female",
                    gold = player.Money / 10000,
                    silver = (player.Money % 10000) / 100,
                    copper = player.Money % 100,
                    moneyRaw = player.Money,
                    online = player.Online != 0,
                    player.Zone,
                    player.Map,
                    player.PosX,
                    player.PosY,
                    player.PosZ,
                    playedTotal = WoWLookups.FormatPlaytime(player.PlayedTotal),
                    playedLevel = WoWLookups.FormatPlaytime(player.PlayedLevel),
                    createTime = player.CreateTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(player.CreateTime).UtcDateTime.ToString("u")
                        : null,
                    logoutTime = player.LogoutTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(player.LogoutTime).UtcDateTime.ToString("u")
                        : null
                },
                guild = guild.GuildName is null ? null : new
                {
                    name = guild.GuildName,
                    rank = guild.GuildRank
                },
                account = account is null ? null : new
                {
                    account.Id,
                    account.Username,
                    account.LastIp,
                    lastLogin = account.LastLogin.ToString("u"),
                    account.GmLevel,
                    gmLevelName = WoWLookups.GmLevelName(account.GmLevel),
                    account.Locked,
                    isMuted = account.MuteTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    account.Online
                }
            };

            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "player_detail failed for guid {Guid}", guid);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "player_list_online")]
    [Description(
        "List all currently online characters (cross-DB join on realmd.account). " +
        "Use player_search for name lookups and player_detail for a single guid. " +
        "Returns up to 'limit' rows (default 50, hard cap 200).")]
    public async Task<string> ListOnline(
        [Description("Max rows to return (default 50, hard cap 200).")] int limit = 50)
    {
        var capped = Math.Clamp(limit, 1, 200);
        try
        {
            using var conn = _db.Characters();
            var rows = await conn.QueryAsync<OnlinePlayerRow>(
                @"SELECT c.guid, c.name, c.level, c.race, c.class AS classId,
                         c.zone, c.map, a.username AS accountName
                  FROM characters c
                  JOIN realmd.account a ON a.id = c.account
                  WHERE c.online = 1
                  ORDER BY c.level DESC, c.name
                  LIMIT @limit",
                new { limit = capped });

            var results = rows.Select(r => new
            {
                r.Guid,
                r.Name,
                r.Level,
                race = WoWLookups.RaceName(r.Race),
                raceId = r.Race,
                @class = WoWLookups.ClassName(r.ClassId),
                classId = r.ClassId,
                r.Zone,
                r.Map,
                r.AccountName
            });

            return McpResult.Success(new { count = results.Count(), results }).ToJson();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "player_list_online failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "account_lookup")]
    [Description(
        "Look up an account by exact username. Returns account metadata (id, gmlevel, online, last_ip, " +
        "last_login, locked, mute state) and the list of characters on the account.")]
    public async Task<string> AccountLookup(
        [Description("Exact account username (case-insensitive).")] string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return McpResult.Failure(ErrorCodes.InvalidInput, "username cannot be empty").ToJson();

        try
        {
            using var realmd = _db.Realmd();
            var account = await realmd.QueryFirstOrDefaultAsync<AccountSummaryRow>(
                @"SELECT id, username, last_ip AS lastIp, last_login AS lastLogin,
                         gmlevel AS gmLevel, locked, mutetime AS muteTime, online
                  FROM account WHERE username = @u",
                new { u = username });

            if (account is null)
                return McpResult.Success(new { found = false }).ToJson();

            using var chars = _db.Characters();
            var characterRows = await chars.QueryAsync<(int Guid, string Name, int Level, int Race, int ClassId, int Online)>(
                @"SELECT guid, name, level, race, class, online
                  FROM characters WHERE account = @id ORDER BY guid",
                new { id = account.Id });

            var isBanned = await realmd.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM account_banned WHERE id = @id AND active = 1",
                new { id = account.Id }) > 0;

            return McpResult.Success(new
            {
                found = true,
                account = new
                {
                    account.Id,
                    account.Username,
                    account.LastIp,
                    lastLogin = account.LastLogin.ToString("u"),
                    account.GmLevel,
                    gmLevelName = WoWLookups.GmLevelName(account.GmLevel),
                    account.Locked,
                    isMuted = account.MuteTime > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    account.Online,
                    isBanned
                },
                characters = characterRows.Select(c => new
                {
                    guid = c.Guid,
                    c.Name,
                    c.Level,
                    race = WoWLookups.RaceName(c.Race),
                    @class = WoWLookups.ClassName(c.ClassId),
                    online = c.Online != 0
                })
            }).ToJson();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "account_lookup failed for {Username}", username);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    // ----- local DTOs (don't pollute Models/) -----

    private sealed class PlayerDetailRow
    {
        public int Guid { get; set; }
        public string Name { get; set; } = "";
        public int Account { get; set; }
        public int Level { get; set; }
        public int Race { get; set; }
        public int ClassId { get; set; }
        public int Gender { get; set; }
        public long Money { get; set; }
        public int Online { get; set; }
        public int Zone { get; set; }
        public int Map { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public long PlayedTotal { get; set; }
        public long PlayedLevel { get; set; }
        public long CreateTime { get; set; }
        public long LogoutTime { get; set; }
    }

    private sealed class AccountSummaryRow
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string LastIp { get; set; } = "";
        public DateTime LastLogin { get; set; }
        public int GmLevel { get; set; }
        public bool Locked { get; set; }
        public long MuteTime { get; set; }
        public int Online { get; set; }
    }

    private sealed class OnlinePlayerRow
    {
        public int Guid { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public int Race { get; set; }
        public int ClassId { get; set; }
        public int Zone { get; set; }
        public int Map { get; set; }
        public string AccountName { get; set; } = "";
    }
}
