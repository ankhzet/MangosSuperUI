using MangosSuperUI.BotLogic.Brain;
using MangosSuperUI.BotLogic.Chat.Capacity;
using MangosSuperUI.BotLogic.Chat.Coordinator;
using MangosSuperUI.BotLogic.Chat.Core;
using MangosSuperUI.BotLogic.Chat.Engine;
using MangosSuperUI.BotLogic.Chat.Health;
using MangosSuperUI.BotLogic.Chat.Memory;
using MangosSuperUI.BotLogic.Chat.Voice;
using MangosSuperUI.BotLogic.Core;
using MangosSuperUI.BotLogic.Data;
using MangosSuperUI.BotLogic.Planners;
using MangosSuperUI.BotLogic.Tracking;
using MangosSuperUI.Hubs;
using MangosSuperUI.Mcp.Auth;
using MangosSuperUI.Mcp.Options;
using MangosSuperUI.Mcp.Prompts;
using MangosSuperUI.Mcp.Resources;
using MangosSuperUI.Mcp.Tools;
using MangosSuperUI.Models;
using MangosSuperUI.Services;
using Microsoft.AspNetCore.StaticFiles;
using ModelContextProtocol.AspNetCore;
using System.Diagnostics.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSystemd();  // ← sends watchdog heartbeats + handles SIGTERM gracefully

// ---------- Brain log buffer (in-memory ring, fed to the Bots "Live" tab on demand) ----------
var botLogBuffer = new BotLogBuffer();
builder.Services.AddSingleton(botLogBuffer);
builder.Logging.AddProvider(new BotLogBufferProvider(botLogBuffer));
builder.Logging.AddFilter<BotLogBufferProvider>("MangosSuperUI", LogLevel.Debug);

// ---------- Additional Config Source ----------
builder.Configuration.AddJsonFile("server-config.json", optional: true, reloadOnChange: true);

// ---------- Configuration ----------
builder.Services.Configure<VmangosSettings>(builder.Configuration.GetSection("Vmangos"));
builder.Services.Configure<RemoteAccessSettings>(builder.Configuration.GetSection("RemoteAccess"));
builder.Services.Configure<BotChatSettings>(builder.Configuration.GetSection("BotChat"));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));

// ---------- Data ----------
builder.Services.AddSingleton<ConnectionFactory>();

// ---------- Services ----------
builder.Services.AddSingleton<DbInitializationService>();
builder.Services.AddSingleton<RaService>();
builder.Services.AddSingleton<ProcessManagerService>();
builder.Services.AddSingleton<StateCaptureService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<WorldArtifactService>();
builder.Services.AddSingleton<RtsWorldCreationService>();
builder.Services.AddSingleton<WorldStateService>();   // world suspend/resume — registry lives on disk, not in a swappable DB
builder.Services.AddSingleton<ChangeGraphService>();  // audit_log as a drillable graph, with entry/batch undo
builder.Services.AddSingleton<DivergenceService>();   // live drift vs og_* baselines — the state view behind the graph
builder.Services.AddSingleton<DbcService>();
builder.Services.AddSingleton<HeightMapService>();
builder.Services.AddSingleton<BotBridgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotBridgeService>());
builder.Services.AddSingleton<OllamaChatService>();
builder.Services.AddSingleton<SourceIndexerService>();
builder.Services.AddSingleton<ZoneSafetyMap>();
builder.Services.AddSingleton<BotFleetDiagnostics>();
builder.Services.AddSingleton<BotFlightRecorder>();
builder.Services.AddSingleton<BotFallRecorder>();   // always-on void/fall black box (flush-only-on-fall)
builder.Services.AddSingleton<SpellCreatorService>();
builder.Services.AddSingleton<BlpWriterService>();
builder.Services.AddSingleton<PatchBuilderService>();
builder.Services.AddSingleton<SpellIconService>();
builder.Services.AddSingleton<SpellConfigService>();
builder.Services.AddSingleton<SpellTextureService>();
builder.Services.AddSingleton<SpellRecipeService>();
builder.Services.AddSingleton<ComfyUIDispatcher>();
builder.Services.AddSingleton<ComfyUIUpscaler>();
builder.Services.AddSingleton<VanillaBlpService>();
builder.Services.AddSingleton<SpellDnaService>();
builder.Services.AddSingleton<MpqReaderService>();
builder.Services.AddSingleton<GameObjectModelService>();
builder.Services.AddSingleton<MinimapTileService>();
builder.Services.AddSingleton<CharacterModelService>();
builder.Services.AddSingleton<BodyAtlasTextureService>();
builder.Services.AddSingleton<CacheVersionRegistry>();
builder.Services.AddSingleton<CharacterSkinCompositor>();
builder.Services.AddSingleton<PaletteSwapService>();
builder.Services.AddScoped<VariationRecipeService>();
builder.Services.AddScoped<TextureSegmentationService>();
builder.Services.AddSingleton<VramManager>();
builder.Services.AddSingleton<WikiDocStore>();
builder.Services.AddSingleton<WikiIndexer>();
builder.Services.AddSingleton<WikiSearchStore>();
builder.Services.AddScoped<RetextureSupport>();


