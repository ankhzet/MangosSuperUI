using System.ComponentModel;
using Dapper;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Patch / spell-builder reads. Wraps the read endpoints of
/// <c>PatchController</c>. All tools default to <c>read</c>; the
/// generation/build tools live in a future phase.
/// </summary>
[McpServerToolType]
public class PatchTools
{
    private readonly ConnectionFactory _db;
    private readonly SpellIconService _icons;
    private readonly ILogger<PatchTools> _log;

    public PatchTools(ConnectionFactory db, SpellIconService icons, ILogger<PatchTools> log)
    {
        _db = db;
        _icons = icons;
        _log = log;
    }

    [McpServerTool(Name = "patch_search_source")]
    [Description(
        "Search source spells (vanilla, used as templates for new custom " +
        "spells). Name substring; returns entry, name, school, level.")]
    public async Task<string> SearchSource(
        [Description("Name substring.")] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return McpResult.Failure(ErrorCodes.InvalidInput, "q required").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var rows = (await conn.QueryAsync<dynamic>(@"
                SELECT entry, name, school, spellLevel
                FROM spell_template
                WHERE name LIKE @q AND entry < 40000
                ORDER BY spellLevel, entry LIMIT 50",
                new { q = $"%{q}%" })).ToList();
            return McpResult.Success(new { count = rows.Count, results = rows }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_search_source failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_source_ranks")]
    [Description(
        "All rank versions of a source spell (linked via spell_chain). " +
        "Useful for picking a rank to clone when generating a custom spell.")]
    public async Task<string> SourceRanks(
        [Description("Source spell entry id.")] int entry)
    {
        if (entry <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "entry must be positive").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var rows = (await conn.QueryAsync<dynamic>(@"
                SELECT entry, name, spellLevel, rank
                FROM spell_template
                WHERE entry = @entry OR entry IN (
                    SELECT spell_id FROM spell_chain WHERE prev_spell = @entry
                       OR first_spell = @entry
                )
                ORDER BY rank",
                new { entry })).ToList();
            return McpResult.Success(new { rootEntry = entry, count = rows.Count, ranks = rows }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_source_ranks failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_skill_tab_map")]
    [Description(
        "Class → skill-tab mapping used when registering custom spells at " +
        "class trainers. Keys: 'Warrior','Paladin','Hunter','Rogue','Priest', " +
        "'DeathKnight','Shaman','Mage','Warlock','Druid'.")]
    public string SkillTabMap()
    {
        var map = SpellCreatorService.GetSkillTabMap();
        return McpResult.Success(new { skillTabs = map }).ToJson();
    }

    [McpServerTool(Name = "patch_search_trainers")]
    [Description(
        "Search trainers by name substring. Returns entry, name, faction, " +
        "position. Useful before patch_register_at_trainer.")]
    public async Task<string> SearchTrainers(
        [Description("Name substring.")] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return McpResult.Failure(ErrorCodes.InvalidInput, "q must be at least 2 chars").ToJson();
        try
        {
            using var conn = _db.Mangos();
            var rows = (await conn.QueryAsync<dynamic>(@"
                SELECT c.entry, c.name, c.faction, c.position_x AS x, c.position_y AS y, c.map
                FROM creature_template c
                WHERE c.name LIKE @q AND c.npcflag & 16 = 16
                ORDER BY c.name LIMIT 50",
                new { q = $"%{q}%" })).ToList();
            return McpResult.Success(new { count = rows.Count, results = rows }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_search_trainers failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_search_icons")]
    [Description("Search custom spell icons by name substring.")]
    public string SearchIcons(
        [Description("Icon filename substring.")] string q,
        [Description("Max results (default 30, hard cap 100).")] int maxResults = 30)
    {
        if (string.IsNullOrWhiteSpace(q))
            return McpResult.Failure(ErrorCodes.InvalidInput, "q required").ToJson();
        var icons = _icons.SearchIcons(q, Math.Clamp(maxResults, 1, 100));
        return McpResult.Success(new { icons }).ToJson();
    }

    [McpServerTool(Name = "patch_custom_spells")]
    [Description(
        "All custom spells in the 40000-49999 range. Returns entry, name, " +
        "school, spellLevel, iconId.")]
    public async Task<string> CustomSpells()
    {
        try
        {
            using var conn = _db.Mangos();
            var rows = (await conn.QueryAsync<dynamic>(@"
                SELECT entry, name, school, spellLevel, iconId
                FROM spell_template
                WHERE entry >= 40000 AND entry < 50000
                ORDER BY entry")).ToList();
            return McpResult.Success(new { count = rows.Count, spells = rows }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "patch_custom_spells failed");
            return McpResult.FromException(ex, ErrorCodes.DbUnavailable).ToJson();
        }
    }

    [McpServerTool(Name = "patch_texture_themes")]
    [Description(
        "Available texture themes (fire, frost, holy, shadow, nature, " +
        "arcane, etc.) used by patch_generate_textures.")]
    public string TextureThemes()
    {
        var themes = SpellTextureService.Themes;
        return McpResult.Success(new { themes }).ToJson();
    }

    [McpServerTool(Name = "patch_class_trainer_template_map")]
    [Description(
        "Class → npc_trainer_template map used to look up the trainer template " +
        "id when calling patch_register_at_class_trainers.")]
    public string ClassTrainerTemplateMap()
    {
        var map = SpellCreatorService.GetClassTrainerTemplateMap();
        return McpResult.Success(new { classTrainerTemplates = map }).ToJson();
    }
}
