using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Read-only queries against gameobject_template + spawns. Mirrors
/// <c>GameObjectsController</c>'s search/detail/custom-summary endpoints.
/// </summary>
[McpServerToolType]
public class GameObjectTools
{
    private const int CustomRangeStart = 900000;

    private readonly ConnectionFactory _db;
    private readonly DbcService _dbc;
    private readonly GameObjectModelService _goModels;
    private readonly ILogger<GameObjectTools> _log;

    public GameObjectTools(ConnectionFactory db, DbcService dbc, GameObjectModelService goModels, ILogger<GameObjectTools> log)
    {
        _db = db;
        _dbc = dbc;
        _goModels = goModels;
        _log = log;
    }

    [McpServerTool(Name = "gameobject_search")]
    [Description(
        "Paginated gameobject_template search. Numeric `query` matches entry id; " +
        "non-numeric matches name substring. Optional type filter (e.g. 5 for " +
        "generic chest, 8 for mage book) and `customOnly` to limit to 900000+.")]
    public async Task<string> Search(
        [Description("Search term (entry id or name substring).")] string? query = null,
        [Description("gameobject type id filter.")] int? typeFilter = null,
        [Description("true = only items in 900000+ range.")] bool customOnly = false,
        [Description("Page number, 1-based.")] int page = 1,
        [Description("Page size, hard-capped at 100.")] int pageSize = 50)
    {
        var capped = Math.Clamp(pageSize, 1, 100);
        try
        {
            using var conn = _db.Mangos();
            var where = "WHERE patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)";
            var p = new DynamicParameters();
            if (customOnly) { where += " AND entry >= @CustomStart"; p.Add("CustomStart", CustomRangeStart); }
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (uint.TryParse(query.Trim(), out var entryId))
                {
                    where += " AND entry = @EntryId";
                    p.Add("EntryId", entryId);
                }
                else
                {
                    where += " AND name LIKE @Search";
                    p.Add("Search", $"%{query.Trim()}%");
                }
            }
            if (typeFilter.HasValue) { where += " AND type = @Type"; p.Add("Type", typeFilter.Value); }

            var totalCount = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM gameobject_template {where}", p);
            p.Add("Offset", (page - 1) * capped);
            p.Add("PageSize", capped);

            var rows = (await conn.QueryAsync<dynamic>($@"
                SELECT entry, type, displayId, name, faction, flags, size,
                       data0, data1, data2, data3, data4, data5, data6,
                       data7, data8, data9, data10, data11, data12
                FROM gameobject_template {where}
                ORDER BY entry ASC
                LIMIT @PageSize OFFSET @Offset", p)).ToList();

            var modelMap = new Dictionary<uint, string>();
            foreach (var obj in rows)
            {
                uint did = (uint)(obj.displayId ?? 0);
                if (did > 0 && !modelMap.ContainsKey(did) && _goModels.HasModel(did))
                    modelMap[did] = _goModels.TryGetCachedWebPath(did) ?? "ondemand";
            }

            return McpResult.Success(new
            {
                objects = rows,
                modelMap,
                totalCount,
                page,
                pageSize = capped,
                totalPages = (int)Math.Ceiling((double)totalCount / capped)
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_search failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "gameobject_detail")]
    [Description(
        "Full gameobject record by entry id, including its spawns (guid, map, " +
        "position, rotation, spawnMask, phaseMask, state) and the client model " +
        "path. Returns {found:false} when the entry doesn't exist.")]
    public async Task<string> Detail(
        [Description("Gameobject entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT entry, type, displayId, name, icon, faction, flags, size,
                         data0, data1, data2, data3, data4, data5, data6, data7,
                         data8, data9, data10, data11, data12, data13, data14, data15,
                         data16, data17, data18, data19, data20, data21, data22, data23
                  FROM gameobject_template
                  WHERE entry = @entry
                  ORDER BY patch DESC LIMIT 1",
                new { entry });
            if (row is null)
                return McpResult.Success(new { found = false, entry }).ToJson();

            uint did = (uint)(row.displayId ?? 0);
            var modelPath = did > 0 ? _dbc.GetGameObjectModelPath(did) : null;
            var modelWebPath = did > 0 ? _goModels.TryGetCachedWebPath(did) : null;

            var spawns = (await conn.QueryAsync<dynamic>(@"
                SELECT guid, id, map, position_x AS posX, position_y AS posY, position_z AS posZ,
                       orientation, rotation0, rotation1, rotation2, rotation3,
                       spawnMask, phaseMask, state
                FROM gameobject WHERE id = @id",
                new { id = entry })).ToList();

            return McpResult.Success(new
            {
                found = true,
                entry,
                row,
                modelPath,
                modelWebPath,
                spawns
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_detail failed for entry {Entry}", entry);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "gameobject_custom_summary")]
    [Description(
        "All custom gameobjects (900000+) grouped by type, with per-type counts.")]
    public async Task<string> CustomSummary()
    {
        try
        {
            using var conn = _db.Mangos();
            var rows = await conn.QueryAsync<(int Type, int Count)>(
                @"SELECT type, COUNT(*) FROM gameobject_template
                  WHERE entry >= @start AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
                  GROUP BY type ORDER BY type",
                new { start = CustomRangeStart });
            return McpResult.Success(new
            {
                start = CustomRangeStart,
                grouped = rows.Select(r => new { type = r.Type, typeLabel = GetTypeLabel(r.Type), count = r.Count })
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_custom_summary failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "gameobject_next_custom_id")]
    [Description("Next free entry in the 900000+ custom-gameobject range.")]
    public async Task<string> NextCustomId()
    {
        try
        {
            using var conn = _db.Mangos();
            var max = await conn.ExecuteScalarAsync<int?>(
                $"SELECT MAX(entry) FROM gameobject_template WHERE entry >= {CustomRangeStart}");
            var next = (max ?? CustomRangeStart - 1) + 1;
            return McpResult.Success(new { nextEntry = next }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_next_custom_id failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "gameobject_full_row")]
    [Description(
        "ALL editable columns for one gameobject entry — used by an agent that " +
        "wants to compose a follow-up edit. Use gameobject_detail first if you " +
        "want the human-readable shape with icon + model + spawns.")]
    public async Task<string> FullRow(
        [Description("Gameobject entry id.")] int entry)
    {
        if (entry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(
                @"SELECT * FROM gameobject_template
                  WHERE entry = @entry
                  ORDER BY patch DESC LIMIT 1",
                new { entry });
            return McpResult.Success(new { found = row is not null, entry, row }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_full_row failed for entry {Entry}", entry);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "gameobject_quest_name")]
    [Description("Resolve a quest ID to its title. Returns null when the quest doesn't exist.")]
    public async Task<string> QuestName(
        [Description("Quest template entry id.")] int questId)
    {
        if (questId <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "questId must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var title = await conn.QueryFirstOrDefaultAsync<string?>(
                "SELECT Title FROM quest_template WHERE entry = @q",
                new { q = questId });
            return McpResult.Success(new { questId, title }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "gameobject_quest_name failed for {QuestId}", questId);
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    // ----- helpers -----

    private static string GetTypeLabel(int type) => type switch
    {
        0 => "DOOR",
        1 => "BUTTON",
        2 => "QUESTGIVER",
        3 => "CHEST",
        4 => "BINDER",
        5 => "GENERIC",
        6 => "TRAP",
        7 => "CHAIR",
        8 => "SPELL_FOCUS",
        9 => "TEXT",
        10 => "GOOBER",
        11 => "TRANSPORT",
        12 => "MO_TRANSPORT",
        13 => "MAP",
        14 => "DUNGEON_DIFFICULTY",
        15 => "BARBER_CHAIR",
        16 => "BLACKSMITH_ANVIL",
        17 => "MAILBOX",
        18 => "TABARD",
        19 => "STABLE",
        20 => "GUILD_BANK",
        21 => "MO_TRANSPORT",
        22 => "SPELL_CASTER",
        23 => "MEETING_STONE",
        24 => "FLAGSTAND",
        25 => "FISHING_HOLE",
        26 => "FLAGDROP",
        _ => $"TYPE_{type}"
    };
}
