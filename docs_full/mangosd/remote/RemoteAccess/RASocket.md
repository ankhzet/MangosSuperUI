# RASocket

<!-- documentation: model-written from source via the local LLM; review before trusting -->

# RASocket

## Purpose & Responsibilities

`RASocket` implements the server-side handler for the **Remote Administration (RA)** interface, a text-based telnet-style protocol allowing administrators to connect to the MaNGOS server and execute console commands remotely. Each instance of `RASocket` manages the lifecycle of a single TCP connection, handling the asynchronous I/O, stateful authentication flow, and command dispatching.

The class operates as a finite state machine with three distinct states:
1.  **FreshConnection**: The initial state where the server expects the client to provide a username.
2.  **GotUsername**: The intermediate state where the server expects the client to provide a password.
3.  **Authenticated**: The operational state where the server accepts and executes administrative commands.

Key responsibilities include:
*   Managing the underlying `AsyncSocket` for non-blocking read/write operations.
*   Parsing incoming byte streams into line-delimited text commands, handling potential fragmentation and telnet negotiation packets.
*   Authenticating users against the `AccountMgr` system, enforcing minimum security levels configured via `Ra.MinLevel`.
*   Dispatching validated commands to the global `World` singleton for execution, capturing output, and relaying results back to the client.
*   Enforcing buffer size limits to prevent denial-of-service attacks during the unauthenticated phase.

## Member-by-Member Behavior

### Initialization and Lifecycle

**`RASocket` (Constructor)**
Initializes the socket wrapper and sets the connection state to `FreshConnection`. It reads the configuration for restricted mode (`Ra.Restricted` or the deprecated `Ra.Stricted`). If restricted mode is enabled (default `true`), administrators connecting via RA will retain their standard account security level. If disabled, administrators (`SEC_ADMINISTRATOR`) are elevated to `SEC_CONSOLE` privileges, allowing them to execute higher-level console commands.

**`~RASocket` (Destructor)**
Logs the closure of the remote administration connection, including the remote IP address. This serves as an audit trail for disconnections.

**`Start`**
Initiates the RA session. It first attempts to fixate the memory location of the underlying socket. If successful, it logs the incoming connection and sends a welcome message composed of the server's Message of the Day (MOTD) and a localized prompt requesting the username. It then triggers the input reception loop by calling `SendAndRecvNextInput`, which schedules the next read operation.

### Input Processing and State Machine

**`DoRecvIncomingData`**
This is the core asynchronous read handler. It performs two main tasks:
1.  **Line Parsing**: It checks the `m_pendingInputBuffer` for newline characters (`\r\n`). If a complete line is found, it extracts the line, removes it from the buffer, and passes it to `HandleInput`. It includes a heuristic check for lines exactly 4095 characters long, logging a warning if detected, as this often indicates a terminal buffer limit issue that may cause command truncation.
2.  **Asynchronous Reading**: If no complete line is available, it checks if the buffer exceeds `MAX_INPUT_BUFFER_SIZE_WHILE_UNAUTHENTICATED` (128 bytes). If the connection is not yet authenticated and the buffer is too large, it logs an error and implicitly closes the socket to mitigate resource exhaustion attacks. Otherwise, it allocates a 1024-byte buffer and initiates an async read.
    *   **Telnet Negotiation**: On the very first packet received (`m_atLeastOnePacketWasReceived` is false), it checks for the Telnet IAC byte (`0xFF`). If present, it assumes the client is attempting protocol negotiation. Since RA does not support complex telnet options, it sends a simple "End of Negotiation" response (`0xFF, 0xF0`) and immediately resumes reading data, effectively ignoring the negotiation payload.

**`HandleInput`**
A dispatcher that routes the parsed text line to the appropriate handler based on the current `m_connectionState`:
*   `FreshConnection` → `HandleInput_FreshConnection`
*   `GotUsername` → `HandleInput_GotUsername`
*   `Authenticated` → `HandleInput_Authenticated`
*   Any other state triggers an assertion failure, indicating a logic error in the state machine.

**`HandleInput_FreshConnection`**
Stores the received line as the `m_username` and transitions the state to `GotUsername`. It responds with a localized prompt requesting the password.

**`HandleInput_GotUsername`**
Performs the authentication sequence:
1.  Retrieves the minimum required account level from config (`Ra.MinLevel`, default `SEC_ADMINISTRATOR`).
2.  Validates the username by checking if it exists in `AccountMgr`.
3.  Validates the password using `AccountMgr::CheckPassword`.
4.  Checks if the account's security level meets the minimum requirement.
5.  If all checks pass, it updates the local `m_accountLevel`. If the account is an administrator and restricted mode is off, it elevates the level to `SEC_CONSOLE`.
6.  On success, it transitions to `Authenticated`, logs the login, and sends a success message followed by the command prompt.
7.  On failure, it sends an error message and disconnects the client using `SendAndDisconnect`.

