using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Prompts;

/// <summary>
/// `/triage-griefing {characterName}` — bundled workflow for assessing a
/// griefing report and recommending action.
/// </summary>
[McpServerPromptType]
public class TriageGriefingPrompt
{
    [McpServerPrompt(Name = "triage_griefing", Title = "Triage Griefing Report")]
    [Description(
        "Triage workflow for a griefing report: gather evidence (chat log, " +
        "online state, recent audit, account flags), summarise what likely " +
        "happened, propose exactly one action with its RA command. " +
        "Slash-invoke as /triage-griefing <characterName> [report-context].")]
    public IEnumerable<ChatMessage> Triage(
        [Description("Reported offender character name.")] string characterName,
        [Description("Free-text description of the complaint.")] string? reportContext = null)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            characterName = "(no name supplied)";

        var systemMsg = new ChatMessage(ChatRole.System,
            "You are triaging a griefing report on a WoW 1.12.1 private server. " +
            "Evidence-first: never recommend action without evidence. " +
            "Sequence: (1) `player_search` and `player_detail` for the reported character; " +
            "(2) `audit_target_history` for 'player' AND 'account' targets; " +
            "(3) `log_chat` for the last 2 hours of chat (filter by channel if the report mentions one); " +
            "(4) `account_lookup` for any account flags (muted, banned, locked). " +
            "Summarise the evidence in 2-3 sentences, then propose EXACTLY ONE action " +
            "from this menu with its full RA command line: " +
            "`ra_kick_player`, `ra_mute` (with duration), `ra_ban_account` (with duration + reason). " +
            "Never recommend an action stronger than the evidence supports. " +
            "Do NOT execute the action — present it for confirmation. " +
            "If the evidence is insufficient, say so plainly and ask for more context.");

        var userMsg = new ChatMessage(ChatRole.User,
            $"Triage a griefing report against '{characterName}'." +
            (string.IsNullOrWhiteSpace(reportContext)
                ? " Use whatever chat context you can pull from the recent logs."
                : $" Report context: {reportContext}"));

        return new[] { systemMsg, userMsg };
    }
}
