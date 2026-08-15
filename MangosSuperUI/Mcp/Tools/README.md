# `Mcp/Tools/` conventions

## Namespace

Every tool class lives in `MangosSuperUI.Mcp.Tools`. The capability registry
(`Mcp/Auth/McpToolCapabilityRegistry.cs`) reflects over exactly this
namespace at startup, so adding a tool class anywhere else silently
disables per-call authorisation.

## Class shape

```csharp
[McpServerToolType]
public class FooTools
{
    private readonly IDependency _dep;
    private readonly McpCallContext _ctx;   // optional, for audit attribution
    private readonly ILogger<FooTools> _log;

    public FooTools(IDependency dep, McpCallContext ctx, ILogger<FooTools> log)
    { _dep = dep; _ctx = ctx; _log = log; }

    [McpServerTool(Name = "foo_bar")]
    [McpCapability(McpCapability.Read)]      // required for any non-read tool
    [Description("One-paragraph agent-facing description.")]
    public async Task<string> Bar(
        [Description("Parameter description.")] string param)
    {
        if (string.IsNullOrWhiteSpace(param))
            return McpResult.Failure(ErrorCodes.InvalidInput, "param required").ToJson();

        try { /* ... */ return McpResult.Success(payload).ToJson(); }
        catch (Exception ex) { _log.LogError(ex, "foo_bar failed"); return McpResult.FromException(ex).ToJson(); }
    }
}
```

## Tool-name grid

| Prefix | Domain |
|---|---|
| `player_*`   | characters / accounts / guilds |
| `account_*`  | realmd DB |
| `world_*`    | world DB (creatures, gameobjects, quests, instances) |
| `item_*`     | item_template + sources + retexture |
| `spell_*`    | spell_template + custom spells + DNA |
| `dbc_*`      | DBC lookups |
| `gameobject_*` | gameobject_template + spawns |
| `bot_*`      | BotBridge + BotBrain |
| `rotation_*` | Combat rotations |
| `chat_*`     | BotChat settings |
| `wiki_*`     | Corpus search + browse |
| `source_*`   | C++ source index |
| `log_*`      | logs_* tables |
| `audit_*`    | audit_log + change graph |
| `divergence_*` | live drift vs OG |
| `baseline_*` | OG snapshots |
| `worlds_*`   | suspend / resume / fork / snapshot |
| `config_*`   | mangosd.conf / server-config.json |
| `patch_*`    | patch MPQ lifecycle |
| `comfy_*`    | ComfyUI pool |
| `ollama_*`   | Ollama probe |
| `process_*`  | mangosd / realmd process control |
| `ra_*`       | RemoteAdmin commands |
| `diag_*`     | diagnostics (DB health, ADT, height) |

Rules:
- snake_case, max 64 chars
- no verbs in the name — actions are the parameters
- a tool that returns a list uses `*_list` or `*_search`, never `list_*`

## Capabilities

Every tool MUST declare at least one `[McpCapability]`. The default (no
attribute) is `McpCapability.Read` so a freshly-added tool is safe by
default, but you should be explicit:

| Capability | Granted for |
|---|---|
| `read` | Pure reads. No DB writes, no RA commands. |
| `ra` | Tools that send RemoteAdmin commands to mangosd. |
| `process` | Tools that start/stop/restart mangosd or realmd. |
| `write_db` | Tools that INSERT/UPDATE/DELETE in any DB. |
| `worlds` | Tools that suspend/resume/fork/delete worlds. |
| `bots` | Tools that send commands to bots or mutate bot state. |
| `patches` | Tools that build or rebuild client patch MPQs. |
| `baseline` | Tools that reset DB state from OG snapshots (irreversible). |
| `lootifier` | Tools that generate loot/crafting/quest variants. |
| `retexture` | Tools that generate retextures (ComfyUI/Ollama). |

Stack multiple `[McpCapability]` attributes when a tool needs more than one
(the caller must hold ALL of them).

## Return envelope

Every tool returns one of:

```jsonc
// success
{ "ok": true,  "data": <payload> }

// failure (code/message mandatory; retryable/hint optional)
{ "ok": false, "error": { "code": "INVALID_INPUT", "message": "...",
                            "retryable": false, "hint": "..." } }
```

Use `McpResult.Success(payload)` and `McpResult.Failure(code, message)`
from `Mcp/Common/McpResult.cs`. Free-form JSON shapes are not allowed —
they break agents' ability to `try/catch`.

Error codes are listed in `Mcp/Common/ErrorCodes.cs`. Adding a new one?
Update the doc + the list in `ErrorCodes.All`.

## Audit attribution

Inject `McpCallContext` into the tool class. Use `_ctx.Operator` as the
`operator_` parameter and `_ctx.RemoteIp` as `operatorIp` for every call
into `AuditService.ExecuteAndLogAsync` / `LogAsync`. Never hard-code
`"mcp"` — the audit log should tell you which token made which call.

## DI registration

New tool classes are registered once in `Program.cs`:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<FooTools>()
    .WithTools<BarTools>();
```

`WithTools<T>()` registers every `[McpServerTool]`-attributed public method
on T. No other wiring needed.