builder.Services.AddScoped<ItemTextureService>();
builder.Services.AddScoped<ItemRetextureService>();

// ---------- BotLogic: Behavioral Engine ----------

// Tracking (in-memory, singleton)
builder.Services.AddSingleton<BotStateTracker>();

// Data loaders
builder.Services.AddSingleton<QuirkLoader>();
builder.Services.AddSingleton<SpellProgressionLoader>();
builder.Services.AddSingleton<ZoneDataLoader>();
builder.Services.AddSingleton<CreatureSpawnLoader>();   // Scatter Build 2: per-entry spawn footprint sampler (QuestPlanner)
builder.Services.AddSingleton<QuestGraphLoader>();
builder.Services.AddSingleton<BotBrainDbInit>();

// Brain spine (Strategy B rebuild): executor + supervisor + driver
builder.Services.AddSingleton<BotExecutor>();
builder.Services.AddSingleton<BotSupervisor>();

// Brain planners (Phase 2 — Grinding): goal selector + per-goal planners (IBotPlanner).
// BotBrain self-assembles the Goal→planner map from the registered IBotPlanner set;
// adding a goal in P3+ is one more AddSingleton<IBotPlanner, …>() here.
builder.Services.AddSingleton<GoalSelector>();
builder.Services.AddSingleton<IBotPlanner, GrindPlanner>();
builder.Services.AddSingleton<IBotPlanner, QuestPlanner>();   // P3: Goal.Questing
builder.Services.AddSingleton<IBotPlanner, MaintenancePlanner>();
builder.Services.AddSingleton<IBotPlanner, TrainingPlanner>();   // Goal.Training — class-trainer trip
builder.Services.AddSingleton<IBotPlanner, HubErrandPlanner>();  // Goal.Vendoring — "do your rounds" hub errand (player-party, 2026-07-08 §3)

// [ROTATION] Custom combat rotations — profile loading, assignment persistence, LOAD_ROTATION
// push (2026-07-16). Self-wires into BotBridgeService for the HELLO re-push; the activation
// line after Build() below is what actually constructs it before the first bot connects.
builder.Services.AddSingleton<RotationService>();

builder.Services.AddSingleton<BotBrain>();
builder.Services.AddSingleton<BotDiagnosticsService>();

// Brain orchestrator (BackgroundService)
builder.Services.AddSingleton<BotBrainService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BotBrainService>());

// ---------- BotLogic: Chat social layer (CHAT_ARCHITECTURE) ----------
// C0: coordinator stub — drains CHAT_RECV stimuli off the bridge and logs [CHAT-COORD].
// Fully separate from the brain spine (D9): its own BackgroundService, never on a brain tick.
// C1: settings snapshot service (§14.1 — 5s TTL, zone→global resolution, hot-apply)
builder.Services.AddSingleton<ChatSettingsService>();
// C2: reactive whisper MVP — persona, engine, broker (temp, C5 replaces), typing timeline, Tier 0
builder.Services.AddSingleton<PersonaService>();
builder.Services.AddSingleton<PromptAssembler>();
builder.Services.AddSingleton<StylePostPass>();
builder.Services.AddSingleton<ConversationTracker>();
builder.Services.AddSingleton<TypingScheduler>();
builder.Services.AddSingleton<IInferenceBroker, FixedEndpointBroker>();
builder.Services.AddSingleton<IChatEngine, ChatEngine>();
// C3: Tier-1 verbatim memory + relationship bumps (buffered, flushed by the coordinator)
builder.Services.AddSingleton<ChatMemoryStore>();
// C4: arbitration — urge scoring + the anti-storm guards (chain depth, token buckets)
builder.Services.AddSingleton<UrgeScorer>();
builder.Services.AddSingleton<ChainGuard>();
builder.Services.AddSingleton<BudgetBuckets>();
// C6: voice library builder (Batch-class admin action from the Capacity tab)
builder.Services.AddSingleton<VoiceLibraryBuilder>();
builder.Services.AddSingleton<ChatCoordinator>();
builder.Services.AddSingleton<IChatCoordinator>(sp => sp.GetRequiredService<ChatCoordinator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatCoordinator>());
builder.Services.AddSingleton<ChatHealthService>();

// ---------- MVC + SignalR ----------
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpCallContext();

// ---------- MCP (Model Context Protocol) server ----------
// Stateless Streamable HTTP transport on the existing UI port at /mcp.
// Bearer-token auth is enforced by McpBearerAuthMiddleware BEFORE MapMcp so
// unauthenticated clients never see the tool catalogue. The MCP-Protocol-Version
// header negotiation happens inside the SDK; we just host the endpoint.
builder.Services.AddSingleton(sp => McpToolCapabilityRegistry.FromAssemblyScans(
    new[] { typeof(RaTools).Assembly },
    sp.GetRequiredService<ILogger<McpToolCapabilityRegistry>>()));

builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
    })
    .WithTools<RaTools>()
    .WithTools<PlayerTools>()
    .WithTools<ProcessTools>()
    .WithTools<AuditTools>()
    .WithTools<HomeTools>()
    .WithTools<DbcTools>()
    .WithTools<ServerLogTools>()
    .WithTools<ItemTools>()
    .WithTools<GameObjectTools>()
    .WithTools<WorldTools>()
    .WithTools<QuestTools>()
    .WithTools<WorldMapTools>()
    .WithTools<WikiTools>()
    .WithTools<SourceTools>()
    .WithTools<AccountWriteTools>()
    .WithTools<InstanceWriteTools>()
    .WithTools<GameObjectWriteTools>()
    .WithTools<ItemWriteTools>()
    .WithTools<ConfigTools>()
    .WithTools<PlayerWriteTools>()
    .WithTools<SpellWriteTools>()
    .WithTools<WorldsTools>()
    .WithTools<BaselineTools>()
    .WithTools<DivergenceTools>()
    .WithTools<ChangeGraphTools>()
    .WithTools<ActivityTools>()
    .WithTools<BotTools>()
    .WithTools<RotationTools>()
    .WithTools<PatchTools>()
    .WithTools<LootifierTools>()
    .WithResources<ServerHealthResource>()
    .WithResources<PlayerSnapshotResource>()
    .WithResources<BotFleetResource>()
    .WithPrompts<InvestigatePlayerPrompt>()
    .WithPrompts<RestartServerPrompt>()
    .WithPrompts<TriageGriefingPrompt>()
    .WithPrompts<ReviewChangesPrompt>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var retexService = scope.ServiceProvider.GetRequiredService<ItemRetextureService>();
    retexService.LoadExistingRetexturesAsync().GetAwaiter().GetResult();

    var registry = scope.ServiceProvider.GetRequiredService<CacheVersionRegistry>();
    registry.SweepAllOnStartup();
}

// ---------- Database Bootstrap ----------
// Ensures vmangos_admin DB + tables exist before any request can hit AuditService.
// Never throws — logs errors and sets AdminDbReady = false for dashboard to display.
var dbInit = app.Services.GetRequiredService<DbInitializationService>();
await dbInit.InitializeAsync();


await app.Services.GetRequiredService<QuestGraphLoader>().LoadAsync();
await app.Services.GetRequiredService<ZoneSafetyMap>().LoadAsync();
// Vendor/innkeeper NPC cache — backs MaintenancePlanner.GetNearestVendor. Was registered
// as a singleton but its LoadAsync was never invoked at boot, so _vendorsByMap stayed empty
// and every vendor lookup returned null ("no vendors loaded on this map"). Load it here, same
// as the other startup loaders.
await app.Services.GetRequiredService<ZoneDataLoader>().LoadAsync();

// Creature spawn footprints — backs QuestPlanner's Scatter (Build 2). Like the loaders above,
// registered as a singleton but its LoadAsync must be invoked here or _spawnsByEntry stays empty
// and every objective dispatch falls back to the canonical GrindX/GrindY (no scatter, no crash —
// just today's dogpile). Confirm the "CreatureSpawnLoader: cached N spawn points across M entries"
// line at boot.
await app.Services.GetRequiredService<CreatureSpawnLoader>().LoadAsync();

// Class-trainer location cache — backs TrainingPlanner.GetNearestTrainer. Like ZoneDataLoader
// above, registered as a singleton but its LoadAsync must be invoked here or _trainersByClass
// stays empty and every training trip gives up ("no-loader" / "no-trainer-in-range"). Confirm the
// "[SpellProgression] Loaded N trainer spawns across M classes" line at boot.
await app.Services.GetRequiredService<SpellProgressionLoader>().LoadAsync();

// [ROTATION] Eagerly construct RotationService so its SetRotationService(this) wire-in lands
// BEFORE the bridge accepts the first HELLO — a lazily-resolved singleton would otherwise not
// exist until the first API call, and every bot login before that would miss its re-push.
app.Services.GetRequiredService<RotationService>();

// ---------- Pipeline ----------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

// Static files with custom MIME types (GLB for 3D model-viewer)
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".glb"] = "model/gltf-binary";
// Serve /client/ -> /client/index.html for the MSUI Client SPA. Without this,
// UseStaticFiles only serves exact file paths and /client/ returns 404 even
// though index.html is sitting right there.
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html" }
});

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider
});

// ---------- MSUI Client: WebSocket ↔ TCP bridge (design doc DD-4) ----------
// MUST come before UseRouting so the upgrade is handled ahead of MVC route
// matching. UseWebSockets is required even though SignalR is registered —
// SignalR brings its own transport handling and does not enable raw WS.
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<ConsoleHub>("/hubs/console");
app.MapHub<LogStreamHub>("/hubs/logs");
app.MapHub<BotBridgeHub>("/hubs/botbridge");

// ---------- MCP endpoint ----------
// Bearer-token middleware runs first; only requests with a valid MCP_AUTH_TOKEN
// reach MapMcp. The route is configured in appsettings.json under "Mcp:Route"
// (default /mcp). Stateless mode means no Mcp-Session-Id is issued.
app.UseMiddleware<McpAuthMiddleware>();
app.MapMcp("/mcp");

app.Run();