**`HandleInput_Authenticated`**
Handles command execution for logged-in users:
1.  Ignores empty lines, simply resending the prompt.
2.  Terminates the connection if the command is `quit`, `exit`, or `logout`.
3.  For valid commands, it creates a temporary `InvokeOutputEnvironment` structure on the heap. This structure holds a shared pointer to the `RASocket` instance and a string buffer for accumulating command output.
4.  It queues a `CliCommandHolder` with the `World` singleton. This holder contains the command string, the user's account ID and level, and two callbacks:
    *   An output callback that appends printed text to the environment's buffer.
    *   A completion callback that appends a status symbol (`+` for success, `-` for failure) and the prompt, then sends the accumulated output back to the client via `SendAndRecvNextInput` and deletes the environment structure.

### Output Handling

**`SendAndDisconnect`**
Sends a final message to the client and closes the connection. It converts the string message to a raw byte array and writes it to the socket. The write callback logs any errors but does not attempt further action, as the connection is intended to be terminated.

**`SendAndRecvNextInput`**
Sends a message to the client and immediately prepares to receive the next input. Like `SendAndDisconnect`, it converts the string to bytes and writes it asynchronously. Upon successful write completion, it calls `DoRecvIncomingData` to resume the read cycle. If the write fails, it logs the error and stops processing for this connection.

## Cross-Unit Boundaries

*   **`AsyncSocket` (IO/Networking)**: `RASocket` wraps an `AsyncSocket` instance. It relies on `AsyncSocket` for low-level TCP operations: `InitializeAndFixateMemoryLocation` for setup, `ReadSome` for async reading, and `Write` for async writing. It also uses `GetRemoteIpString` for logging and identification.
*   **`Config`**: Used during construction to determine if the RA interface is in restricted mode (`Ra.Restricted`/`Ra.Stricted`) and during authentication to determine the minimum required security level (`Ra.MinLevel`).
*   **`AccountMgr`**: Used during `HandleInput_GotUsername` to validate credentials (`GetId`, `CheckPassword`) and retrieve the account's security level (`GetSecurity`).
*   **`ObjectMgr`**: Used in `Start` and `HandleInput_FreshConnection` to retrieve localized strings for the username and password prompts (`GetMangosStringForDBCLocale`).
*   **`World`**: Used in `Start` to get the Message of the Day (`GetMotd`) and in `HandleInput_Authenticated` to queue commands for execution (`QueueCliCommand`).
*   **`Master`**: The `Start` method is called by `Master::SetupRemoteAccessServer`, indicating that `RASocket` instances are created and initialized by the master server component when a new RA connection is accepted.
*   **`CliCommandHolder`**: Instantiated in `HandleInput_Authenticated` to encapsulate the command and its context for asynchronous execution by the `World` singleton.
*   **`Log`**: Extensively used throughout the class for debugging, error reporting, and audit logging of connections, authentications, and commands.
*   **`Errors`**: `HandleInput` asserts on unexpected states, potentially triggering stack traces via `PrintStacktraceAndThrow` if assertions are enabled.

## Data Model

`RASocket` does not directly interact with database tables. It relies on `AccountMgr` for authentication, which in turn accesses the `account` table (implied by `GetId`, `CheckPassword`, `GetSecurity`), but `RASocket` itself contains no SQL queries or direct database access logic.

## Notable Implementation Details

*   **Telnet Negotiation Bypass**: The code explicitly handles the Telnet IAC (`0xFF`) byte on the first packet. Instead of implementing a full telnet negotiation protocol, it sends a minimal "End of Negotiation" response and discards the rest of the negotiation data. This simplifies the implementation but may cause issues with clients that strictly require specific option negotiations.
*   **Buffer Limit Enforcement**: A hard limit of 128 bytes is enforced on the input buffer while the connection is unauthenticated. This is a security measure to prevent attackers from holding open connections and filling memory with partial data.
*   **Heap Allocation for Command Context**: In `HandleInput_Authenticated`, the `InvokeOutputEnvironment` is allocated on the heap using `new`. This is necessary because the command execution is asynchronous, and the object must survive beyond the scope of the `HandleInput_Authenticated` call. The object is deleted in the completion callback. This pattern avoids stack overflow risks but requires careful management to prevent leaks if the callback is not invoked (though `CliCommandHolder` presumably guarantees invocation).
*   **Restricted Mode Privilege Escalation**: If `Ra.Restricted` is false, administrators are granted `SEC_CONSOLE` privileges. This allows them to execute commands that might otherwise be restricted to the server console itself, such as reloading configurations or shutting down the server. This is a significant security consideration.
*   **Implicit Socket Closure**: Several methods (`Start`, `DoRecvIncomingData`, `SendAndDisconnect`) rely on "implicit close" semantics. When an error occurs or a condition is met (like buffer overflow), the method returns without explicitly closing the socket. The assumption is that the `AsyncSocket` or the owning `Master` component will detect the idle/error state and clean up the resource. This design choice simplifies error handling paths but requires confidence in the underlying socket manager's cleanup logic.
*   **Line Length Warning**: The check for a 4095-character line is a heuristic. It warns that the command might be truncated due to terminal limitations, but it does not prevent execution. This could lead to subtle bugs if a long command is cut off mid-syntax.

## Member Reference

**`RASocket`**: Constructor that initializes the socket, sets the initial state to `FreshConnection`, and configures the restricted mode flag based on server configuration.

