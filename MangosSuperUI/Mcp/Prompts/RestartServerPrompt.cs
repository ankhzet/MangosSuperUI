using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Prompts;

/// <summary>
/// `/restart-server` — bundled workflow for a safe world-server restart.
/// </summary>
[McpServerPromptType]
public class RestartServerPrompt
{
    [McpServerPrompt(Name = "restart_server", Title = "Restart Server")]
    [Description(
        "Bundle the safe-restart workflow: preflight → save_all → shutdown " +
        "with delay → restart → health check. Slash-invoke as /restart-server " +
        "[optional delay-seconds].")]
    public IEnumerable<ChatMessage> Restart(
        [Description("Seconds to delay before shutdown (default 5).")] int delaySeconds = 5)
    {
        var systemMsg = new ChatMessage(ChatRole.System,
            "You are restarting the MSUI world server. Always confirm with the operator " +
            "BEFORE invoking `ra_shutdown` and `process_restart_mangosd`. " +
            "Sequence: (1) call `worlds_status`; (2) report online player count; " +
            "(3) confirm the operator wants to proceed; (4) call `ra_save_all`; " +
            "(5) call `ra_shutdown` with delay=" + delaySeconds + "s; " +
            "(6) call `process_restart_mangosd`; (7) wait a few seconds then " +
            "call `home_status` and `home_diagnose` to confirm the world came back. " +
            "If anything fails at any step, STOP and surface the error to the operator. " +
            "Do NOT chain additional changes after a failed restart.");

        var userMsg = new ChatMessage(ChatRole.User,
            $"Restart mangosd with a {delaySeconds}-second delay. " +
            "Show me the online player count first and ask for confirmation.");

        return new[] { systemMsg, userMsg };
    }
}
