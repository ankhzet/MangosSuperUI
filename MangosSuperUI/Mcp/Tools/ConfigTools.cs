using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Common;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MangosSuperUI.Mcp.Tools;

/// <summary>
/// Configuration reads + writes for mangosd.conf and server-config.json.
/// `config_load_mangosd` is read-only; everything else requires `write_db`.
///
/// `config_reload_mangosd` requires `ra` because it sends `.reload config`.
/// </summary>
[McpServerToolType]
public class ConfigTools
{
    private readonly RaService _ra;
    private readonly AuditService _audit;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly IOptions<VmangosSettings> _vmangos;
    private readonly McpCallContext _ctx;
    private readonly ILogger<ConfigTools> _log;
    private readonly ComfyUIDispatcher? _comfy;

    private string ServerConfigFilePath => Path.Combine(_env.ContentRootPath, "server-config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ConfigTools(
        RaService ra, AuditService audit,
        IConfiguration config, IWebHostEnvironment env,
        IOptions<VmangosSettings> vmangos,
        McpCallContext ctx, ILogger<ConfigTools> log,
        ComfyUIDispatcher? comfy = null)
    {
        _ra = ra;
        _audit = audit;
        _config = config;
        _env = env;
        _vmangos = vmangos;
        _ctx = ctx;
        _log = log;
        _comfy = comfy;
    }

    [McpServerTool(Name = "config_load_mangosd")]
    [Description(
        "Read and parse the entire mangosd.conf into structured settings: line, " +
        "key, value (unquoted), rawValue, isQuoted, section, description. " +
        "Returns section summary + total counts.")]
    public string LoadMangosd()
    {
        try
        {
            var path = GetConfPath();
            if (!File.Exists(path))
                return McpResult.Failure(ErrorCodes.NotFound, $"Config file not found: {path}").ToJson();

            var lines = File.ReadAllLines(path);
            var settings = new List<object>();
            var currentComment = new List<string>();
            var currentSection = "General";

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("#") && !trimmed.StartsWith("#    ") && !trimmed.StartsWith("# "))
                {
                    var sectionMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^#+\s*(.+?)\s*#*$");
                    if (sectionMatch.Success)
                    {
                        var candidate = sectionMatch.Groups[1].Value.Trim();
                        if (candidate.Length > 3 && candidate == candidate.ToUpper() && !candidate.Contains("="))
                        {
                            currentSection = TitleCase(candidate);
                            currentComment.Clear();
                            continue;
                        }
                    }
                }
                if (trimmed.StartsWith("#"))
                {
                    var commentText = trimmed.TrimStart('#').TrimStart();
                    if (!string.IsNullOrWhiteSpace(commentText) && !commentText.All(c => c == '#' || c == '-' || c == '='))
                        currentComment.Add(commentText);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = trimmed[..eqIdx].Trim();
                var value = trimmed[(eqIdx + 1)..].Trim();
                var rawValue = value;
                var isQuoted = value.StartsWith('"') && value.EndsWith('"');
                if (isQuoted) value = value[1..^1];

                settings.Add(new
                {
                    line = i + 1, key, value, rawValue, isQuoted,
                    section = currentSection,
                    description = currentComment.Count > 0 ? string.Join(" ", currentComment) : null
                });
                currentComment.Clear();
            }

            return McpResult.Success(new
            {
                success = true,
                path,
                totalLines = lines.Length,
                totalSettings = settings.Count,
                sections = settings.GroupBy(s => ((dynamic)s).section)
                    .Select(g => new { name = g.Key, count = g.Count() }).ToList(),
                settings
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "config_load_mangosd failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "config_save_mangosd")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Update one or more settings in mangosd.conf. Creates a `.bak.<ts>` " +
        "backup before writing. `changes` is a list of { key, value, forceQuote }. " +
        "Keys not found in the file are reported in `notFound` but don't fail the call.")]
    public async Task<string> SaveMangosd(
        [Description("List of changes: [{ key, value, forceQuote? }].")] List<ConfigChangeRequest> changes)
    {
        if (changes is null || changes.Count == 0)
            return McpResult.Failure(ErrorCodes.InvalidInput, "no changes provided").ToJson();
        try
        {
            var path = GetConfPath();
            if (!File.Exists(path))
                return McpResult.Failure(ErrorCodes.NotFound, $"Config file not found: {path}").ToJson();

            var backupPath = path + ".bak." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Copy(path, backupPath, overwrite: true);
            var lines = File.ReadAllLines(path);
            var applied = new Dictionary<string, ConfigChange>();

            foreach (var change in changes)
            {
                bool found = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("#") || string.IsNullOrWhiteSpace(trimmed)) continue;
                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx <= 0) continue;
                    var key = trimmed[..eqIdx].Trim();
                    if (!key.Equals(change.Key, StringComparison.OrdinalIgnoreCase)) continue;

                    var oldValue = trimmed[(eqIdx + 1)..].Trim();
                    var isQuoted = oldValue.StartsWith('"') && oldValue.EndsWith('"');
                    var cleanOld = isQuoted ? oldValue[1..^1] : oldValue;

                    string newRawValue = (isQuoted || change.ForceQuote)
                        ? $"\"{change.Value}\""
                        : change.Value;

                    // Preserve original indentation
                    var originalLine = lines[i];
                    var keyEnd = originalLine.IndexOf(key) + key.Length;
                    var afterKey = originalLine[keyEnd..];
                    var eqOffset = afterKey.IndexOf('=');
                    var afterEq = eqOffset >= 0 ? afterKey[(eqOffset + 1)..] : " ";
                    var prefixLen = (keyEnd + eqOffset + 1);
                    var prefix = originalLine[..prefixLen];
                    lines[i] = prefix + newRawValue + afterEq[change.Value.Length..];

                    applied[change.Key] = new ConfigChange
                    {
                        Key = change.Key, OldValue = cleanOld,
                        NewValue = change.Value, Line = i + 1
                    };
                    found = true;
                    break;
                }
                if (!found) _log.LogWarning("Config key not found: {Key}", change.Key);
            }
            await File.WriteAllLinesAsync(path, lines);

