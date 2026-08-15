using System.ComponentModel;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Per-player RA actions. Thin wrappers that send well-formed RA commands
/// and route every call through <see cref="AuditService"/>. All tools
/// require the <c>ra</c> capability.
/// </summary>
[McpServerToolType]
public class PlayerWriteTools
{
    private readonly RaService _ra;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<PlayerWriteTools> _log;

    public PlayerWriteTools(RaService ra, AuditService audit,
        McpCallContext ctx, ILogger<PlayerWriteTools> log)
    {
        _ra = ra;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    private async Task<string> Run(string command, string notes)
    {
        try
        {
            var (response, success) = await _audit.ExecuteAndLogAsync(
                _ra, command, operator_: _ctx.Operator, operatorIp: _ctx.RemoteIp, notes: notes);
            return success
                ? McpResult.Success(new { response }).ToJson()
                : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RA command {Command} failed", command);
            return McpResult.FromException(ex, ErrorCodes.RaDisconnected).ToJson();
        }
    }

    [McpServerTool(Name = "player_revive")]
    [McpCapability(McpCapability.Ra)]
    [Description("Revive a character by name. RA: `.revive <name>`.")]
    public Task<string> Revive([Description("Character name.")] string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson());
        return Run($".revive {characterName}", $"MCP: player_revive {characterName}");
    }

    [McpServerTool(Name = "player_reset_talents")]
    [McpCapability(McpCapability.Ra)]
    [Description("Reset a character's talents. RA: `.reset talents <name>`.")]
    public Task<string> ResetTalents([Description("Character name.")] string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson());
        return Run($".reset talents {characterName}", $"MCP: player_reset_talents {characterName}");
    }

    [McpServerTool(Name = "player_reset_spells")]
    [McpCapability(McpCapability.Ra)]
    [Description("Reset a character's spells. RA: `.reset spells <name>`.")]
    public Task<string> ResetSpells([Description("Character name.")] string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson());
        return Run($".reset spells {characterName}", $"MCP: player_reset_spells {characterName}");
    }

    [McpServerTool(Name = "player_reset_all")]
    [McpCapability(McpCapability.Ra)]
    [Description("Reset a character's talents AND spells. RA: `.reset all <name>`.")]
    public Task<string> ResetAll([Description("Character name.")] string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson());
        return Run($".reset all {characterName}", $"MCP: player_reset_all {characterName}");
    }

    [McpServerTool(Name = "player_mute")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Mute a character for `minutes`. RA: `.mute <name> <minutes> <reason>`. " +
        "Reason is recorded in audit_log.")]
    public Task<string> Mute(
        [Description("Character name.")] string characterName,
        [Description("Mute duration in minutes.")] int minutes,
        [Description("Reason for the mute (recorded in audit_log).")] string reason)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson());
        if (minutes <= 0)
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "minutes must be positive").ToJson());
        if (string.IsNullOrWhiteSpace(reason))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "reason required").ToJson());
        return Run($".mute {characterName} {minutes} {reason}", $"MCP: player_mute {characterName} {minutes}m reason={reason}");
    }

    [McpServerTool(Name = "player_unmute")]
    [McpCapability(McpCapability.Ra)]
    [Description("Unmute a character. RA: `.unmute <name>`.")]
    public Task<string> Unmute([Description("Character name.")] string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson());
        return Run($".unmute {characterName}", $"MCP: player_unmute {characterName}");
    }

    [McpServerTool(Name = "player_teleport")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Teleport a character to a location. Use a vanilla location name " +
        "(Stormwind, Orgrimmar, Dalaran, etc.) or 'map x y z' for raw coords. " +
        "RA: `.teleport <spec>`.")]
    public Task<string> Teleport(
        [Description("Character name.")] string characterName,
        [Description("Location spec (location name OR 'map x y z').")] string locationSpec)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "characterName cannot be empty").ToJson());
        if (string.IsNullOrWhiteSpace(locationSpec))
            return Task.FromResult(McpResult.Failure(ErrorCodes.InvalidInput, "locationSpec cannot be empty").ToJson());
        return Run($".teleport {locationSpec} {characterName}", $"MCP: player_teleport {characterName} to {locationSpec}");
    }

    [McpServerTool(Name = "player_gps")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Report the selected character's current map, zone, position, and orientation. " +
        "Requires a target to be selected first (set via in-game `.npc select` or " +
        "another tool). RA: `.gps`.")]
    public Task<string> Gps()
        => Run(".gps", "MCP: player_gps");
}
