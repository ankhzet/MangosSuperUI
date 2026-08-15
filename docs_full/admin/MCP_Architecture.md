# MSUI MCP Server — Architecture

<!-- documentation: hand-authored; reflects commit 0e35de2 / parent 50ed641 -->

# MSUI MCP Server — Architecture

How the MCP server sits inside the MangosSuperUI ASP.NET process, what
runs when a tool is invoked, and how it preserves the same audit /
capability / envelope semantics that the existing UI controllers use.

## Where it lives

The MCP server is an embedded ASP.NET endpoint on the existing MSUI web
app — it is **not** a separate process. The same Kestrel host that serves
`/` (the dashboard), `/hubs/*` (SignalR), and `/api/*` (controllers)
also serves `/mcp` (the MCP endpoint).

```
┌──────────────────────────────────────────────────────────────┐
│  MSUI web app (ASP.NET Core 8.0, single Kestrel host)         │
│                                                              │
│  /                       (dashboard)                         │
│  /hubs/console           (SignalR)                           │
│  /hubs/logs              (SignalR)                           │
│  /api/...                (controllers — JSON over HTTP)       │
│  /mcp                    (MCP — Streamable HTTP)              │
│                                                              │
│  Middleware pipeline:                                        │
│    UseRouting                                                │
│    UseAuthentication                                          │
│    McpAuthMiddleware  ◄── bearer token + capability check    │
│    MapMcp("/mcp")                                             │
│                                                              │
│  DI services (singletons):                                   │
│    McpToolCapabilityRegistry  (assembly scan → caps map)      │
│    RaService               (TCP client to mangosd RA)         │
│    BotBridgeService        (TCP server on 127.0.0.1:3444)    │
│    ProcessManagerService   (Process.Kill + /proc scan)       │
│    WorldStateService       (suspend/resume/fork registry)     │
│    ChangeGraphService      (audit_log → graph)               │
│    DivergenceService       (live drift vs og_* tables)        │
│    BaselineService         (OG baseline snapshots)            │
│    AuditService            (the single audit_log writer)      │
│    ConnectionFactory       (5 connection strings)            │
│    …                                                           │
│                                                              │
│  Scoped (one per HTTP request):                              │
│    McpCallContext          (caller label + tool + IP + reqId)│
└──────────────────────────────────────────────────────────────�
```

The same DI services the controllers use are also resolved by the MCP
tool methods — there is no parallel implementation. `Mcp/RaTools.cs`
literally wraps `RaService.SendCommandAsync`; `Mcp/ItemTools.cs` literally
wraps `ConnectionFactory.Mangos()`; etc.

## Request lifecycle (one tool call)

```
client (Claude Desktop / VS Code / curl)
  │
  │  POST /mcp
  │    Content-Type: application/json
  │    Accept: application/json, text/event-stream
  │    Authorization: Bearer tk_xxx
  │    MCP-Protocol-Version: 2025-06-18
  │    {"jsonrpc":"2.0","id":1,"method":"tools/call",
  │     "params":{"name":"ra_kick_player",
  │               "arguments":{"characterName":"Foo"}}}
  ▼
┌────────────────────────────────────────────────────────┐
│ McpAuthMiddleware                                         │
│   1. Extract bearer token (Authorization header)         │
│   2. Constant-time compare against McpAuthOptions.Tokens │
│      AND against legacy MCP_AUTH_TOKEN env var            │
│   3. If no token: 401 invalid_token (rejects before      │
│      MapMcp, so the tool catalogue isn't leaked)          │
│   4. Peek JSON body, extract method=tools/call +         │
│      params.name (tool name) via JsonDocument            │
│   5. Look up required capabilities in registry            │
│   6. If token lacks any required cap:                     │
│      403 insufficient_scope (per-tool, not per-request)  │
│   7. Stamp HttpContext.Items[McpCallContext.HttpContextKey]│
│      with caller label, tool name, remote IP, req id     │
│   8. Rewind the body stream for MapMcp                    │
└────────────────────────────────────────────────────────┘
  │
  ▼
┌────────────────────────────────────────────────────────┐
│ MapMcp("/mcp") — SDK-internal                            │
│   1. Re-reads the body, parses JSON-RPC                    │
│   2. Looks up the tool in McpToolCapabilityRegistry       │
│   3. Resolves the tool's containing class from DI         │
│      (everything is AddSingleton, scoped per the SDK)     │
│   4. Reads McpCallContext from the scoped DI              │
│   5. Invokes the [McpServerTool] method                   │
│      (parameters bound from JSON by the SDK)              │
└────────────────────────────────────────────────────────┘
  │
  ▼
┌────────────────────────────────────────────────────────┐
│ Tool method (e.g. RaTools.KickPlayer)                    │
│   1. Validate args (null/empty checks, range checks)      │
│   2. Call McpResult.Failure(ErrorCodes.X, ...) on invalid│
│   3. Call underlying service:                             │
│        AuditService.ExecuteAndLogAsync(                   │
│          RaService, ".kick player Foo",                   │
│          operator_: McpCallContext.Operator,             │
│          operatorIp: McpCallContext.RemoteIp,            │
│          notes: "MCP tool: ra_kick_player")                │
│   4. ExecuteAndLogAsync does:                             │
│        a. Capture before-state via StateCaptureService    │
│        b. Send the RA command via RaService               │
│        c. Capture after-state                             │
│        d. INSERT INTO audit_log                           │
│   5. Return McpResult.Success(new { response })          │
└────────────────────────────────────────────────────────�
  │
  │ 200 OK, application/json
  │ {"jsonrpc":"2.0","id":1,"result":{
  │   "content":[{"type":"text","text":"{\"ok\":true,\"data\":{...}}"}],
  │   "isError":false}}
  ▼
client
```