            var changesJson = JsonSerializer.Serialize(
                applied.ToDictionary(kvp => kvp.Key, kvp => new { from = kvp.Value.OldValue, to = kvp.Value.NewValue }));

            await _audit.LogAsync(new AuditEntry
            {
                Operator = _ctx.Operator,
                OperatorIp = _ctx.RemoteIp,
                Category = "config",
                Action = "mangosd_conf_update",
                TargetType = "config",
                TargetName = "mangosd.conf",
                StateAfter = changesJson,
                IsReversible = true,
                Success = true,
                Notes = $"Updated {applied.Count} setting(s). Backup: {Path.GetFileName(backupPath)}"
            });

            return McpResult.Success(new
            {
                success = true,
                appliedCount = applied.Count,
                notFound = changes.Count - applied.Count,
                backupFile = Path.GetFileName(backupPath),
                changes = applied.Values.Select(c => new { c.Key, c.OldValue, c.NewValue, c.Line })
            }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "config_save_mangosd failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "config_reload_mangosd")]
    [McpCapability(McpCapability.Ra)]
    [Description(
        "Send `.reload config` over RemoteAdmin. The server re-reads mangosd.conf " +
        "without a restart. Use after config_save_mangosd.")]
    public async Task<string> ReloadMangosd()
    {
        try
        {
            var (response, success) = await _audit.ExecuteAndLogAsync(
                _ra, ".reload config",
                operator_: _ctx.Operator,
                operatorIp: _ctx.RemoteIp,
                notes: "MCP tool: config_reload_mangosd");
            return success
                ? McpResult.Success(new { response }).ToJson()
                : McpResult.Failure(ErrorCodes.RaDisconnected, response, retryable: true).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "config_reload_mangosd failed");
            return McpResult.FromException(ex, ErrorCodes.RaDisconnected).ToJson();
        }
    }

