using System.ComponentModel;
using MangosSuperUI.BotLogic.Data;
using MangosSuperUI.Mcp.Common;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Quest graph reads. Backed by <see cref="QuestGraphLoader"/>, the in-memory
/// index built at startup from quest_template + creature data.
/// </summary>
[McpServerToolType]
public class QuestTools
{
    private readonly QuestGraphLoader _graph;
    private readonly ILogger<QuestTools> _log;

    public QuestTools(QuestGraphLoader graph, ILogger<QuestTools> log)
    {
        _graph = graph;
        _log = log;
    }

    [McpServerTool(Name = "quest_zones")]
    [Description(
        "All zones that have quests with givers, with quest counts and " +
        "level range. Powers a 'pick a zone' dropdown. Returns empty if " +
        "the quest graph hasn't loaded yet.")]
    public string Zones()
    {
        if (!_graph.IsLoaded)
            return McpResult.Failure(ErrorCodes.Partial, "Quest graph not loaded",
                retryable: true, hint: "wait for startup to finish").ToJson();

        var zones = _graph.AllQuests.Values
            .Where(q => q.ZoneId > 0 && q.Giver != null)
            .GroupBy(q => q.ZoneId)
            .Select(g => new
            {
                zoneId = g.Key,
                questCount = g.Count(),
                minLevel = g.Min(q => q.MinLevel),
                maxLevel = g.Max(q => q.QuestLevel)
            })
            .OrderBy(z => z.minLevel)
            .ThenBy(z => z.zoneId)
            .ToList();
        return McpResult.Success(zones).ToJson();
    }

    [McpServerTool(Name = "quest_zone_chain")]
    [Description(
        "All quests for one zone with chain edges (Prev/Next), kill + item " +
        "objectives, giver + turn-in NPC info, and best-drop-source for " +
        "item objectives (creature entry, name, grind coords). Heavy call — " +
        "filter by zone rather than dumping the whole graph.")]
    public string ZoneChain(
        [Description("Zone id (see quest_zones).")] int zoneId)
    {
        if (!_graph.IsLoaded)
            return McpResult.Failure(ErrorCodes.Partial, "Quest graph not loaded",
                retryable: true, hint: "wait for startup to finish").ToJson();

        var quests = _graph.AllQuests.Values
            .Where(q => q.ZoneId == zoneId && q.Giver != null)
            .Select(q => new
            {
                q.QuestId,
                q.Title,
                q.QuestLevel,
                q.MinLevel,
                q.PrevQuestId,
                q.NextQuestId,
                q.NextQuestInChain,
                q.ExclusiveGroup,
                q.RaceMask,
                q.ClassMask,
                prevQuests = q.PrevQuests,
                hasKillObjectives = q.HasKillObjectives,
                hasItemObjectives = q.HasItemObjectives,
                objectives = q.Objectives.Select(o => new
                {
                    o.Slot, o.CreatureEntry, o.Count, o.TargetName, o.GrindX, o.GrindY
                }),
                itemObjectives = q.ItemObjectives.Select(i => new
                {
                    i.ItemId, i.Count, i.ItemName,
                    bestDrop = i.BestDropSource is null ? null : new
                    {
                        i.BestDropSource.CreatureEntry,
                        i.BestDropSource.CreatureName,
                        i.BestDropSource.GrindX, i.BestDropSource.GrindY
                    }
                }),
                giver = q.Giver is null ? null : new { q.Giver.NpcEntry, q.Giver.Name, q.Giver.X, q.Giver.Y, q.Giver.Map },
                turnIn = q.TurnIn is null ? null : new { q.TurnIn.NpcEntry, q.TurnIn.Name, q.TurnIn.X, q.TurnIn.Y, q.TurnIn.Map }
            })
            .OrderBy(q => q.QuestLevel)
            .ThenBy(q => q.QuestId)
            .ToList();

        return McpResult.Success(new { zoneId, count = quests.Count, quests }).ToJson();
    }

    [McpServerTool(Name = "quest_search")]
    [Description(
        "Search quests by title substring across the entire quest graph. " +
        "Returns up to `limit` matches with quest id, title, level range, " +
        "zone, and giver NPC.")]
    public string Search(
        [Description("Title substring (case-insensitive).")] string query,
        [Description("Max results (default 25, hard cap 100).")] int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return McpResult.Failure(ErrorCodes.InvalidInput, "query must be at least 2 chars").ToJson();
        if (!_graph.IsLoaded)
            return McpResult.Failure(ErrorCodes.Partial, "Quest graph not loaded",
                retryable: true, hint: "wait for startup to finish").ToJson();

        var capped = Math.Clamp(limit, 1, 100);
        var hits = _graph.AllQuests.Values
            .Where(q => q.Title is not null && q.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(q => q.QuestLevel)
            .ThenBy(q => q.QuestId)
            .Take(capped)
            .Select(q => new
            {
                q.QuestId,
                q.Title,
                q.QuestLevel,
                q.MinLevel,
                zoneId = q.ZoneId,
                giver = q.Giver is null ? null : new { q.Giver.NpcEntry, q.Giver.Name }
            })
            .ToList();

        return McpResult.Success(new { query, matched = hits.Count, results = hits }).ToJson();
    }
}
