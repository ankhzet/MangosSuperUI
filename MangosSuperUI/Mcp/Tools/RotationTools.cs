using System.ComponentModel;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Combat-rotation management. Profiles are JSON files in
/// <c>Rotations/</c>; assignments persist in <c>Rotations/assignments.json</c>.
/// Reads default to <c>read</c>; assign/clear require <c>bots</c>.
/// </summary>
[McpServerToolType]
public class RotationTools
{
    private readonly RotationService _rotations;
    private readonly McpCallContext _ctx;
    private readonly ILogger<RotationTools> _log;

    public RotationTools(RotationService rotations, McpCallContext ctx, ILogger<RotationTools> log)
    {
        _rotations = rotations;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "rotation_list")]
    [Description(
        "All rotation profiles (from JSON files in Rotations/) + every " +
        "active bot → profile assignment.")]
    public string List()
    {
        var profiles = _rotations.LoadProfiles();
        var assignments = _rotations.Assignments;
        return McpResult.Success(new
        {
            profileCount = profiles.Count,
            profiles = profiles.Select(p => new
            {
                p.Name, p.Description,
                instructionCount = p.Instructions?.Count ?? 0
            }),
            assignmentCount = assignments.Count,
            assignments = assignments.Select(kv => new { bot = kv.Key, profile = kv.Value })
        }).ToJson();
    }

    [McpServerTool(Name = "rotation_assignments")]
    [Description("Just the bot → profile assignment map.")]
    public string Assignments() => McpResult.Success(new
    {
        assignments = _rotations.Assignments
            .Select(kv => new { bot = kv.Key, profile = kv.Value })
    }).ToJson();

    [McpServerTool(Name = "rotation_get_profile")]
    [Description("One rotation profile by name — full instruction list.")]
    public string GetProfile(
        [Description("Profile name (case-insensitive).")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return McpResult.Failure(ErrorCodes.InvalidInput, "name required").ToJson();
        var p = _rotations.FindProfile(name);
        return McpResult.Success(p is null ? new { found = false, name } : new { found = true, profile = p }).ToJson();
    }

    [McpServerTool(Name = "rotation_assign")]
    [McpCapability(McpCapability.Bots)]
    [Description(
        "Assign a profile to a bot. Persists to Rotations/assignments.json. " +
        "If the bot is online, pushes the new instructions immediately.")]
    public async Task<string> Assign(
        [Description("Bot name (case-insensitive).")] string botName,
        [Description("Profile name.")] string profileName)
    {
        if (string.IsNullOrWhiteSpace(botName) || string.IsNullOrWhiteSpace(profileName))
            return McpResult.Failure(ErrorCodes.InvalidInput, "botName and profileName required").ToJson();
        try
        {
            var result = await _rotations.AssignAsync(botName, profileName);
            return McpResult.Success(new { result }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "rotation_assign failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "rotation_clear")]
    [McpCapability(McpCapability.Bots)]
    [Description(
        "Clear a bot's rotation assignment. Pushes an empty slate if the bot " +
        "is online.")]
    public async Task<string> Clear(
        [Description("Bot name.")] string botName)
    {
        if (string.IsNullOrWhiteSpace(botName))
            return McpResult.Failure(ErrorCodes.InvalidInput, "botName required").ToJson();
        try
        {
            var result = await _rotations.ClearAsync(botName);
            return McpResult.Success(new { result }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "rotation_clear failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }
}
