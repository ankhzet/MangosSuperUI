using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// World map reads: heightmap, gameobject spawns, custom-object catalog.
/// Read-only — writes live in Phase-3 <c>WorldMapWriteTools</c> (TBD).
/// </summary>
[McpServerToolType]
public class WorldMapTools
{
    private const int CustomRangeStart = 900000;

    private readonly ConnectionFactory _db;
    private readonly HeightMapService _heightMap;
    private readonly MinimapTileService _minimap;
    private readonly ILogger<WorldMapTools> _log;

    public WorldMapTools(ConnectionFactory db, HeightMapService heightMap, MinimapTileService minimap, ILogger<WorldMapTools> log)
    {
        _db = db;
        _heightMap = heightMap;
        _minimap = minimap;
        _log = log;
    }

    [McpServerTool(Name = "worldmap_available_maps")]
    [Description(
        "Maps that have minimap tiles decoded. Returns [{ name, tileCount }].")]
    public string AvailableMaps()
    {
        try
        {
            if (_minimap.IsAvailable)
            {
                var maps = _minimap.GetAvailableMaps()
                    .Select(m => new { name = m.Name, tileCount = m.TileCount })
                    .ToList();
                return McpResult.Success(new { source = "mpq", maps }).ToJson();
            }
            return McpResult.Success(new { source = "disk", maps = Array.Empty<object>() }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worldmap_available_maps failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worldmap_tile_index")]
    [Description(
        "Which (row,col) tiles exist on the minimap for a given map. " +
        "Returns an array of [row,col] pairs.")]
    public string TileIndex(
        [Description("Map name (e.g. Azeroth, Kalimdor).")] string map = "Azeroth")
    {
        try
        {
            var safe = Path.GetFileName(map ?? "Azeroth");
            if (_minimap.IsAvailable)
            {
                var tiles = _minimap.GetTileIndex(safe)
                    .Select(t => new[] { t.Row, t.Col })
                    .ToList();
                return McpResult.Success(new { map = safe, tiles }).ToJson();
            }
            return McpResult.Success(new { map = safe, tiles = Array.Empty<int[]>() }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worldmap_tile_index failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worldmap_get_height")]
    [Description(
        "Terrain Z height at a world (x,y) for a given map id. " +
        "Returns { z: null } if heightmap isn't available.")]
    public string GetHeight(
        [Description("Map id (0=Eastern Kingdoms, 1=Kalimdor, instance ids otherwise).")] int map = 0,
        [Description("World X coordinate.")] float x = 0,
        [Description("World Y coordinate.")] float y = 0)
    {
        try
        {
            if (!_heightMap.IsAvailable)
                return McpResult.Success(new { z = (float?)null, available = false }).ToJson();
            var z = _heightMap.GetHeight(map, x, y);
            return McpResult.Success(new { z, available = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worldmap_get_height failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worldmap_spawns")]
    [Description(
        "Gameobject spawns inside a bounding box. map: 0=Eastern Kingdoms, " +
        "1=Kalimdor. Returns up to 5000 spawns with guid, entry, name, type, " +
        "position, orientation. Use `customOnly:true` to filter to 900000+.")]
    public async Task<string> Spawns(
        [Description("Map id.")] int map = 0,
        [Description("Min X (inclusive).")] float minX = -99999f,
        [Description("Max X (inclusive).")] float maxX = 99999f,
        [Description("Min Y (inclusive).")] float minY = -99999f,
        [Description("Max Y (inclusive).")] float maxY = 99999f,
        [Description("true = only custom 900000+ entries.")] bool customOnly = false)
    {
        try
        {
            using var conn = _db.Mangos();
            var sql = @"
                SELECT g.guid, g.id AS entry, g.map,
                       g.position_x AS x, g.position_y AS y, g.position_z AS z,
                       g.orientation, gt.name, gt.type
                FROM gameobject g
                JOIN gameobject_template gt ON gt.entry = g.id
                    AND gt.patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = g.id)
                WHERE g.map = @Map
                  AND g.position_x BETWEEN @MinX AND @MaxX
                  AND g.position_y BETWEEN @MinY AND @MaxY";
            if (customOnly) sql += " AND g.id >= @CustomStart";
            sql += " ORDER BY g.id LIMIT 5000";

            var rows = await conn.QueryAsync<dynamic>(sql, new
            {
                Map = map,
                MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY,
                CustomStart = CustomRangeStart
            });
            return McpResult.Success(new { spawns = rows.ToList() }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worldmap_spawns failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "worldmap_catalog")]
    [Description("All custom gameobject templates (900000+) for the placement picker.")]
    public async Task<string> Catalog()
    {
        try
        {
            using var conn = _db.Mangos();
            var objects = await conn.QueryAsync<dynamic>(@"
                SELECT entry, type, displayId, name
                FROM gameobject_template
                WHERE entry >= @Start
                  AND patch = (SELECT MAX(patch) FROM gameobject_template gt2 WHERE gt2.entry = gameobject_template.entry)
                ORDER BY entry",
                new { Start = CustomRangeStart });
            return McpResult.Success(new { objects = objects.ToList() }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worldmap_catalog failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }
}
