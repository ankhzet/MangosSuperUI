# MSUI MCP Server — Wire Protocol

<!-- documentation: hand-authored; reflects commit 0e35de2 / parent 50ed641 -->

# MSUI MCP Server — Wire Protocol

The MSUI MCP server speaks **Model Context Protocol spec 2025-11-25**
over a stateless Streamable HTTP transport. This document covers the
byte-level wire format: request framing, response framing, error
codes, the headers the server cares about, and the JSON-RPC shapes
the SDK accepts.

## Transport at a glance

| Property | Value |
|---|---|
| URL | `POST http://<host>:5000/mcp` |
| Methods | `POST` (only). `GET` returns 405 — stateless mode doesn't use it. |
| Content-Type (request) | `application/json` |
| Accept (request) | `application/json, text/event-stream` |
| Content-Type (response) | `application/json` (single round-trip; server holds POST open as an SSE stream while the tool runs) |
| Auth | `Authorization: Bearer <token>` |
| Protocol version header | `MCP-Protocol-Version: 2025-06-18` (minimum supported) |
| Session header | none — stateless = no `Mcp-Session-Id` |
| Backpressure | natural — POST stays open until the tool method returns |

## Request framing

```http
POST /mcp HTTP/1.1
Host: localhost:5000
Content-Type: application/json
Accept: application/json, text/event-stream
Authorization: Bearer tk_op_xxx
MCP-Protocol-Version: 2025-06-18
Content-Length: 234

{"jsonrpc":"2.0","id":1,"method":"tools/call",
 "params":{"name":"ra_kick_player",
           "arguments":{"characterName":"Foo"}}}
```

The body is a single JSON-RPC 2.0 request. The `id` field is opaque to
the server but must be unique per call so the client can correlate
responses to requests. `method` is the RPC method; `params` are method-
specific.

## Methods

| `method` | Purpose | Direction |
|---|---|---|
| `initialize` | Handshake. Returns server info + capabilities. **Required first.** | C → S |
| `notifications/initialized` | Client confirms the handshake is complete. Stateless mode: optional, ignored if sent. | C → S |
| `tools/list` | Enumerate every tool the caller's token can invoke (capability-filtered). | C → S |
| `tools/call` | Invoke a tool. | C → S |
| `resources/list` | List the 3 resources (no capability gate). | C → S |
| `resources/read` | Read a resource (server health, player card, bot fleet). | C → S |
| `resources/templates/list` | List resource templates (we expose 1: `mcp://msui/players/{guid}`). | C → S |
| `prompts/list` | List the 4 slash-invocable prompts. | C → S |
| `prompts/get` | Render a prompt's messages. | C → S |
| `ping` | Health probe (used by SDK keepalive). | C → S |
| `notifications/cancelled` | Client aborts an in-flight call. Stateless: not really applicable, but accepted. | C → S |
| `notifications/progress` | Optional — server emits during long tool calls. Stateless: emitted over the SSE stream the POST is held open on. | S → C |

The server does **not** support `logging/setLevel`, `roots/list`,
`sampling/createMessage`, or `elicitation/create` (we have no use for
sampling or elicitation in a server-admin context; clients shouldn't
ask the server to sample completions or elicit input from us).

## `tools/list` response

The capability-filtered tool catalogue:

```json
{
  "jsonrpc": "2.0", "id": 1,
  "result": {
    "tools": [
      {
        "name": "ra_kick_player",
        "description": "Kick a currently-online player by character name. Sends the RA 'kick player <name>' command. ...",
        "inputSchema": {
          "type": "object",
          "properties": {
            "characterName": { "type": "string", "description": "Exact character name of the online player to kick." },
            "reason":        { "type": "string", "description": "Optional reason message stored in the audit log." }
          },
          "required": ["characterName"],
          "additionalProperties": false
        },
        "annotations": { "readOnlyHint": false, "destructiveHint": true, "idempotentHint": false, "openWorldHint": false }
      },
      ...
    ]
  }
}
```

* `inputSchema` is JSON Schema (Draft 2020-12). The SDK generates this
  from the C# `[Description]` attributes on the tool method's
  parameters.
