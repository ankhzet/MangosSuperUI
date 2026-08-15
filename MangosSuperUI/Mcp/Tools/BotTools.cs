using System.ComponentModel;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Bot commands and queries. Wraps <c>BotBridgeService</c>,
/// <c>BotBrainService</c>, and <c>BotDiagnosticsService</c>.
///
/// Reads default to <c>read</c>; bot commands require <c>bots</c>.
/// All command tools route the request through the bot TCP bridge and
/// stamp audit_log with the caller's label.
/// </summary>
[McpServerToolType]
public class BotTools
{
    private readonly BotBridgeService _bridge;
    private readonly BotBrainService _brain;
    private readonly BotDiagnosticsService _diag;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<BotTools> _log;

    public BotTools(BotBridgeService bridge, BotBrainService brain,
        BotDiagnosticsService diag, AuditService audit,
        McpCallContext ctx, ILogger<BotTools> log)
    {
        _bridge = bridge;
        _brain = brain;
        _diag = diag;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    // ===================== READS =====================

    [McpServerTool(Name = "bot_list")]
    [Description(
        "Roster of every known bot: guid, name, race, classId, level, " +
        "current position, health/mana, in-combat flag, party membership. " +
        "Includes bots that disconnected but whose last state is cached.")]
    public string List()
    {
        var states = _bridge.GetAllBotStates();
        return McpResult.Success(new
        {
            count = states.Count,
            connected = _bridge.ConnectedCount,
            tracked = _bridge.TotalTracked,
            bots = states.Select(s => new
            {
                s.Guid, s.Name, s.Race, s.ClassId, s.Level,
                s.MapId, s.ZoneId, s.X, s.Y, s.Z,
                s.Health, s.MaxHealth, s.Mana, s.MaxMana,
                s.InCombat, s.IsDead, s.InPlayerParty
            })
        }).ToJson();
    }

    [McpServerTool(Name = "bot_state")]
    [Description("Full BotState for a single bot by guid — all 30+ fields.")]
    public string State(
        [Description("Bot guid.")] int guid)
    {
        if (guid <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "guid must be positive").ToJson();
        var s = _bridge.GetBotState(guid);
        return McpResult.Success(s is null ? new { found = false, guid } : new { found = true, state = s }).ToJson();
    }

    [McpServerTool(Name = "bot_fleet_state")]
    [Description(
        "Live fleet projection: stalled bots first, then everyone. Each row " +
        "carries goal/step/why/timers/pos/target/pending/failure/stall/scratch.")]
    public string FleetState()
    {
        var rows = _brain.GetLiveFleet();
        return McpResult.Success(new { count = rows.Count, bots = rows }).ToJson();
    }

    [McpServerTool(Name = "bot_brain_state")]
    [Description("DecisionEngine summary for one bot — activity, personality, copper, slots, quest progress.")]
    public string BrainState(
        [Description("Bot guid.")] int guid)
    {
        if (guid <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "guid must be positive").ToJson();
        var s = _brain.GetBotBrainSummary(guid);
        return McpResult.Success(s is null ? new { found = false, guid } : new { found = true, summary = s }).ToJson();
    }

    [McpServerTool(Name = "bot_brain_status")]
    [Description(
        "Brain enable flag, active bot count, group roster, per-bot activity summary.")]
    public string BrainStatus()
    {
        var all = _brain.AllBots.Values.ToList();
        return McpResult.Success(new
        {
            brainEnabled = _brain.BrainEnabled,
            activeBotCount = _brain.ActiveBotCount,
            rosterCount = all.Count,
            roster = all.Select(b => new { b.Guid, b.Name, b.ClassId, b.Level })
        }).ToJson();
    }

    [McpServerTool(Name = "bot_live_state")]
    [Description("Per-bot context projection (goal/step/why/timers/pos/target/pending/failure/stall/scratch).")]
    public string LiveState(
        [Description("Bot guid.")] int guid)
    {
        if (guid <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "guid must be positive").ToJson();
        var s = _brain.GetLiveState(guid);
        return McpResult.Success(s is null ? new { found = false, guid } : new { found = true, state = s }).ToJson();
    }

    [McpServerTool(Name = "bot_fleet_report")]
    [Description(
        "Bounded text rollup of the whole fleet. Runs `bot_run_report.sh` via " +
        "BotDiagnosticsService — same journald-backed report the dashboard shows.")]
    public async Task<string> FleetReport(
        [Description("Optional pid override (default: auto-detect).")] int? pid = null,
        CancellationToken ct = default)
    {
        try
        {
            var r = await _diag.RunFleetReportAsync(pid, ct);
            return McpResult.Success(new { r.Ok, r.ExitCode, r.Stdout, r.Stderr }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_fleet_report failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_diag")]
    [Description("Run `bot_diag.sh <name>` and return stdout/stderr.")]
    public async Task<string> Diag(
        [Description("Bot name (alnum/underscore/dash only).")] string botName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botName))
            return McpResult.Failure(ErrorCodes.InvalidInput, "botName required").ToJson();
        try
        {
            var r = await _diag.RunBotDiagAsync(botName, ct);
            return McpResult.Success(new { r.Ok, r.ExitCode, r.Stdout, r.Stderr }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_diag failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    // ===================== BOT COMMANDS =====================

    [McpServerTool(Name = "bot_spawn")]
    [McpCapability(McpCapability.Bots)]
    [Description(
        "Spawn N bots via `.bot addai <class> <race> <name>`. Whitelist-validated " +
        "class/race. Returns the names spawned.")]
    public async Task<string> Spawn(
        [Description("Bot class id (1=warrior .. 11=druid).")] int classId,
        [Description("Bot race id (1=human .. 8=troll).")] int raceId,
        [Description("Count to spawn.")] int count)
    {
        if (classId <= 0 || raceId <= 0 || count <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "classId, raceId, count must be positive").ToJson();
        try
        {
            await _bridge.SendToAllBotsAsync("ADD_AI", new { classId, raceId, count });
            await AuditBots($"spawn x{count} class={classId} race={raceId}");
            return McpResult.Success(new { success = true, count }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_spawn failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_spawn_all")]
    [McpCapability(McpCapability.Bots)]
    [Description("Spawn every persisted bot from the bot registry via `.bot add_all`.")]
    public async Task<string> SpawnAll()
    {
        try
        {
            await _bridge.SendToAllBotsAsync("ADD_ALL", new { });
            await AuditBots("spawn_all");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_spawn_all failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_move_to")]
    [McpCapability(McpCapability.Bots)]
    [Description("Teleport a bot to map(x,y,z).")]
    public async Task<string> MoveTo(
        [Description("Bot guid.")] int guid,
        [Description("Map id.")] int mapId,
        [Description("World X.")] float x,
        [Description("World Y.")] float y,
        [Description("World Z.")] float z)
    {
        if (guid <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "guid must be positive").ToJson();
        try
        {
            await _bridge.SendMoveToAsync(guid, mapId, x, y, z);
            await AuditBots($"move_to guid={guid} to ({mapId},{x},{y},{z})");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_move_to failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_say_text")]
    [McpCapability(McpCapability.Bots)]
    [Description(
        "Make a bot speak in chat. chatType 0=say, 1=yell, 2=whisper (target), " +
        "3=party, 4=guild, 5=channel (channel name).")]
    public async Task<string> SayText(
        [Description("Bot guid.")] int guid,
        [Description("Text to say.")] string text,
        [Description("Chat type (0=say, 1=yell, 2=whisper, 3=party, 4=guild, 5=channel).")] int chatType = 0,
        [Description("Whisper target name (chatType=2).")] string? target = null,
        [Description("Channel name (chatType=5).")] string? channel = null)
    {
        if (guid <= 0 || string.IsNullOrWhiteSpace(text))
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid and text required").ToJson();
        try
        {
            await _bridge.SendSayTextAsync(guid, text, chatType, target, channel);
            await AuditBots($"say_text guid={guid}: {text}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_say_text failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_accept_quest")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push ACCEPT_QUEST to a bot for a specific quest entry.")]
    public async Task<string> AcceptQuest(
        [Description("Bot guid.")] int guid,
        [Description("Quest entry id.")] int questEntry)
    {
        if (guid <= 0 || questEntry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid and questEntry required").ToJson();
        try
        {
            await _bridge.SendAcceptQuestAsync(guid, questEntry);
            await AuditBots($"accept_quest guid={guid} quest={questEntry}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_accept_quest failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_complete_quest")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push COMPLETE_QUEST — turn in the bot's active quest to the turn-in NPC.")]
    public async Task<string> CompleteQuest(
        [Description("Bot guid.")] int guid,
        [Description("Quest entry id.")] int questEntry)
    {
        if (guid <= 0 || questEntry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid and questEntry required").ToJson();
        try
        {
            await _bridge.SendCompleteQuestAsync(guid, questEntry);
            await AuditBots($"complete_quest guid={guid} quest={questEntry}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_complete_quest failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_abandon_quest")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push ABANDON_QUEST to a bot.")]
    public async Task<string> AbandonQuest(
        [Description("Bot guid.")] int guid,
        [Description("Quest entry id.")] int questEntry)
    {
        if (guid <= 0 || questEntry <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid and questEntry required").ToJson();
        try
        {
            await _bridge.SendAbandonQuestAsync(guid, questEntry);
            await AuditBots($"abandon_quest guid={guid} quest={questEntry}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_abandon_quest failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_learn_spell")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push LEARN_SPELL to a bot.")]
    public async Task<string> LearnSpell(
        [Description("Bot guid.")] int guid,
        [Description("Spell entry id.")] int spellId)
    {
        if (guid <= 0 || spellId <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid and spellId required").ToJson();
        try
        {
            await _bridge.SendLearnSpellAsync(guid, spellId);
            await AuditBots($"learn_spell guid={guid} spell={spellId}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_learn_spell failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_attack_target")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push ATTACK_TARGET — bot engages a target guid.")]
    public async Task<string> AttackTarget(
        [Description("Bot guid.")] int guid,
        [Description("Target guid.")] int targetGuid)
    {
        if (guid <= 0 || targetGuid <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid and targetGuid required").ToJson();
        try
        {
            await _bridge.SendAttackTargetAsync(guid, targetGuid);
            await AuditBots($"attack_target guid={guid} target={targetGuid}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_attack_target failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_interact_npc")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push INTERACT_NPC — bot right-clicks an NPC guid.")]
    public async Task<string> InteractNpc(
        [Description("Bot guid.")] int guid,
        [Description("NPC guid.")] int npcGuid)
    {
        if (guid <= 0 || npcGuid <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid and npcGuid required").ToJson();
        try
        {
            await _bridge.SendInteractNpcAsync(guid, npcGuid);
            await AuditBots($"interact_npc guid={guid} npc={npcGuid}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_interact_npc failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_take_flight")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push TAKE_FLIGHT between two taxi nodes.")]
    public async Task<string> TakeFlight(
        [Description("Bot guid.")] int guid,
        [Description("Source taxi node id.")] int sourceNode,
        [Description("Destination taxi node id.")] int destNode)
    {
        if (guid <= 0 || sourceNode <= 0 || destNode <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid, sourceNode, destNode required").ToJson();
        try
        {
            await _bridge.SendTakeFlightAsync(guid, sourceNode, destNode);
            await AuditBots($"take_flight guid={guid} {sourceNode}->{destNode}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_take_flight failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_set_task_grind")]
    [McpCapability(McpCapability.Bots)]
    [Description(
        "Push SET_TASK GRIND — assign a grind task at (x,y,z) targeting " +
        "creatureEntry with optional killCount.")]
    public async Task<string> SetTaskGrind(
        [Description("Bot guid.")] int guid,
        [Description("Center X.")] float x,
        [Description("Center Y.")] float y,
        [Description("Center Z.")] float z,
        [Description("Grind radius (default 40).")] float radius = 40f,
        [Description("Target creature entry id (0 = any).")] int creatureEntry = 0,
        [Description("Kill count target (0 = unlimited).")] int killCount = 0)
    {
        if (guid <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "guid required").ToJson();
        try
        {
            await _bridge.SendSetTaskGrindAsync(guid, x, y, z, radius, creatureEntry, killCount);
            await AuditBots($"set_task_grind guid={guid} at ({x},{y},{z}) r={radius}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_set_task_grind failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_set_task_idle")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push SET_TASK IDLE — bot stops its current task and stands down.")]
    public async Task<string> SetTaskIdle(
        [Description("Bot guid.")] int guid)
    {
        if (guid <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "guid required").ToJson();
        try
        {
            await _bridge.SendSetTaskIdleAsync(guid);
            await AuditBots($"set_task_idle guid={guid}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_set_task_idle failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_gear_up")]
    [McpCapability(McpCapability.Bots)]
    [Description("Push GEAR_UP — auto-equip the bot and summon a mount.")]
    public async Task<string> GearUp(
        [Description("Bot guid.")] int guid,
        [Description("Level to scale gear for (default 60).")] int level = 60,
        [Description("Mount item entry id (default 8630 = Palomino).")] int mountItem = 8630,
        [Description("Riding skill (1=yes, 0=no, default 1).")] int riding = 1)
    {
        if (guid <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "guid required").ToJson();
        try
        {
            await _bridge.SendGearUpAsync(guid, level, mountItem, riding);
            await AuditBots($"gear_up guid={guid} level={level}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_gear_up failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_toggle_brain")]
    [McpCapability(McpCapability.Bots)]
    [Description("Enable or disable the global bot brain. Disabling wipes roster state.")]
    public async Task<string> ToggleBrain(
        [Description("true = enabled, false = disabled.")] bool enabled)
    {
        try
        {
            _brain.BrainEnabled = enabled;
            await AuditBots($"toggle_brain enabled={enabled}");
            return McpResult.Success(new { brainEnabled = enabled }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_toggle_brain failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_form_group")]
    [McpCapability(McpCapability.Bots)]
    [Description("Form one group with a leader guid + follower guids.")]
    public async Task<string> FormGroup(
        [Description("Leader bot guid.")] int leaderGuid,
        [Description("Follower bot guids.")] int[] followerGuids)
    {
        if (leaderGuid <= 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "leaderGuid required").ToJson();
        try
        {
            var g = await _brain.FormGroupAsync(leaderGuid, followerGuids ?? Array.Empty<int>());
            await AuditBots($"form_group leader={leaderGuid} followers={(followerGuids?.Length ?? 0)}");
            return McpResult.Success(new { group = g }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_form_group failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_disband_group")]
    [McpCapability(McpCapability.Bots)]
    [Description("Disband a bot group by id.")]
    public async Task<string> DisbandGroup(
        [Description("Group id.")] int groupId)
    {
        if (groupId <= 0) return McpResult.Failure(ErrorCodes.InvalidInput, "groupId required").ToJson();
        try
        {
            await _brain.DisbandGroupAsync(groupId);
            await AuditBots($"disband_group id={groupId}");
            return McpResult.Success(new { success = true }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_disband_group failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_auto_form_groups")]
    [McpCapability(McpCapability.Bots)]
    [Description("Auto-form bot groups based on current grouping mode.")]
    public async Task<string> AutoFormGroups()
    {
        try
        {
            var groups = await _brain.AutoFormGroupsAsync();
            await AuditBots($"auto_form_groups formed={groups.Count}");
            return McpResult.Success(new { groups }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_auto_form_groups failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "bot_set_grouping_mode")]
    [McpCapability(McpCapability.Bots)]
    [Description(
        "Persist grouping mode (0=Off, 1=Sticky, 2=Opportunistic). " +
        "Sends FORM_GROUP to leaders per current roster.")]
    public async Task<string> SetGroupingMode(
        [Description("0=Off, 1=Sticky, 2=Opportunistic.")] int mode)
    {
        try
        {
            await _brain.SetGroupingModeAsync((GroupingMode)mode);
            await AuditBots($"set_grouping_mode mode={mode}");
            return McpResult.Success(new { success = true, mode }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "bot_set_grouping_mode failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    // ----- helpers -----

    private async Task AuditBots(string notes)
    {
        await _audit.LogAsync(new AuditEntry
        {
            Operator = _ctx.Operator,
            OperatorIp = _ctx.RemoteIp,
            Category = "BotBridge",
            Action = "bot_command",
            TargetType = "bot",
            TargetName = "(fleet)",
            StateAfter = System.Text.Json.JsonSerializer.Serialize(new { notes }),
            IsReversible = false,
            Success = true,
            Notes = notes
        });
    }
}
