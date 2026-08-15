using System.ComponentModel;
using System.Text.Json;
using MangosSuperUI.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Resources;

/// <summary>
/// Bot-fleet snapshot resource. URI <c>mcp://msui/bots/fleet</c> —
/// the live fleet projection, stalled bots first. Cheap to read;
/// updates as often as the Brain ticks.
/// </summary>
[McpServerResourceType]
public class BotFleetResource
{
    private readonly BotBridgeService _bridge;
    private readonly BotBrainService _brain;

    public BotFleetResource(BotBridgeService bridge, BotBrainService brain)
    {
        _bridge = bridge;
        _brain = brain;
    }

    [McpServerResource(
        Name = "bot_fleet",
        UriTemplate = "mcp://msui/bots/fleet",
        MimeType = "application/json")]
    [Description(
        "Live bot fleet projection: stalled bots first, then everyone. " +
        "Each row carries goal/step/why/timers/pos/target/pending/failure/stall/scratch. " +
        "Includes brain enable flag, connected count, and total tracked.")]
    public ReadResourceResult GetFleet()
    {
        var rows = _brain.GetLiveFleet();
        var payload = new
        {
            capturedAt = DateTimeOffset.UtcNow,
            brainEnabled = _brain.BrainEnabled,
            connected = _bridge.ConnectedCount,
            tracked = _bridge.TotalTracked,
            fleetCount = rows.Count,
            fleet = rows
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents { Uri = "mcp://msui/bots/fleet", Text = json, MimeType = "application/json" }
            }
        };
    }
}