**`~RASocket`**: Destructor that logs the disconnection event with the remote IP address.

**`Start`**: Initializes the socket memory, logs the connection, sends the MOTD and username prompt, and begins the input reception loop.

**`DoRecvIncomingData`**: Asynchronously reads data from the socket, parses complete lines from the buffer, handles telnet negotiation packets, enforces buffer size limits for unauthenticated connections, and dispatches parsed lines to `HandleInput`.

**`HandleInput`**: Dispatches the parsed input line to the appropriate state-specific handler (`HandleInput_FreshConnection`, `HandleInput_GotUsername`, or `HandleInput_Authenticated`) based on the current connection state.

**`HandleInput_FreshConnection`**: Stores the received username, transitions the state to `GotUsername`, and prompts for the password.

**`HandleInput_GotUsername`**: Validates the username and password against `AccountMgr`, checks the account security level against the configured minimum, handles privilege escalation if restricted mode is off, and transitions to `Authenticated` on success or disconnects on failure.

**`HandleInput_Authenticated`**: Processes commands from authenticated users. It handles quit/exit/logout commands, queues other commands to `World` via `CliCommandHolder`, and manages the asynchronous collection and transmission of command output.

**`SendAndDisconnect`**: Sends a final message to the client and initiates the disconnection process by writing to the socket and relying on implicit closure.

**`SendAndRecvNextInput`**: Sends a message to the client and schedules the next asynchronous read operation by calling `DoRecvIncomingData` upon successful write completion.

---

## MCP integration

The RemoteAdmin protocol documented above is also exposed to LLM agents
through MSUI's MCP server. See [`../../admin/MCP.md`](../../admin/MCP.md)
for the high-level overview and
[`../../admin/MCP_Architecture.md`](../../admin/MCP_Architecture.md) for
the request lifecycle (the C# `RaService` opens a TCP connection to
this RASocket and sends text commands; `Mcp/RaTools.cs` wraps every
command in `AuditService.ExecuteAndLogAsync` so the audit log captures
the before-state — e.g. the target character's level + mute time before
`.mute <name> <minutes> <reason>` runs).

Relevant MCP tools (all require the `ra` capability):

* `ra_send_command` — raw text command (audit-logged).
* `ra_server_info` / `ra_list_online` — read-only queries.
* `ra_kick_player` / `ra_announce` / `ra_save_all` / `ra_shutdown` / `ra_connection_status` — session-level actions.
* `ra_ban_account` / `ra_unban_account` — account-level actions.
* `player_revive` / `player_reset_talents` / `player_reset_spells` /
  `player_reset_all` / `player_mute` / `player_unmute` / `player_teleport` /
  `player_gps` — per-character actions routed through RA.
* `config_reload_mangosd` — sends `.reload config`.

See [`../../admin/MCP_WireProtocol.md`](../../admin/MCP_WireProtocol.md) for
the byte-level framing.


<!-- machine-true, projected from graph.json -->

## Map — RASocket

*Source:* RASocket.cpp, RASocket.h

| Member | Kind | Calls out (other units) | Called by (other units) | Tables |
|---|---|---|---|---|
| RASocket | ctor | AsyncSocket.Main/AsyncSocket, Config/GetBoolDefault, Config/IsSet, Log.Main/Out | — | — |
| ~RASocket | dtor | AsyncSocket.Main/GetRemoteIpString, Log.Main/Out | — | — |
| Start | method | AsyncSocket.Main/GetRemoteIpString, AsyncSocket._posix/InitializeAndFixateMemoryLocation, Log.Main/Out, NetworkError/ToString, ObjectMgr/GetMangosStringForDBCLocale, World/GetMotd | Master/SetupRemoteAccessServer | — |
| DoRecvIncomingData | method | AsyncSocket.Main/GetRemoteIpString, AsyncSocket._posix/ReadSome, AsyncSocket._posix/Write, Log.Main/Out, NetworkError/ToString, ReadableBuffer/ReadableBuffer#8 | — | — |
| HandleInput | method | Errors/PrintStacktraceAndThrow | — | — |
| HandleInput_FreshConnection | method | ObjectMgr/GetMangosStringForDBCLocale | — | — |
| HandleInput_GotUsername | method | AccountMgr/CheckPassword, AccountMgr/GetId, AccountMgr/GetSecurity, AsyncSocket.Main/GetRemoteIpString, Config/GetIntDefault, Log.Main/Out | — | — |
| HandleInput_Authenticated | method | AsyncSocket.Main/GetRemoteIpString, CliCommandHolder/CliCommandHolder, Log.Main/Out, World/QueueCliCommand | — | — |
| SendAndDisconnect | method | AsyncSocket.Main/GetRemoteIpString, AsyncSocket._posix/Write, Log.Main/Out, NetworkError/ToString, ReadableBuffer/ReadableBuffer#16 | — | — |
| SendAndRecvNextInput | method | AsyncSocket.Main/GetRemoteIpString, AsyncSocket._posix/Write, Log.Main/Out, NetworkError/ToString, ReadableBuffer/ReadableBuffer#16 | — | — |
