using System.ComponentModel;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// DBC lookups. Backed by <see cref="DbcService"/> which parses the WDBC
/// files once at startup. All calls are O(1) dictionary lookups — safe to
/// call freely.
/// </summary>
[McpServerToolType]
public class DbcTools
{
    private readonly DbcService _dbc;
    private readonly ILogger<DbcTools> _log;

    public DbcTools(DbcService dbc, ILogger<DbcTools> log)
    {
        _dbc = dbc;
        _log = log;
    }

    [McpServerTool(Name = "dbc_status")]
    [Description(
        "DBC load status: IsLoaded, LoadError, DbcPath, and per-file row counts " +
        "(item display icons, spell icons, durations, cast times, ranges, spells, " +
        "item model infos, gameobject models, character sections, hair geosets, " +
        "world map zones). Call first if you suspect DBC parsing failed.")]
    public string Status() => McpResult.Success(new
    {
        _dbc.IsLoaded,
        _dbc.LoadError,
        _dbc.DbcPath,
        loadedCounts = _dbc.LoadedCounts
    }).ToJson();

    [McpServerTool(Name = "dbc_item_icon_url")]
    [Description(
        "Resolve an item display ID to its icon web URL (e.g. /Icon/Get?name=inv_sword_39). " +
        "Returns null if the display ID isn't in ItemDisplayInfo.dbc.")]
    public string ItemIconUrl(
        [Description("Item display ID (uint).")] uint displayId)
        => McpResult.Success(new { displayId, iconUrl = _dbc.GetItemIconPath(displayId) }).ToJson();

    [McpServerTool(Name = "dbc_spell_icon_url")]
    [Description(
        "Resolve a spell icon ID to its icon web URL.")]
    public string SpellIconUrl(
        [Description("Spell icon ID (uint).")] uint spellIconId)
        => McpResult.Success(new { spellIconId, iconUrl = _dbc.GetSpellIconPath(spellIconId) }).ToJson();

    [McpServerTool(Name = "dbc_item_model_info")]
    [Description(
        "Full model info for an item display ID: modelNames[2], textureNames[2], " +
        "bodyTextures[8], geosetGroup[3], helmetGeosetVis[2], itemVisualId. " +
        "Returns null when the display ID is unknown.")]
    public string ItemModelInfo(
        [Description("Item display ID (uint).")] uint displayId)
    {
        var info = _dbc.GetItemModelInfo(displayId);
        return McpResult.Success(info is null
            ? new { displayId, found = false }
            : new { displayId, found = true, info }
        ).ToJson();
    }

    [McpServerTool(Name = "dbc_gameobject_model_path")]
    [Description(
        "Resolve a gameobject display ID to its client model path. " +
        "Returns null when the display ID is unknown.")]
    public string GameObjectModelPath(
        [Description("Gameobject display ID (uint).")] uint displayId)
        => McpResult.Success(new { displayId, path = _dbc.GetGameObjectModelPath(displayId) }).ToJson();

    [McpServerTool(Name = "dbc_spell_entry")]
    [Description(
        "Resolve a spell ID to its DBC entry (name, subtext, school, " +
        "spellVisual1, spellIconId, spellLevel, description). " +
        "Returns null when the spell ID is unknown — note DBC covers " +
        "vanilla spells; custom 40000+ entries are in spell_template, not here.")]
    public string SpellEntry(
        [Description("Spell ID (uint).")] uint spellId)
    {
        if (!_dbc.SpellEntries.TryGetValue(spellId, out var entry))
            return McpResult.Success(new { spellId, found = false }).ToJson();
        return McpResult.Success(new { spellId, found = true, entry }).ToJson();
    }

    [McpServerTool(Name = "dbc_spell_duration")]
    [Description(
        "Look up spell duration by ID. Returns DurationMs, DurationPerLevel, " +
        "MaxDurationMs, and a human label (e.g. '30 sec', '1 min').")]
    public string SpellDuration(
        [Description("Spell duration ID (uint).")] uint durationId)
    {
        if (!_dbc.SpellDurations.TryGetValue(durationId, out var d))
            return McpResult.Success(new { durationId, found = false }).ToJson();
        return McpResult.Success(new
        {
            durationId, found = true,
            d.DurationMs, d.DurationPerLevel, d.MaxDurationMs,
            displayLabel = d.DisplayLabel
        }).ToJson();
    }

    [McpServerTool(Name = "dbc_spell_cast_time")]
    [Description(
        "Look up spell cast time by ID. Returns BaseMs, PerLevelMs, MinimumMs, " +
        "and a human label (e.g. '1.5 sec cast').")]
    public string SpellCastTime(
        [Description("Spell cast time ID (uint).")] uint castTimeId)
    {
        if (!_dbc.SpellCastTimes.TryGetValue(castTimeId, out var c))
            return McpResult.Success(new { castTimeId, found = false }).ToJson();
        return McpResult.Success(new
        {
            castTimeId, found = true,
            c.BaseMs, c.PerLevelMs, c.MinimumMs,
            displayLabel = c.DisplayLabel
        }).ToJson();
    }

    [McpServerTool(Name = "dbc_spell_range")]
    [Description(
        "Look up spell range by ID. Returns RangeMin, RangeMax (yards), Flags, " +
        "and DisplayName (e.g. 'Self', '10 yd', '100 yd').")]
    public string SpellRange(
        [Description("Spell range ID (uint).")] uint rangeId)
    {
        if (!_dbc.SpellRanges.TryGetValue(rangeId, out var r))
            return McpResult.Success(new { rangeId, found = false }).ToJson();
        return McpResult.Success(new
        {
            rangeId, found = true,
            r.RangeMin, r.RangeMax, r.Flags, displayName = r.DisplayName
        }).ToJson();
    }

    [McpServerTool(Name = "dbc_professions")]
    [Description(
        "Gear-making professions (Blacksmithing, Leatherworking, Tailoring, " +
        "Engineering) with their recipe spells (rank-ordered) and output items. " +
        "Single call answers 'what does this profession craft?'.")]
    public string Professions()
    {
        var all = new List<object>();
        foreach (var (skillLineId, name) in _dbc.GetProfessions())
        {
            var recipeSpells = _dbc.GetProfessionRecipeSpells(skillLineId);
            var outputs = _dbc.GetProfessionOutputs(skillLineId);
            all.Add(new
            {
                skillLineId,
                name,
                recipeCount = recipeSpells.Count,
                recipes = recipeSpells,
                outputs
            });
        }
        return McpResult.Success(new { count = all.Count, professions = all }).ToJson();
    }

    [McpServerTool(Name = "dbc_reload")]
    [Description(
        "Re-read all DBC files from disk. Use after updating the world data " +
        "(vmaps / dbc / maps) so the in-memory tables reflect the new state. " +
        "Returns the refreshed row counts.")]
    public string Reload()
    {
        try
        {
            _dbc.Reload();
            return McpResult.Success(new
            {
                reloaded = true,
                counts = _dbc.LoadedCounts
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "dbc_reload failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }
}
