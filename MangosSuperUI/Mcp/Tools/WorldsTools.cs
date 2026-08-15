using System.ComponentModel;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// World lifecycle: suspend, resume, fork, snapshot, delete, preflight.
/// Reads default to <c>read</c>; suspend/resume/fork/delete require
/// <c>worlds</c>. These are the most dangerous tools in the surface —
/// every call may stop mangosd and rewrite world data.
/// </summary>
[McpServerToolType]
public class WorldsTools
{
    private readonly WorldStateService _worlds;
    private readonly AuditService _audit;
    private readonly McpCallContext _ctx;
    private readonly ILogger<WorldsTools> _log;

    public WorldsTools(WorldStateService worlds, AuditService audit,
        McpCallContext ctx, ILogger<WorldsTools> log)
    {
        _worlds = worlds;
        _audit = audit;
        _ctx = ctx;
        _log = log;
    }

    [McpServerTool(Name = "worlds_status")]
    [Description(
        "Full world status: live world, shelf worlds, process flags, current " +
        "in-flight job, and DB row counts. Read this first.")]
    public async Task<string> Status()
    {
        try
        {
            var status = await _worlds.GetStatusAsync();
            var stats = await _worlds.GatherStatsAsync();
            return McpResult.Success(new { status, stats }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_status failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_job")]
    [Description("Current in-flight suspend/resume job (single-flight), or null when idle.")]
    public string Job()
        => McpResult.Success(new { job = _worlds.CurrentJob }).ToJson();

    [McpServerTool(Name = "worlds_list")]
    [Description("All worlds in the registry (live + suspended + archived).")]
    public async Task<string> List()
    {
        try
        {
            var reg = await _worlds.GetRegistryAsync();
            return McpResult.Success(new { registry = reg }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_list failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_preflight")]
    [Description(
        "Validate a snapshot before resume. Returns artifact checks, " +
        "config sanity, RTS eligibility, and name-pool capacity.")]
    public async Task<string> Preflight(
        [Description("World id to validate.")] string worldId,
        [Description("Snapshot folder name (optional, defaults to latest).")] string? snapshot = null,
        [Description("Force full restore instead of incremental.")] bool forceFullRestore = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worldId))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId required").ToJson();
        try
        {
            var r = await _worlds.PreflightResumeAsync(worldId, snapshot, forceFullRestore, ct);
            return McpResult.Success(r).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_preflight failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_create_options")]
    [Description(
        "Profile metadata + eligible snapshots for creating a new RTS world. " +
        "Use this before worlds_create_rts to see what's available.")]
    public async Task<string> CreateOptions(CancellationToken ct = default)
    {
        try
        {
            var registry = await _worlds.GetRegistryAsync();
            var namePool = await _worlds.GetBotNamePoolStatsAsync(ct);
            // Walk every world × snapshot, flag missing required artifacts.
            var sourceOptions = new List<object>();
            foreach (var world in registry.Worlds)
            {
                foreach (var snap in world.Snapshots)
                {
                    var captured = snap.Artifacts.Select(a => a.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = WorldArtifactService.V2Artifacts
                        .Where(x => x.Required && !captured.Contains(x.File))
                        .Select(x => x.File).ToArray();
                    sourceOptions.Add(new
                    {
                        worldId = world.Id,
                        worldName = world.Name,
                        folder = snap.Folder,
                        schemaVersion = snap.SchemaVersion,
                        artifactCount = snap.Artifacts.Count,
                        missingRequired = missing,
                        eligible = missing.Length == 0 && snap.SchemaVersion >= 2
                    });
                }
            }
            return McpResult.Success(new
            {
                sources = sourceOptions,
                botNamePool = namePool
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_create_options failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_suspend")]
    [McpCapability(McpCapability.Worlds)]
    [Description(
        "Freeze the live world and write a snapshot. Stops mangosd for " +
        "the duration. Long-running; returns a WorldJob. Use worlds_job to poll.")]
    public async Task<string> Suspend(
        [Description("Optional label for the snapshot.")] string? label = null)
    {
        try
        {
            var job = await _worlds.SuspendAsync(label, _ctx.RemoteIp);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_suspend",
                TargetType = "world",
                TargetName = label ?? "(unlabelled)",
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new { jobId = job.Id }),
                IsReversible = true,
                Success = true,
                Notes = "Suspended live world via MCP"
            });
            return McpResult.Success(new { job }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_suspend failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_resume")]
    [McpCapability(McpCapability.Worlds)]
    [Description(
        "Mount a world. Auto-suspends the current live world first if one " +
        "is running. Long-running; returns a WorldJob. Use worlds_job to poll.")]
    public async Task<string> Resume(
        [Description("World id to mount.")] string worldId,
        [Description("Snapshot folder (defaults to latest).")] string? snapshotFolder = null,
        [Description("Force full restore instead of incremental.")] bool forceFullRestore = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(worldId))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId required").ToJson();
        try
        {
            var job = await _worlds.ResumeAsync(worldId, snapshotFolder, _ctx.RemoteIp, forceFullRestore);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_resume",
                TargetType = "world",
                TargetName = worldId,
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new { jobId = job.Id, snapshotFolder, forceFullRestore }),
                IsReversible = false,
                Success = true,
                Notes = $"Resumed world {worldId} via MCP"
            });
            return McpResult.Success(new { job }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_resume failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_restore_group")]
    [McpCapability(McpCapability.Worlds)]
    [Description(
        "Restore a single group ('world', 'players', or 'core') from one " +
        "snapshot, without touching the live world. Refuses if a live " +
        "world is mounted (call worlds_suspend first).")]
    public async Task<string> RestoreGroup(
        [Description("World id.")] string worldId,
        [Description("Snapshot folder name.")] string folder,
        [Description("Group: 'world', 'players', or 'core'.")] string group)
    {
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(group))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId, folder and group required").ToJson();
        if (!WorldStateService.AllGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
            return McpResult.Failure(ErrorCodes.InvalidInput,
                $"group must be one of: {string.Join(", ", WorldStateService.AllGroups)}").ToJson();
        try
        {
            var job = await _worlds.RestoreSingleGroupAsync(worldId, folder, group.ToLowerInvariant());
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_restore_group",
                TargetType = "world",
                TargetName = $"{worldId}/{folder}",
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new { group, jobId = job.Id }),
                IsReversible = true,
                Success = true,
                Notes = $"Restored group '{group}' from {worldId}/{folder}"
            });
            return McpResult.Success(new { job }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_restore_group failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_create_rts")]
    [McpCapability(McpCapability.Worlds)]
    [Description("Create a parked zero-roster RTS world from a v2 source snapshot. Long-running.")]
    public async Task<string> CreateRts(
        [Description("CreateRtsWorldRequestModel body — see WorldsController.")] CreateRtsWorldRequestModel request)
    {
        try
        {
            var job = await _worlds.CreateRtsWorldAsync(request);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_create_rts",
                TargetType = "world",
                TargetName = request.Name,
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new { jobId = job.Id }),
                IsReversible = true,
                Success = true,
                Notes = $"Created RTS world {request.Name}"
            });
            return McpResult.Success(new { job }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_create_rts failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_fork")]
    [McpCapability(McpCapability.Worlds)]
    [Description(
        "Branch a new world off an existing snapshot. Reference-counted: " +
        "snapshots are shared until one world drops its reference.")]
    public async Task<string> Fork(
        [Description("Source world id to fork.")] string worldId,
        [Description("Snapshot folder (defaults to latest).")] string? snapshotFolder,
        [Description("New world name.")] string name,
        [Description("Flavor tag (e.g. 'casual', 'hardcore').")] string flavor,
        [Description("Optional notes.")] string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(flavor))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId, name, flavor required").ToJson();
        try
        {
            var rec = await _worlds.ForkAsync(worldId, snapshotFolder, name, flavor, notes);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_fork",
                TargetType = "world",
                TargetName = name,
                StateAfter = System.Text.Json.JsonSerializer.Serialize(rec),
                IsReversible = true,
                Success = true,
                Notes = $"Forked world '{name}' from {worldId}"
            });
            return McpResult.Success(rec).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_fork failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_update")]
    [McpCapability(McpCapability.Worlds)]
    [Description("Rename / re-flavour / re-note a world.")]
    public async Task<string> Update(
        [Description("World id.")] string worldId,
        [Description("New display name.")] string? name = null,
        [Description("New flavor tag.")] string? flavor = null,
        [Description("New notes.")] string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(worldId))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId required").ToJson();
        try
        {
            var rec = await _worlds.UpdateAsync(worldId, name, flavor, notes);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_update",
                TargetType = "world",
                TargetName = worldId,
                StateAfter = System.Text.Json.JsonSerializer.Serialize(rec),
                IsReversible = true,
                Success = true,
                Notes = $"Updated world {worldId}"
            });
            return McpResult.Success(rec).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_update failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_snapshot_label")]
    [McpCapability(McpCapability.Worlds)]
    [Description("Retitle a snapshot folder (cosmetic only).")]
    public async Task<string> SnapshotLabel(
        [Description("World id.")] string worldId,
        [Description("Snapshot folder name.")] string folder,
        [Description("New label.")] string label)
    {
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(label))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId, folder, label required").ToJson();
        try
        {
            var ok = await _worlds.UpdateSnapshotLabelAsync(worldId, folder, label);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_snapshot_label",
                TargetType = "snapshot",
                TargetName = $"{worldId}/{folder}",
                StateAfter = System.Text.Json.JsonSerializer.Serialize(new { label }),
                IsReversible = true,
                Success = ok,
                Notes = $"Relabelled snapshot {worldId}/{folder}"
            });
            return McpResult.Success(new { success = ok }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_snapshot_label failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_delete_world")]
    [McpCapability(McpCapability.Worlds)]
    [Description(
        "Delete a world. Ref-counts snapshot folders — ones still " +
        "referenced by other worlds are kept.")]
    public async Task<string> DeleteWorld(
        [Description("World id.")] string worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId required").ToJson();
        try
        {
            var result = await _worlds.DeleteWorldAsync(worldId);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_delete_world",
                TargetType = "world",
                TargetName = worldId,
                StateAfter = System.Text.Json.JsonSerializer.Serialize(result),
                IsReversible = false,
                Success = true,
                Notes = $"Deleted world {worldId}"
            });
            return McpResult.Success(result).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_delete_world failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "worlds_delete_snapshot")]
    [McpCapability(McpCapability.Worlds)]
    [Description(
        "Delete one snapshot. Refuses if it's the only path back to a world " +
        "still in use.")]
    public async Task<string> DeleteSnapshot(
        [Description("World id.")] string worldId,
        [Description("Snapshot folder name.")] string folder)
    {
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(folder))
            return McpResult.Failure(ErrorCodes.InvalidInput, "worldId and folder required").ToJson();
        try
        {
            var ok = await _worlds.DeleteSnapshotAsync(worldId, folder);
            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "worlds",
                Action = "worlds_delete_snapshot",
                TargetType = "snapshot",
                TargetName = $"{worldId}/{folder}",
                IsReversible = false,
                Success = ok,
                Notes = $"Deleted snapshot {worldId}/{folder}"
            });
            return McpResult.Success(new { success = ok }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "worlds_delete_snapshot failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }
}