    [McpServerTool(Name = "settings_current")]
    [Description(
        "Current merged configuration (appsettings.json + server-config.json overlay). " +
        "Returns every section: ConnectionStrings, RemoteAccess, Vmangos, " +
        "SpellCreator, Wiki, Kestrel. Also reports `overrideExists` and the " +
        "config file path.")]
    public string SettingsCurrent() => McpResult.Success(new
    {
        settings = BuildCurrentConfig(),
        overrideExists = File.Exists(ServerConfigFilePath),
        configFilePath = ServerConfigFilePath
    }).ToJson();

    [McpServerTool(Name = "settings_override")]
    [Description(
        "Returns just the contents of server-config.json (the override file). " +
        "Returns {exists:false} when the file doesn't exist and the defaults " +
        "in appsettings.json are in effect.")]
    public string SettingsOverride()
    {
        if (!File.Exists(ServerConfigFilePath))
            return McpResult.Success(new { exists = false }).ToJson();
        try
        {
            var json = File.ReadAllText(ServerConfigFilePath);
            var parsed = JsonSerializer.Deserialize<ServerConfig>(json, JsonOpts);
            return McpResult.Success(new { exists = true, settings = parsed }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings_override failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "settings_save")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Save server-config.json. MERGES into the existing file (sections not " +
        "in the supplied settings are preserved). Requires an app restart to " +
        "apply.")]
    public async Task<string> SettingsSave([Description("Full ServerConfig object.")] ServerConfig settings)
    {
        try
        {
            JsonObject root = new();
            if (File.Exists(ServerConfigFilePath))
            {
                try
                {
                    var docOpts = new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    root = JsonNode.Parse(File.ReadAllText(ServerConfigFilePath), null, docOpts) as JsonObject ?? new JsonObject();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Existing server-config.json unreadable — writing a fresh file.");
                    root = new JsonObject();
                }
            }

            void Set(string name, object? section)
            {
                if (section is null) return;
                var existing = root.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Key;
                if (existing is not null) root.Remove(existing);
                root[name] = JsonSerializer.SerializeToNode(section, JsonOpts);
            }

            Set("connectionStrings", settings.ConnectionStrings);
            Set("remoteAccess", settings.RemoteAccess);
            Set("vmangos", settings.Vmangos);
            Set("spellCreator", settings.SpellCreator);
            Set("wiki", settings.Wiki);
            Set("kestrel", settings.Kestrel);

            var json = root.ToJsonString(JsonOpts);
            File.WriteAllText(ServerConfigFilePath, json);
            await _audit.LogConfigChangeAsync(json, null);

            return McpResult.Success(new { success = true, message = "Settings saved. Restart the application to apply changes." }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings_save failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "settings_reset")]
    [McpCapability(McpCapability.WriteDb)]
    [Description(
        "Delete server-config.json. The app reverts to appsettings.json defaults " +
        "on next restart.")]
    public string SettingsReset()
    {
        try
        {
            if (File.Exists(ServerConfigFilePath))
                File.Delete(ServerConfigFilePath);
            return McpResult.Success(new { success = true, message = "Override removed. Restart to revert to appsettings.json defaults." }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings_reset failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    [McpServerTool(Name = "settings_comfy_pool_status")]
    [Description(
        "Per-node ComfyUI dispatcher pool health: online, busy, running, " +
        "pending, submitted, completed, error counts. Empty array if no " +
        "nodes are configured.")]
    public async Task<string> SettingsComfyPoolStatus()
    {
        if (_comfy is null)
            return McpResult.Success(new { nodes = Array.Empty<object>(), notice = "no ComfyUI dispatcher registered" }).ToJson();
        try
        {
            var statuses = await _comfy.GetPoolStatusAsync();
            return McpResult.Success(new { nodes = statuses }).ToJson();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "settings_comfy_pool_status failed");
            return McpResult.FromException(ex, ErrorCodes.Internal).ToJson();
        }
    }

    // ----- helpers -----

    private string GetConfPath()
    {
        var path = _vmangos.Value.MangosdConfPath;
        if (string.IsNullOrWhiteSpace(path))
            path = "/opt/superui-core/etc/mangosd.conf";
        return path;
    }

    private static string TitleCase(string upper)
    {
        if (string.IsNullOrEmpty(upper)) return upper;
        var words = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w =>
            w.Length <= 3 ? w : char.ToUpper(w[0]) + w[1..].ToLower()));
    }

    private ServerConfig BuildCurrentConfig() => new()
    {
        ConnectionStrings = new ConnectionStringsConfig
        {
            Mangos = _config.GetConnectionString("Mangos") ?? "",
            Characters = _config.GetConnectionString("Characters") ?? "",
            Realmd = _config.GetConnectionString("Realmd") ?? "",
            Logs = _config.GetConnectionString("Logs") ?? "",
            Admin = _config.GetConnectionString("Admin") ?? ""
        },
        RemoteAccess = new RemoteAccessConfig
        {
            Host = _config["RemoteAccess:Host"] ?? "127.0.0.1",
            Port = int.TryParse(_config["RemoteAccess:Port"], out var p) ? p : 3443,
            Username = _config["RemoteAccess:Username"] ?? "",
            Password = _config["RemoteAccess:Password"] ?? "",
            ReconnectDelayMs = int.TryParse(_config["RemoteAccess:ReconnectDelayMs"], out var rd) ? rd : 3000,
            CommandTimeoutMs = int.TryParse(_config["RemoteAccess:CommandTimeoutMs"], out var ct) ? ct : 5000
        },
        Vmangos = new VmangosConfig
        {
            BinDirectory = _config["Vmangos:BinDirectory"] ?? "",
            LogDirectory = _config["Vmangos:LogDirectory"] ?? "",
            ConfigDirectory = _config["Vmangos:ConfigDirectory"] ?? "",
            MangosdProcess = _config["Vmangos:MangosdProcess"] ?? "mangosd",
            RealmdProcess = _config["Vmangos:RealmdProcess"] ?? "realmd",
            MangosdConfPath = _config["Vmangos:MangosdConfPath"] ?? "",
            LogsDir = _config["Vmangos:LogsDir"] ?? "",
            DbcPath = _config["Vmangos:DbcPath"] ?? "",
            MapsDataPath = _config["Vmangos:MapsDataPath"] ?? "",
            BackupDirectory = _config["Vmangos:BackupDirectory"] ?? "",
            VmangosSourcePath = _config["Vmangos:VmangosSourcePath"] ?? "",
            VmangosSqlPath = _config["Vmangos:VmangosSqlPath"] ?? "",
            ExtractorsPath = _config["Vmangos:ExtractorsPath"] ?? "",
            ServerDataPath = _config["Vmangos:ServerDataPath"] ?? "",
            ClientDataPath = _config["Vmangos:ClientDataPath"] ?? "",
            MangosdStartCommand   = _config["Vmangos:MangosdStartCommand"]   ?? "",
            MangosdStopCommand    = _config["Vmangos:MangosdStopCommand"]    ?? "",
            MangosdRestartCommand = _config["Vmangos:MangosdRestartCommand"] ?? "",
            RealmdStartCommand   = _config["Vmangos:RealmdStartCommand"]   ?? "",
            RealmdStopCommand    = _config["Vmangos:RealmdStopCommand"]    ?? "",
            RealmdRestartCommand = _config["Vmangos:RealmdRestartCommand"] ?? ""
        },
        SpellCreator = BuildSpellCreatorConfig(),
        Wiki = new WikiConfig { Root = _config["Wiki:Root"] ?? "" },
        Kestrel = new KestrelConfig
        {
            Url = _config["Kestrel:Endpoints:Http:Url"] ?? "http://0.0.0.0:5000"
        }
    };

    private SpellCreatorConfig BuildSpellCreatorConfig()
    {
        var cfg = new SpellCreatorConfig
        {
            ComfyUI = new ComfyUIConfig { ClipModel2 = _config["SpellCreator:ComfyUI:ClipModel2"] ?? "", Nodes = new() },
            Ollama = new OllamaConfig
            {
                BaseUrl = _config["SpellCreator:Ollama:BaseUrl"] ?? "",
                Model = _config["SpellCreator:Ollama:Model"] ?? "",
                VisionModel = _config["SpellCreator:Ollama:VisionModel"] ?? ""
            },
            RawBlpPath = _config["SpellCreator:RawBlpPath"] ?? "",
            DataPath = _config["SpellCreator:DataPath"] ?? "",
            ClientM2Path = _config["SpellCreator:ClientM2Path"] ?? "",
            ClientDataPath = _config["SpellCreator:ClientDataPath"] ?? "",
            PatchOutputPath = _config["SpellCreator:PatchOutputPath"] ?? ""
        };
        foreach (var node in _config.GetSection("SpellCreator:ComfyUI:Nodes").GetChildren())
        {
            cfg.ComfyUI.Nodes.Add(new ComfyUINodeConfig { Name = node["Name"] ?? "", BaseUrl = node["BaseUrl"] ?? "" });
        }
        return cfg;
    }
}

// ----- DTOs (mirror the controllers' Models but defined locally so MCP doesn't depend on controller internals) -----

public class ConfigChangeRequest
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool ForceQuote { get; set; }
}

public class ConfigChange
{
    public string Key { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public int Line { get; set; }
}

public class ServerConfig
{
    public ConnectionStringsConfig? ConnectionStrings { get; set; }
    public RemoteAccessConfig? RemoteAccess { get; set; }
    public VmangosConfig? Vmangos { get; set; }
    public SpellCreatorConfig? SpellCreator { get; set; }
    public WikiConfig? Wiki { get; set; }
    public KestrelConfig? Kestrel { get; set; }
}

public class ConnectionStringsConfig
{
    public string Mangos { get; set; } = "";
    public string Characters { get; set; } = "";
    public string Realmd { get; set; } = "";
    public string Logs { get; set; } = "";
    public string Admin { get; set; } = "";
}

public class RemoteAccessConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3443;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int ReconnectDelayMs { get; set; } = 3000;
    public int CommandTimeoutMs { get; set; } = 5000;
}

public class VmangosConfig
{
    public string BinDirectory { get; set; } = "";
    public string LogDirectory { get; set; } = "";
    public string ConfigDirectory { get; set; } = "";
    public string MangosdProcess { get; set; } = "mangosd";
    public string RealmdProcess { get; set; } = "realmd";
    public string MangosdConfPath { get; set; } = "";
    public string LogsDir { get; set; } = "";
    public string DbcPath { get; set; } = "";
    public string MapsDataPath { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public string VmangosSourcePath { get; set; } = "";
    public string VmangosSqlPath { get; set; } = "";
    public string ExtractorsPath { get; set; } = "";
    public string ServerDataPath { get; set; } = "";
    public string ClientDataPath { get; set; } = "";
    public string MangosdStartCommand { get; set; } = "";
    public string MangosdStopCommand { get; set; } = "";
    public string MangosdRestartCommand { get; set; } = "";
    public string RealmdStartCommand { get; set; } = "";
    public string RealmdStopCommand { get; set; } = "";
    public string RealmdRestartCommand { get; set; } = "";
}

public class SpellCreatorConfig
{
    public ComfyUIConfig ComfyUI { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public string RawBlpPath { get; set; } = "";
    public string DataPath { get; set; } = "";
    public string ClientM2Path { get; set; } = "";
    public string ClientDataPath { get; set; } = "";
    public string PatchOutputPath { get; set; } = "";
}

public class ComfyUIConfig
{
    public List<ComfyUINodeConfig> Nodes { get; set; } = new();
    public string ClipModel2 { get; set; } = "";
}

public class ComfyUINodeConfig
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public class OllamaConfig
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string VisionModel { get; set; } = "";
}

public class WikiConfig
{
    public string Root { get; set; } = "";
}

public class KestrelConfig
{
    public string Url { get; set; } = "http://0.0.0.0:5000";
}