## Transport: Streamable HTTP (stateless)

We use the **Streamable HTTP** transport (MCP spec 2025-11-25) in
**stateless** mode:

* `POST /mcp` — JSON-RPC request → JSON-RPC response (single round-trip).
  The server holds the POST body open as an SSE stream while the tool
  handler runs, providing natural HTTP-level backpressure.
* `GET /mcp` — **rejected** with `405 method_not_allowed`. Stateless
  mode has no use for GET (it's the long-lived SSE stream in stateful
  mode, which we deliberately don't enable).
* `Mcp-Session-Id` header — **not issued**. Stateless = no session.
* `MCP-Protocol-Version` header — handled by the SDK. Clients send
  `2025-06-18` (the minimum we support); older versions would 400.

Why stateless: each tool call is independent, the server doesn't need
to track per-client state, and horizontal scaling becomes a non-issue
(no session affinity required). The SDK enforces this via
`WithHttpTransport(o => o.Stateless = true)`.

See [`MCP_WireProtocol`](MCP_WireProtocol.md) for byte-level framing
details.

## Authentication

Bearer token, constant-time compared. Three token sources are checked in
order:

1. `MCP_TOKENS_JSON` env var — JSON document describing a token allowlist:
   ```json
   [
     { "token": "tk_readonly_xxx", "label": "ci",        "capabilities": ["read"] },
     { "token": "tk_op_xxx",        "label": "operator", "capabilities": ["read", "ra", "write_db", "bots", "patches"] }
   ]
   ```
   The legacy single-token case is a one-element array.
2. `MCP_AUTH_TOKEN` env var — a single superuser token. Kept for
   back-compat with Phase-0 deployments. If set AND present in
   `MCP_TOKENS_JSON`, the env-var token wins for that specific match.
3. Built-in superuser fallback — there is none. If no token matches,
   `401 invalid_token`.

A token's `capabilities` array (or empty array = superuser) is
intersected with each tool's required capability set. Missing
capability → `403 insufficient_scope`.

## Code layout

```
MSUI/Mcp/
├── Options/McpOptions.cs                   ─ Mcp / McpAuth / McpAudit POCOs
├── Auth/
│   ├── McpCapabilityAttribute.cs            ─ [McpCapability("ra")] on tool methods
│   ├── McpToolCapabilityRegistry.cs        ─ assembly scan, frozen Lookup[]
│   ├── McpCallContext.cs                   ─ per-request: label, tool, IP, reqId
│   ├── McpCallContextServiceExtensions.cs  ─ AddMcpCallContext() DI glue
│   └── McpAuthMiddleware.cs                ─ bearer + body-peek + 401/403
├── Common/McpResult.cs                     ─ {ok, data|error} envelope + ErrorCodes
├── Tools/                                  ─ 29 classes × 215 tools
│   ├── RaTools.cs        ─ ra_send_command, ra_kick_player, ...
│   ├── PlayerTools.cs    ─ player_search, player_detail, ...
│   ├── BotTools.cs       ─ bot_list, bot_state, bot_spawn, bot_move_to, ...
│   ├── WorldsTools.cs    ─ worlds_suspend, worlds_resume, ...
│   ├── BaselineTools.cs  ─ baseline_diff_item, baseline_reset_all, ...
│   ├── ...                ─ 24 more classes
├── Resources/                              ─ 3 classes
│   ├── ServerHealthResource.cs             ─ mcp://msui/health
│   ├── PlayerSnapshotResource.cs           ─ mcp://msui/players/{guid}
│   └── BotFleetResource.cs                 ─ mcp://msui/bots/fleet
└── Prompts/                                ─ 4 classes
    ├── InvestigatePlayerPrompt.cs          ─ /investigate-player {name}
    ├── RestartServerPrompt.cs             ─ /restart-server [delay]
    ├── TriageGriefingPrompt.cs            ─ /triage-griefing {name}
    └── ReviewChangesPrompt.cs              ─ /review-changes {domain?}
```

`Program.cs` wires everything:

```csharp
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));

builder.Services.AddSingleton(sp => McpToolCapabilityRegistry.FromAssemblyScans(
    new[] { typeof(RaTools).Assembly },
    sp.GetRequiredService<ILogger<McpToolCapabilityRegistry>>()));

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<RaTools>()        .WithTools<PlayerTools>()
    .WithTools<ProcessTools>()    .WithTools<AuditTools>()
    .WithTools<HomeTools>()       .WithTools<DbcTools>()
    .WithTools<ServerLogTools>()  .WithTools<ItemTools>()
    .WithTools<GameObjectTools>().WithTools<WorldTools>()
    .WithTools<QuestTools>()     .WithTools<WorldMapTools>()
    .WithTools<WikiTools>()      .WithTools<SourceTools>()
    .WithTools<AccountWriteTools>().WithTools<InstanceWriteTools>()
    .WithTools<GameObjectWriteTools>().WithTools<ItemWriteTools>()
    .WithTools<ConfigTools>()    .WithTools<PlayerWriteTools>()
    .WithTools<SpellWriteTools>().WithTools<WorldsTools>()
    .WithTools<BaselineTools() .WithTools<DivergenceTools>()
    .WithTools<ChangeGraphTools>().WithTools<ActivityTools>()
    .WithTools<BotTools>()       .WithTools<RotationTools>()
    .WithTools<PatchTools>()     .WithTools<LootifierTools>()
    .WithResources<ServerHealthResource>()
    .WithResources<PlayerSnapshotResource>()
    .WithResources<BotFleetResource>()
    .WithPrompts<InvestigatePlayerPrompt>()
    .WithPrompts<RestartServerPrompt>()
    .WithPrompts<TriageGriefingPrompt>()
    .WithPrompts<ReviewChangesPrompt>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpCallContext();

app.UseMiddleware<McpAuthMiddleware>();
app.MapMcp("/mcp");
```

The `McpToolCapabilityRegistry.FromAssemblyScans` call is critical:
it runs at first DI resolution (singleton), reflects over
`MangosSuperUI.Mcp.Tools` for every public method with
`[McpServerTool]` + `[McpCapability]` attributes, and builds a frozen
`ToolName → Capabilities[]` map. Adding a new tool class + capability
attribute is automatic — no per-tool wiring.

## Audit attribution

Every mutating tool stamps `audit_log` with the caller's bearer-token
label, tool name, and remote IP. Concretely, `McpCallContext.Operator`
is `"<label>/<toolName>[@<remoteIp>]"` — e.g.
`"claude-desktop/ra_kick_player@10.0.0.42"`.

This makes the audit log a precise forensic trail: you can `SELECT
DISTINCT operator FROM audit_log WHERE category = 'RA'` and see exactly
which MCP client did what. The `MCP_OPERATOR_NAME` config key lets
operators override the default label per deployment (e.g. set it to
`"prod-claude-1"` so log analytics can split prod from dev).

## Path Reference

**MSUI/Mcp/Auth/McpAuthMiddleware.cs** (C#)
Role: Bearer auth + per-tool capability enforcement. The two-stage
design (authenticate, then peek body to authorise) means an
unauthenticated request never sees the tool catalogue — the catalogue
itself is capability-filtered by the registry, so a read-only token
won't see `bot_spawn` or `worlds_suspend` in its `tools/list` response.

**MSUI/Mcp/Auth/McpToolCapabilityRegistry.cs** (C#)
Role: Assembly reflection → frozen `(toolName, capabilities[])[]` map.
Built once at first resolution, immutable thereafter.

**MSUI/Mcp/Auth/McpCallContext.cs** (C#)
Role: Per-request scoped DI service. Resolved by tool methods to stamp
the audit log. Returns `"anonymous"` + empty tool name when the request
hits a non-/mcp endpoint or comes from a background service.

**MSUI/Mcp/Common/McpResult.cs** (C#)
Role: Standard `{ok, data}` envelope. `McpResult.Success(...)`,
`McpResult.Failure(code, message, retryable?, hint?)`,
`McpResult.FromException(ex, code?)`. Source-generated JSON context for
AOT compat.

**MSUI/Program.cs** (C#)
Role: DI wiring. The block above.

**ModelContextProtocol.AspNetCore (NuGet)** (C#)
Role: The official MCP C# SDK. 2.2.0+ for spec 2025-11-25.
`AddMcpServer()` + `WithHttpTransport(o => o.Stateless = true)` is the
core primitive.