* `annotations` hint whether the tool is destructive — useful for
  clients that want to gate confirmations.
* `name` is the slug used in `tools/call`. Names are stable across
  releases; new tools get new names, never renamed.

For a token with only `["read"]`, the catalogue **does not include**
`ra_*`, `process_*`, `bot_*`, `worlds_*`, `baseline_*`, or any other
write-gated tool. The middleware enforces this at the `tools/call`
level; the catalogue is filtered to avoid leaking existence of tools
the caller can't use.

## `tools/call` request / response

Request:

```json
{
  "jsonrpc": "2.0", "id": 1,
  "method": "tools/call",
  "params": {
    "name": "ra_kick_player",
    "arguments": { "characterName": "Foo" }
  }
}
```

Success response (HTTP 200, the POST body held open as an SSE stream):

```json
{
  "jsonrpc": "2.0", "id": 1,
  "result": {
    "content": [
      { "type": "text", "text": "{\"ok\":true,\"data\":{\"response\":\"Player 'Foo' kicked.\"}}" }
    ],
    "isError": false
  }
}
```

The `text` payload is the standard `McpResult` envelope
`{ok, data}` or `{ok:false, error:{code, message, retryable?, hint?}}`.
Clients should parse the inner JSON.

Failure response (HTTP 200 + JSON-RPC result with `isError: true`,
unless the failure is at the protocol layer — then it's an HTTP error):

```json
{
  "jsonrpc": "2.0", "id": 1,
  "result": {
    "content": [
      { "type": "text", "text": "{\"ok\":false,\"error\":{\"code\":\"RA_DISCONNECTED\",\"message\":\"connection refused\",\"retryable\":true,\"hint\":\"ra_reconnect\"}}" }
    ],
    "isError": true
  }
}
```

`code` values come from `MSUI/Mcp/Common/McpResult.cs`'s `ErrorCodes`
constant set:

| `code` | Meaning | `retryable` |
|---|---|---|
| `RA_DISCONNECTED` | `RaService.SendCommandAsync` threw — socket closed, auth failed, or mangosd not running | `true` (try `ra_reconnect`) |
| `DB_UNAVAILABLE` | DB query failed (connection refused, schema missing, timeout) | `true` |
| `DB_TIMEOUT` | Same as above, specifically a `MySqlException` with a timeout code | `true` |
| `INVALID_INPUT` | Bad argument (empty name, out-of-range number, missing required field) | `false` |
| `PERMISSION_DENIED` | Capability check failed (returns HTTP 403 instead of this — see below) | `false` |
| `NOT_FOUND` | Lookup miss (player guid doesn't exist, entry has no baseline row) | `false` |
| `PARTIAL` | Read partially succeeded (e.g. baseline missing) | `false` |
| `CONFLICT` | Insert would duplicate a row (loot row, etc.) | `false` |
| `INTERNAL` | Unhandled exception. The body of the exception is in `message`. | `false` |
| `RATE_LIMITED` | Reserved — no rate limiter wired yet (Phase 6+). | `true` |

## HTTP error codes (protocol-layer failures)

| Status | `WWW-Authenticate` | When |
|---|---|---|
| `401 Unauthorized` | `Bearer realm="mcp", error="invalid_token"` | Missing / malformed / wrong bearer token |
| `403 Forbidden` | `Bearer realm="mcp", error="insufficient_scope", error_description="Token 'X' lacks capability 'Y' required by 'Z'."` | Token valid, but missing a required capability for the named tool |
| `405 Method Not Allowed` | — | `GET /mcp` (stateless doesn't use it) or `DELETE` |
| `500 Internal Server Error` | — | Unhandled exception in the middleware before the tool runs |
| `400 Bad Request` | — | Malformed JSON-RPC body (parse error before we can peek) |

## `initialize` handshake

```json
// request
{"jsonrpc":"2.0","id":1,"method":"initialize",
 "params":{"protocolVersion":"2025-06-18",
           "capabilities":{},
           "clientInfo":{"name":"claude-desktop","version":"1.0.61"}}}

// response
{"jsonrpc":"2.0","id":1,
 "result":{"protocolVersion":"2025-06-18",
          "capabilities":{"tools":{"listChanged":false},
                          "resources":{"subscribe":false,"listChanged":false},
                          "prompts":{"listChanged":false}},
          "serverInfo":{"name":"msui","version":"1.2.2"}}}
}
```

We declare `listChanged: false` everywhere — the catalogue is static
for a given build. Clients should refresh on their own schedule, not
expect server-pushed changes.

## Resources (`mcp://msui/...`)

### `resources/read` request

```json
{"jsonrpc":"2.0","id":1,"method":"resources/read",
 "params":{"uri":"mcp://msui/health"}}
```

Response:

```json
{
  "jsonrpc":"2.0","id":1,
  "result":{"contents":[{"uri":"mcp://msui/health",
                       "mimeType":"application/json",
                       "text":"{\"capturedAt\":\"2026-08-15T20:42:13Z\",\"mangosd\":{...}}"}]}
}
```

Resources return their content as a single `TextResourceContents` (or
`BlobResourceContents` for binary — we have no binary resources).
`mcp://msui/players/{guid}` is the only resource **template** — the
SDK advertises it via `resources/templates/list`.

## Prompts (`/investigate-player` etc.)

### `prompts/list`

```json
{"jsonrpc":"2.0","id":1,"method":"prompts/list",
 "params":{}}

{"jsonrpc":"2.0","id":1,
 "result":{"prompts":[
   {"name":"investigate_player",
    "title":"Investigate Player",
    "description":"Bundle the standard player-investigation workflow...",
    "arguments":[
      {"name":"characterName","description":"Character name...","required":true},
      {"name":"context","description":"Optional notes...","required":false}
    ]},
   {"name":"restart_server", "title":"Restart Server", ...},
   {"name":"triage_griefing", "title":"Triage Griefing Report", ...},
   {"name":"review_changes", "title":"Review Changes", ...}
  ]}}
```

### `prompts/get`

```json
{"jsonrpc":"2.0","id":1,"method":"prompts/get",
 "params":{"name":"triage_griefing",
           "arguments":{"characterName":"Foo",
                        "reportContext":"griefing in WSG"}}}
```

Response:

```json
{"jsonrpc":"2.0","id":1,
 "result":{
   "description":"Triage griefing report against 'Foo'.",
   "messages":[
     {"role":"system",
      "content":{"type":"text",
                 "text":"You are triaging a griefing report on a WoW 1.12.1..."}},
     {"role":"user",
      "content":{"type":"text",
                 "text":"Triage a griefing report against 'Foo'. Report context: griefing in WSG"}}
   ]}}
}
```

The client feeds these messages into its own LLM as the system/user
turns. The server never invokes an LLM — it's the client's job.

## Progress notifications (optional, long tool calls)

For tool calls expected to take >1s (e.g. `worlds_resume`, `baseline_reset_all`),
the server MAY emit `notifications/progress` over the SSE stream the
POST is held open on:

```json
{"jsonrpc":"2.0","method":"notifications/progress",
 "params":{"progressToken":"req-1",
           "progress":42,
           "total":100,
           "message":"Restored creature_loot_template (42/100 tables)"}}
```

Stateless mode: the SSE stream lives for the duration of the tool
call, so progress notifications reach the client before the final
result. Most clients ignore them; Claude Desktop shows them in the
tool progress UI.

## Path Reference

**ModelContextProtocol.Protocol** (NuGet — `ModelContextProtocol.Core`)
Role: All DTOs (`JsonRpcRequest`, `JsonRpcResponse`, `CallToolRequest`,
`ListToolsResult`, `TextResourceContents`, `PromptMessage`, etc.) and the
JSON-RPC framing. Spec version: 2025-11-25.

**ModelContextProtocol.Server** (NuGet — `ModelContextProtocol`)
Role: `McpServerBuilder` + `McpServerResource` + `McpServerTool` + the
attribute-based registration. `WithHttpTransport(...)` configures the
server transport.

**ModelContextProtocol.AspNetCore** (NuGet)
Role: `AddMcpServer()` + `MapMcp(path)` — the ASP.NET integration.
`WithTools<T>()`, `WithResources<T>()`, `WithPrompts<T>()` all use
reflection over the supplied types.
