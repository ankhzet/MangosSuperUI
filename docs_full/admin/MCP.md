# MSUI MCP Server — Overview

<!-- documentation: hand-authored; reflects commit 0e35de2 / parent 50ed641 -->

# MSUI MCP Server

The **MSUI MCP server** is a [Model Context Protocol](https://modelcontextprotocol.io)
endpoint embedded inside MangosSuperUI itself. It exposes the entire MSUI
admin surface — RemoteAdmin console, database queries, world / spell / item
/ gameobject / quest / loot management, server logs, wiki + source code
search, worlds lifecycle, OG baseline resets, bot fleet + brain commands,
patch metadata, and config editor — as MCP tools that any MCP-compatible
LLM client can invoke.

This document is the high-level overview. See:

* [`MCP_Architecture`](MCP_Architecture.md) — transport, auth, capability
  model, audit attribution, code layout.
* [`MCP_WireProtocol`](MCP_WireProtocol.md) — JSON-RPC framing,
  Streamable HTTP semantics, the `Mcp-Session-Id` and `MCP-Protocol-Version`
  headers, and how the SDK enforces stateless mode.

## Why it exists

Running a WoW 1.12.1 server end-to-end involves many subsystems: a
telnet-style RemoteAdmin protocol, the `BotBridge` TCP JSON protocol, a
five-schema MariaDB (mangos / characters / realmd / logs / vmangos_admin),
the audit-log-as-graph for "what changed", the world lifecycle for
suspend / resume / fork, and the bot brain for the SuperUiBots fleet.
Until now every one of these was reachable only through the MSUI web
dashboard — a human UI. The MCP server puts the same surface behind a
programmatic protocol an LLM agent can drive.

Concretely, an agent can now:

* Run a player-side RA command (`ra_kick_player`, `ra_ban_account`,
  `ra_announce`).
* Drive the bot fleet (`bot_spawn`, `bot_set_task_grind`, `bot_gear_up`,
  `bot_form_group`, `bot_auto_form_groups`).
* Mutate the world DB (`item_create`, `item_update`, `spell_save`,
  `instance_update_loot`, `config_save_mangosd`).
* Suspend / resume / fork worlds (`worlds_suspend`, `worlds_resume`,
  `worlds_fork`, `worlds_create_rts`).
* Diff and reset to OG baseline (`baseline_diff_item`,
  `baseline_reset_all`, `changegraph_revert_batch`).
* Search the in-process C++ wiki + source index (`wiki_search`,
  `source_search`, `source_smart_search`).

Every mutating call routes through `AuditService.LogAsync` with the
caller's bearer-token label + tool name + remote IP. Before/after state
is captured for every reversible write, so `changegraph_revert_*` can
undo an agent's actions with a single tool call.

## What it exposes

| Category | Count | Capability |
|---|---|---|
| MCP tools | **215** | per-tool (see Capability matrix below) |
| MCP resources | **3** | read-only |
| MCP prompts | **4** | slash-invocable workflow templates |
| Capability tags | **10** | `read`, `ra`, `process`, `write_db`, `worlds`, `bots`, `patches`, `baseline`, `lootifier`, `retexture` |
| Audit attribution | every mutating tool | `AuditService.LogAsync` |
| Bearer-token auth | `Authorization: Bearer …` header | constant-time compare |

### Resources (file-like content the agent can `@-mention`)

| URI | Returns |
|---|---|
| `mcp://msui/health` | One-call JSON snapshot of the live server: process flags for mangosd/realmd, RA connectivity, parsed `.server info`, DB row counts, per-DB ping. |
| `mcp://msui/players/{guid}` | Full player record (character + guild + account) + the last 20 audit_log entries targeting them. `{guid}` is the character guid. |
| `mcp://msui/bots/fleet` | Live bot fleet projection: stalled bots first, then every bot's live context (goal/step/why/timers/pos/target/pending/failure/stall/scratch). |

### Prompts (slash-invocable message templates)

| Slash command | Bundled workflow |
|---|---|
| `/investigate-player {characterName}` | search → detail → account → audit → log_chat, then synthesise. **Does not** propose moderation actions without evidence, **does not** execute RA without confirmation. |
| `/restart-server [delay=5]` | worlds_status → confirm with operator → save_all → shutdown → restart → health check. Reports online player count first. |
| `/triage-griefing {characterName}` | evidence-first summary → exactly one proposed action with full RA command line → confirmation. |
| `/review-changes {domain?}` | divergence + change graph audit → grouped revert plan with risk levels (low/medium/high) → confirmation. Never auto-executes a revert. |

### Capability matrix

Every MCP tool declares one or more `[McpCapability]` attributes. A
bearer token must hold **all** the capabilities a tool requires. The
default — no `[McpCapability]` attribute — is `read`.

| Capability | What it gates |
|---|---|
| `read`      | All Phase 2 read-only tools + `audit_*` + `activity_*` + `process_status/diagnostics`. |
| `ra`        | All `ra_*` tools + `player_revive` / `player_reset_*` / `player_mute` / `player_unmute` / `player_teleport` / `player_gps` + `config_reload_mangosd`. |
| `process`   | `process_start_*` / `process_stop_*` / `process_restart_*`. |
| `write_db`  | Phase 3 content writes (account, realm, item, gameobject, instance loot, config, spell save/teach/unlearn). |
| `worlds`    | `worlds_suspend` / `worlds_resume` / `worlds_fork` / `worlds_create_rts` / `worlds_restore_group` / `worlds_update` / `worlds_snapshot_label` / `worlds_delete_world` / `worlds_delete_snapshot`. |
| `bots`      | `bot_spawn` / `bot_move_to` / `bot_say_text` / `bot_accept_quest` / `bot_complete_quest` / `bot_abandon_quest` / `bot_learn_spell` / `bot_attack_target` / `bot_interact_npc` / `bot_take_flight` / `bot_set_task_grind` / `bot_set_task_idle` / `bot_gear_up` / `bot_toggle_brain` / `bot_form_group` / `bot_disband_group` / `bot_auto_form_groups` / `bot_set_grouping_mode` + `rotation_assign` / `rotation_clear`. |
| `patches`   | `patch_register_at_trainer` / `patch_register_at_class_trainers` / `patch_copy_source_trainers` / `patch_delete_spell` / `patch_teach_spell` / `patch_unlearn_spell`. |
| `baseline`  | `baseline_initialize` / `baseline_reset_item` / `baseline_reset_spell` / `baseline_reset_gameobject` / `baseline_reset_creature_loot` / `baseline_reset_table` / `baseline_reset_all` + `changegraph_revert_entry` / `changegraph_revert_batch`. (Resets are destructive — irreversible.) |
| `lootifier` | Phase-7 placeholder. Currently only `lootifier_status` / `crafting_lootifier_status` / `quest_lootifier_status` are exposed read-only. |
| `retexture` | Phase-7 placeholder. No tools exposed yet. |

A token with an empty `capabilities` array is treated as a **superuser**
(granted all). The legacy `MCP_AUTH_TOKEN` env var is also a superuser
token for back-compat with Phase-0 deployments.

## Quick reference: token JSON for common agents

```jsonc
// Read-only CI bot
{ "token": "tk_xxx", "label": "ci-readonly", "capabilities": ["read"] }

// Operator agent — can do everything except destructive world ops
{ "token": "tk_yyy", "label": "claude-desktop", "capabilities": ["read", "ra", "process", "write_db", "bots", "patches"] }

// Superuser (treat with care)
{ "token": "tk_zzz", "label": "emergency", "capabilities": [] }
```

Configure via the `Mcp.Auth.Tokens` array in `server-config.json` or
the `MCP_TOKENS_JSON` env var (one JSON document, not an array).

## Path Reference

**MSUI/Mcp/Tools/** (C#)
Role: 29 MCP tool classes — every public method annotated with
`[McpServerTool(Name = …)]` becomes a tool. Class organisation:
`RaTools`, `PlayerTools`, `ProcessTools`, `AuditTools`, `HomeTools`,
`DbcTools`, `ServerLogTools`, `ItemTools`, `GameObjectTools`,
`WorldTools`, `QuestTools`, `WorldMapTools`, `WikiTools`, `SourceTools`,
`AccountWriteTools`, `InstanceWriteTools`, `GameObjectWriteTools`,
`ItemWriteTools`, `ConfigTools`, `PlayerWriteTools`, `SpellWriteTools`,
`WorldsTools`, `BaselineTools`, `DivergenceTools`, `ChangeGraphTools`,
`ActivityTools`, `BotTools`, `RotationTools`, `PatchTools`,
`LootifierTools`.

**MSUI/Mcp/Resources/** (C#)
Role: 3 MCP resources — `ServerHealthResource`,
`PlayerSnapshotResource`, `BotFleetResource`.

**MSUI/Mcp/Prompts/** (C#)
Role: 4 MCP prompts — `InvestigatePlayerPrompt`, `RestartServerPrompt`,
`TriageGriefingPrompt`, `ReviewChangesPrompt`.

**MSUI/Mcp/Auth/McpAuthMiddleware.cs** (C#)
Role: Bearer-token authentication + per-tool capability enforcement.
Runs BEFORE `MapMcp` so unauthenticated clients never see the tool
catalogue.

**MSUI/Mcp/Auth/McpToolCapabilityRegistry.cs** (C#)
Role: Assembly scan that maps tool name → required capability set.
Reflected at startup so adding a new tool + capability is automatic.

**MSUI/Mcp/Auth/McpCallContext.cs** (C#)
Role: Per-request scoped DI service carrying the caller's token label,
remote IP, tool name, and JSON-RPC request id. Resolved by tool methods
to stamp `AuditEntry.Operator` so the audit log says
`"claude-desktop/ra_kick_player@10.0.0.42"` rather than `"mcp"`.

**MSUI/Mcp/Common/McpResult.cs** (C#)
Role: Standard `{ok, data}` / `{ok:false, error:{code, message, retryable, hint}}`
JSON envelope. Every tool returns `McpResult.Success(...)` or
`McpResult.Failure(code, message)`. Source-generated JSON context for the
envelope.

**MSUI/Services/RaService.cs** (C#)
Role: TCP client to the mangosd RemoteAdmin server. `MCP.RaTools` wraps
each command in an `AuditService.ExecuteAndLogAsync` call that captures
the before-state (e.g. target character level, mute time) before sending
the RA command. See also [`../../mangosd/remote/RemoteAccess/RASocket.md`](../../mangosd/remote/RemoteAccess/RASocket.md).

**MSUI/Services/BotBridgeService.cs** (C#)
Role: TCP server (listens on `127.0.0.1:3444`) that the C++ `AiBotAI`
processes connect to. `MCP.BotTools` wraps each bot command in
`AuditService.LogAsync` so every move / quest / gear change is
attributed to the MCP caller. See also [`../../game/SuperUiBots/AiBotAI.md`](../../game/SuperUiBots/AiBotAI.md).

**MSUI/Services/AuditService.cs** (C#)
Role: The single sink for `INSERT INTO audit_log`. Every mutating MCP
tool calls `LogAsync` (or `ExecuteAndLogAsync` for RA) with
`McpCallContext`-derived `Operator`, `OperatorIp`, before-state JSON, and
after-state JSON. The ChangeGraphService reads from this table to power
`changegraph_*` tools and the undo path.
