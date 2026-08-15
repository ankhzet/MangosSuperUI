using System.ComponentModel;
using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Resources;

/// <summary>
/// Per-player snapshot resource. URL template <c>mcp://msui/players/{guid}</c>
/// — when an agent @-mentions this URI, the server returns the player's
/// full record + the last 20 audit_log entries targeting them.
/// </summary>
[McpServerResourceType]
public class PlayerSnapshotResource
{
    private readonly ConnectionFactory _db;

    public PlayerSnapshotResource(ConnectionFactory db) { _db = db; }

    [McpServerResource(
        Name = "player_snapshot",
        UriTemplate = "mcp://msui/players/{guid}",
        MimeType = "application/json")]
    [Description(
        "Full player snapshot: character + guild + account + last 20 audit " +
        "actions targeting the character. URI path segment {guid} = " +
        "character guid (integer).")]
    public async Task<ReadResourceResult> GetSnapshot(string guid, CancellationToken ct)
    {
        if (!int.TryParse(guid, out var playerGuid) || playerGuid <= 0)
            return new ReadResourceResult
            {
                Contents = new List<ResourceContents>
                {
                    new TextResourceContents
                    {
                        Uri = $"mcp://msui/players/{guid}",
                        Text = JsonSerializer.Serialize(new { error = "guid must be a positive integer", guid }),
                        MimeType = "application/json"
                    }
                }
            };

        var payload = await BuildSnapshot(playerGuid, ct);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents { Uri = $"mcp://msui/players/{playerGuid}", Text = json, MimeType = "application/json" }
            }
        };
    }

    private async Task<object> BuildSnapshot(int playerGuid, CancellationToken ct)
    {
        try
        {
            using var charConn = _db.Characters();
            var player = await charConn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT guid, name, account, level, race, class AS classId, gender, money, online,
                         zone, map, position_x AS posX, position_y AS posY, position_z AS posZ,
                         played_time_total AS playedTotal, played_time_level AS playedLevel,
                         create_time AS createTime, logout_time AS logoutTime
                  FROM characters WHERE guid = @guid",
                new { guid = playerGuid });

            if (player is null)
                return new { found = false, guid = playerGuid };

            var guild = await charConn.QueryFirstOrDefaultAsync<(string Name, int Rank)>(
                @"SELECT g.name, gm.rank FROM guild_member gm
                  JOIN guild g ON g.guild_id = gm.guild_id
                  WHERE gm.guid = @guid",
                new { guid = playerGuid });

            using var realmdConn = _db.Realmd();
            var account = await realmdConn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT id, username, last_ip AS lastIp, last_login AS lastLogin,
                         gmlevel AS gmLevel, locked, mutetime AS muteTime, online
                  FROM account WHERE id = @id",
                new { id = (int)player.account });

            var audit = (await realmdConn.QueryAsync<dynamic>(
                @"SELECT id, `timestamp`, category, action, target_name AS targetName,
                         ra_command AS raCommand, success, notes
                  FROM audit_log
                  WHERE (target_type = 'player' AND target_id = @id) OR operator = @username
                  ORDER BY `timestamp` DESC LIMIT 20",
                new { id = playerGuid, username = (string?)account?.username ?? "" })).ToList();

            return new
            {
                found = true,
                guid = playerGuid,
                player = new
                {
                    player.guid, player.name,
                    accountId = player.account,
                    player.level,
                    race = player.race,
                    @class = player.classId,
                    gender = (int)player.gender == 0 ? "Male" : "Female",
                    gold = (int)player.money / 10000,
                    silver = ((int)player.money % 10000) / 100,
                    copper = (int)player.money % 100,
                    online = (int)player.online != 0,
                    player.zone, player.map, player.posX, player.posY, player.posZ
                },
                guild = guild.Name is null ? null : new { name = guild.Name, rank = guild.Rank },
                account = account is null ? null : new
                {
                    account.id, account.username, account.lastIp,
                    gmLevel = account.gmLevel, online = (int)account.online != 0,
                    locked = (bool)account.locked
                },
                audit = audit
            };
        }
        catch (Exception ex)
        {
            return new { found = false, guid = playerGuid, error = ex.Message };
        }
    }
}
