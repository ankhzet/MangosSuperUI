using Microsoft.AspNetCore.Mvc;
using MangosSuperUI.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MangosSuperUI.Controllers;

public class SettingsController : Controller
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly ILogger<SettingsController> _logger;
    private readonly ComfyUIDispatcher? _comfyDispatcher;

    private string ConfigFilePath => Path.Combine(_env.ContentRootPath, "server-config.json");

    public SettingsController(
        IWebHostEnvironment env,
        IConfiguration config,
        AuditService audit,
        ILogger<SettingsController> logger,
        ComfyUIDispatcher? comfyDispatcher = null)
    {
        _env = env;
        _config = config;
        _audit = audit;
        _logger = logger;
        _comfyDispatcher = comfyDispatcher;
    }

    public IActionResult Index()
    {
        return View();
    }

    /// <summary>
    /// Returns the current running configuration merged from all sources.
    /// </summary>
    [HttpGet]
    public IActionResult Current()
    {
        var settings = new ServerConfig
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
                VmapsDataPath = _config["Vmangos:VmapsDataPath"] ?? "",
                MangosdStartCommand   = _config["Vmangos:MangosdStartCommand"]   ?? "",
                MangosdStopCommand    = _config["Vmangos:MangosdStopCommand"]    ?? "",
                MangosdRestartCommand = _config["Vmangos:MangosdRestartCommand"] ?? "",
                RealmdStartCommand   = _config["Vmangos:RealmdStartCommand"]   ?? "",
                RealmdStopCommand    = _config["Vmangos:RealmdStopCommand"]    ?? "",
                RealmdRestartCommand = _config["Vmangos:RealmdRestartCommand"] ?? ""
            },
            SpellCreator = BuildSpellCreatorConfig(),
            Wiki = new WikiConfig
            {
                Root = _config["Wiki:Root"] ?? ""
            },
            Kestrel = new KestrelConfig
            {
                Url = _config["Kestrel:Endpoints:Http:Url"] ?? "http://0.0.0.0:5000"
            }
        };

        // Also return whether a server-config.json override file exists
        var overrideExists = System.IO.File.Exists(ConfigFilePath);

        return Json(new { settings, overrideExists, configFilePath = ConfigFilePath });
    }

    /// <summary>
    /// Build SpellCreator config from IConfiguration, reading the Nodes[] array dynamically.
    /// </summary>
    private SpellCreatorConfig BuildSpellCreatorConfig()
    {
        var cfg = new SpellCreatorConfig
        {
            ComfyUI = new ComfyUIConfig
            {
                ClipModel2 = _config["SpellCreator:ComfyUI:ClipModel2"] ?? "",
                Nodes = new List<ComfyUINodeConfig>()
            },
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

        // Read the Nodes[] array from config
        var nodesSection = _config.GetSection("SpellCreator:ComfyUI:Nodes").GetChildren().ToList();
        foreach (var node in nodesSection)
        {
            cfg.ComfyUI.Nodes.Add(new ComfyUINodeConfig
            {
                Name = node["Name"] ?? "",
                BaseUrl = node["BaseUrl"] ?? ""
            });
        }

        return cfg;
    }

    /// <summary>
    /// Returns just the override file contents (server-config.json), or empty if it doesn't exist.
    /// </summary>
    [HttpGet]
    public IActionResult Override()
    {
        if (!System.IO.File.Exists(ConfigFilePath))
            return Json(new { exists = false });

        try
        {
            var json = System.IO.File.ReadAllText(ConfigFilePath);
            var parsed = JsonSerializer.Deserialize<ServerConfig>(json, JsonOpts);
            return Json(new { exists = true, settings = parsed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read server-config.json");
            return Json(new { exists = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Saves settings to server-config.json.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] ServerConfig settings)
    {
        try
        {
            // MERGE into the existing override rather than rewriting it wholesale:
            // server-config.json carries sections this page doesn't manage
            // (BotChat.InferenceProfiles, future additions) — the old full rewrite
            // silently dropped them on every save. JSONC comments in the existing file
            // are lost on the first save (JsonNode can't round-trip comments); the
            // section VALUES are what must survive.
            JsonObject root = new();
            if (System.IO.File.Exists(ConfigFilePath))
            {
                try
                {
                    var docOpts = new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    root = JsonNode.Parse(System.IO.File.ReadAllText(ConfigFilePath), null, docOpts) as JsonObject ?? new JsonObject();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Existing server-config.json unreadable — writing a fresh file.");
                    root = new JsonObject();
                }
            }

            void Set(string name, object? section)
            {
                if (section is null) return;
                // drop a differently-cased existing key so we never end up with both
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
            System.IO.File.WriteAllText(ConfigFilePath, json);
            _logger.LogInformation("Saved server-config.json to {Path}", ConfigFilePath);
            await _audit.LogConfigChangeAsync(json, null);
            return Json(new { success = true, message = "Settings saved. Restart the application to apply changes." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save server-config.json");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes the override file, reverting to appsettings.json defaults.
    /// </summary>
    [HttpPost]
    public IActionResult Reset()
    {
        try
        {
            if (System.IO.File.Exists(ConfigFilePath))
            {
                System.IO.File.Delete(ConfigFilePath);
                _logger.LogInformation("Deleted server-config.json");
            }
            return Json(new { success = true, message = "Override removed. Restart to revert to appsettings.json defaults." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete server-config.json");
            return Json(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the live status of all ComfyUI nodes in the dispatcher pool.
    /// Used by the Settings page to show node health indicators.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ComfyPoolStatus()
    {
        if (_comfyDispatcher == null)
            return Json(Array.Empty<object>());

        var statuses = await _comfyDispatcher.GetPoolStatusAsync();
        return Json(statuses);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // read-side: the live override is JSONC with mixed-case section names —
        // without these, Override() fails to parse the very file Save() writes to
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

// ==================== Config DTOs ====================

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
    // Optional explicit vmaps override for the World Editor's collision (VMaNGOS
    // extracted vmaps). Blank → falls back to ServerDataPath/vmaps. See
    // WorldEditorController.GetVmapsDirectory.
    public string VmapsDataPath { get; set; } = "";

    // Commands run by ProcessManagerService for start/stop/restart on each
    // service. Blank → the service signals the process directly via
    // Process.Kill() (works when the UI shares the mangosd container's
    // PID namespace, i.e. the docker-compose default).
    //
    // Placeholders:
    //   {unit}    → MangosdProcess / RealmdProcess
    //   {action}  → start | stop | restart
    public string MangosdStartCommand   { get; set; } = "";
    public string MangosdStopCommand    { get; set; } = "";
    public string MangosdRestartCommand { get; set; } = "";
    public string RealmdStartCommand   { get; set; } = "";
    public string RealmdStopCommand    { get; set; } = "";
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
    // Root of the generated documentation corpus the /Wiki page renders and indexes.
    // (Wiki:IndexConnection remains a config-file-only advanced override; it defaults
    // to the Admin connection string, which is already editable above.)
    public string Root { get; set; } = "";
}

public class KestrelConfig
{
    public string Url { get; set; } = "http://0.0.0.0:5000";
}