# MCP tools through the official C# SDK

`McpToolSet` connects an explicitly configured set of MCP servers and adapts their
tools to the shared `IAgentTool` contract. Each agent run owns fresh clients through
`IAgentToolSet` and disposes them when that run ends. The desktop and stdio Host use
the same kernel integration. No server is inferred from a project, account login,
installed application, or model response. An absent configuration file disables MCP.

## Reused components and provenance

The runtime consumes **ModelContextProtocol.Core 2.3.0**, the official C# SDK,
under Apache-2.0. Its NuGet repository commit is
`9a327829ef066c44a0d737c58bf3c7617d7c701c`. Sources:

- [SDK release](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.3.0)
- [Pinned SDK source](https://github.com/modelcontextprotocol/csharp-sdk/tree/9a327829ef066c44a0d737c58bf3c7617d7c701c)
- [Upstream license](https://github.com/modelcontextprotocol/csharp-sdk/blob/9a327829ef066c44a0d737c58bf3c7617d7c701c/LICENSE)
- [SDK transport documentation](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html)
- [MCP tools specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)

No SDK implementation was copied or ported. The SDK implements JSON-RPC,
initialization/version negotiation, stdio framing, Streamable HTTP/SSE framing,
request cancellation, HTTP session ownership and child-process shutdown. The BCL
provides `SocketsHttpHandler`, `HttpClient`, `System.Text.Json`, environment access,
SHA-256 and cancellation. There is no second production HTTP/SSE implementation,
protocol parser, process scheduler or storage layer.

Cogitide adds only the local configuration contract, credential forwarding policy,
bounded discovery, provider-compatible names, approval flag and bounded projection
from MCP content into the existing text-based `ToolResult`. The tiny stdio and HTTP
peers in `McpIntegrationTests` are independent test fixtures, not runtime transports.

## Explicit local configuration

The Host reads `mcp.json` in its application data directory. Keep this local file
outside Git. Configure a `servers` array; `servers: []` explicitly disables all
servers. The supported transports are `stdio` and `http` (Streamable HTTP).

```json
{
  "servers": [
    {
      "id": "local",
      "transport": "stdio",
      "command": "my-mcp-server",
      "arguments": ["--stdio"],
      "environmentVariables": ["MCP_SERVER_API_KEY"],
      "connectionTimeoutSeconds": 15,
      "callTimeoutSeconds": 60
    },
    {
      "id": "remote",
      "transport": "http",
      "url": "https://mcp.example.com/mcp",
      "headerEnvironmentVariables": {
        "Authorization": "MCP_SERVER_AUTHORIZATION"
      }
    }
  ]
}
```

`environmentVariables` contains **names**, not values. Each listed variable is
read from the Host's environment and forwarded with the same name. Each
`headerEnvironmentVariables` value is also an environment variable **name**; for
example, set `MCP_SERVER_AUTHORIZATION` to the full `Bearer ...` header value in
system credential/environment management. Never put credentials in the JSON,
command arguments or endpoint URL. A missing referenced variable fails connection.

All entries are validated before any server starts. Unknown JSON fields, duplicate
JSON properties, duplicate server IDs and mixed transport settings are rejected.
IDs contain 1–16 ASCII letters, digits, underscores or hyphens. HTTP endpoints
require HTTPS, except loopback HTTP for local servers; user-info, query strings and
fragments are rejected. Redirects are disabled so configured credentials cannot be
forwarded to another endpoint. Protocol-owned headers cannot be overridden.

For stdio, the SDK starts the process without a visible window and with
`InheritEnvironmentVariables = false`. Its maintained operating-system environment
allowlist supplies basic startup variables. Only explicitly named extra variables
are added. Provider credential variable names supplied by the run, plus common
OpenAI/Anthropic/DeepSeek credential names, are excluded and cannot be explicitly
forwarded. Use separate credentials intended for the MCP server. Command working
directory is the run workspace. Configured executables and endpoints remain user
trusted integrations: application permissions do not provide an OS sandbox.

## Approval, ownership and unknown outcomes

Every discovered MCP tool has `RequiresApproval = true`, including tools whose
server advertises `readOnlyHint`. Server annotations never grant permission.
Cogitide's existing execution mode and approval policy decide whether a call may
proceed. Read-only runs do not establish MCP connections. Connecting a configured
stdio server itself starts its executable, so configuration is a local opt-in to
that executable. Sampling, elicitation and client-side tools are not advertised.
Server instructions are not inserted into the agent's system instructions.

Names use the server ID, a readable part of the remote name and a SHA-256 suffix.
They fit the provider tool-name limit and distinguish names such as `a.b` and
`a_b`, including across servers. The original remote name is retained for calls.
Definitions stay fixed for one run; reconnecting on the next run refreshes them.

The run owns every client and HTTP transport, including partially connected
sets. Cancellation and timeout close the set; SDK disposal terminates owned stdio
process trees and closes owned HTTP sessions. Calls are never retried or replayed.
A two-second cleanup deadline disposes owned HTTP clients if a session-deletion
request stalls; remote session deletion is then unconfirmed.
A cancelled or timed-out call may already have changed the remote system: timeout
returns an error with **unknown remote outcome**, and caller cancellation remains
cancellation. Transport failure also reports unknown outcome. Neither is recorded
as a successful remote action. An MCP `isError` result remains an error with its
bounded content and structured result available to the model.

## Limits and verification

Configuration is limited to 64 KiB and eight servers. Connection/discovery has a
15-second default deadline per server (configurable from 1 to 30 seconds). Calls
default to 60 seconds (1–120 seconds). Discovery is limited to eight pages per
server, 64 tools across the set, 32,768 characters per input schema and 262,144
characters across tool definitions. Repeated cursors and duplicate remote tool
names fail the connection instead of silently aliasing tools.
The 64-tool cap reserves room for Cogitide's built-in tools; it is a local limit,
not a compatibility guarantee for every provider's tool limit.

Arguments are JSON objects with at most 32,768 characters and depth 32. The result
projection returns valid JSON with at most 32,768 characters, up to 32 content
blocks, at most 8,192 characters per text block, and structured JSON up to 8,192
characters. Oversized content is explicitly marked as truncated or omitted. Image
and audio results expose MIME type and encoded length without copying base64 into
model context; embedded binary resources are also omitted. The original server
result is parsed by the SDK before projection: these are context/discovery bounds,
not an OS memory boundary against a malicious server or a cap on SDK wire frames.

`McpIntegrationTests` covers real local stdio and Streamable HTTP round trips,
structured errors, image omission, always-on approval, namespace collisions,
ambient credential isolation, explicitly excluded provider variables, unsafe
configuration, discovery/result limits, call timeout, cancellation, idle run
cancellation, process exit, HTTP redirects and session deletion. Tests make no
model calls and need Python on `PATH` for the tiny stdio peer. Functional fixture
coverage does not establish interoperability with every external MCP server or
measured improvements in coding performance.

`python scripts/verify-integrations.py` adds an end-to-end deterministic acceptance
fixture after the Host is built and Playwright Chromium is installed. It reuses
the repository's existing stdio Host client and process-identity helpers. A local
SSE provider first requests an MCP action that the fixture explicitly denies,
then a new run explicitly approves one Unicode echo and a headless browser visit
to a loopback page. Roslyn definition/reference calls verify the actual overload
and UTF-16 source positions. Cold Host restarts verify persisted sessions/events;
a read-only resumed run verifies no MCP reconnection or action replay. The fixture
observes MCP process exits before any fallback cleanup. Screenshots are enabled by
default; `--no-screenshot` disables only PNG capture. Result manifests, assembly
SHA-256 hashes and generated evidence are written under the Git-ignored
`artifacts/verification/integrations/<run-id>/` directory. The script performs no
SDK commands, external model calls or desktop launch; a passing manifest records
only that particular run's observed fixture results.
