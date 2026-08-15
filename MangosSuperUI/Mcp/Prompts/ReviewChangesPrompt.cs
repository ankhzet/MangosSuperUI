using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Prompts;

/// <summary>
/// `/review-changes {domain?}` — bundled workflow for reviewing drift +
/// change graph and proposing a revert plan.
/// </summary>
[McpServerPromptType]
public class ReviewChangesPrompt
{
    [McpServerPrompt(Name = "review_changes", Title = "Review Changes")]
    [Description(
        "Review world drift vs baseline + the audit-driven change graph. " +
        "Proposes a revert plan grouped by domain. " +
        "Slash-invoke as /review-changes [optional domain].")]
    public IEnumerable<ChatMessage> Review(
        [Description("Optional domain key: 'items', 'spells', 'world', 'loot'. " +
                     "Empty = all domains.")] string? domain = null,
        [Description("Hours of audit history to consider (default 168 = 1 week).")] int hours = 168)
    {
        var domainClause = string.IsNullOrWhiteSpace(domain) ? "all domains" : $"domain '{domain}'";

        var systemMsg = new ChatMessage(ChatRole.System,
            "You are reviewing custom content changes on a WoW 1.12.1 server. " +
            "Two complementary views: state (current vs OG baseline) and " +
            "events (audit_log + change graph). Always consult BOTH. " +
            $"Scope: {domainClause}; audit window: {hours}h. " +
            "Sequence: (1) `divergence_overview` ('tracked' then 'deep'); " +
            "(2) `divergence_tree` for each affected domain; " +
            "(3) `changegraph_overview` with operator + days filters; " +
            "(4) `changegraph_batches` + `changegraph_entries` for the recent " +
            "revertable batches; (5) `activity_entries` for human-readable audit. " +
            "Synthesise: what drifted, who changed it, and a concrete revert " +
            "plan with the exact `baseline_reset_*` or `changegraph_revert_*` " +
            "tool call(s) needed. Group by domain. " +
            "Mark each item in the plan as low/medium/high risk based on " +
            "potential player impact. NEVER execute a revert — just present the plan.");

        var userMsg = new ChatMessage(ChatRole.User,
            domainClause == "all domains"
                ? "Review all custom changes across every domain. Group by domain."
                : $"Review all changes in {domainClause}.");

        return new[] { systemMsg, userMsg };
    }
}
