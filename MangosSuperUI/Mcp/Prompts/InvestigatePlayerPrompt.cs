using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Prompts;

/// <summary>
/// `/investigate-player {characterName}` — bundled system prompt that
/// instructs the agent to gather player_search → player_detail →
/// account_lookup → audit_target_history and synthesise a report.
/// </summary>
[McpServerPromptType]
public class InvestigatePlayerPrompt
{
    [McpServerPrompt(Name = "investigate_player", Title = "Investigate Player")]
    [Description(
        "Bundle the standard player-investigation workflow: search by name, " +
        "pull full detail + guild + account + recent audit, then synthesise " +
        "a triage report. Slash-invoke as /investigate-player <name>.")]
    public IEnumerable<ChatMessage> Investigate(
        [Description("Character name to investigate (exact or partial).")] string characterName,
        [Description("Optional notes from the caller (e.g. 'reported by ticket #1234').")] string? context = null)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            characterName = "(no name supplied)";

        var systemMsg = new ChatMessage(ChatRole.System,
            "You are an MSUI server-admin assistant investigating a player. " +
            "Always use the MCP tools — never assume. " +
            "Sequence: (1) `player_search` with the name; (2) if multiple matches, " +
            "ask the user to disambiguate; (3) `player_detail` on the matching guid; " +
            "(4) `account_lookup` on the owning account; (5) `audit_target_history` for " +
            "player + account; (6) `log_chat` for the last 24h. " +
            "Synthesise: who they are, what's happened to them recently, whether " +
            "anything in the audit log looks suspicious, and what (if anything) " +
            "the operator should do. Call out low-hanging moderation actions " +
            "(mute, kick, ban) with their exact RA command line, but never " +
            "execute them without explicit confirmation.");

        var userMsg = new ChatMessage(ChatRole.User,
            $"Investigate the player named '{characterName}'." +
            (string.IsNullOrWhiteSpace(context) ? "" : $" Context: {context}"));

        return new[] { systemMsg, userMsg };
    }
}
