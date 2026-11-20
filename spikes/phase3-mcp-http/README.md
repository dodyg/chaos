# HTTP MCP and OpenIddict spike

This local-only experiment uses an in-memory, preconfigured public client, OpenIddict degraded
mode, an ephemeral signing key, and an automatically approved test identity. It is not suitable
for deployment: there is no consent screen, persistent registration, TLS, real user identity,
revocation or rate limit.

From the repository root, run the following in one terminal:

```powershell
$env:ASPNETCORE_URLS = 'http://127.0.0.1:55441'
dotnet run --project spikes/phase3-mcp-http/Phase3McpHttp.csproj -c Release
```

In a second terminal run `python spikes/phase3-mcp-http/probe.py`. It checks an unauthenticated
refusal, authorization code and PKCE, token exchange, MCP initialization and a tool call.
Stop the server afterward. The callback URI is a fixed identifier for the test; the probe reads
the redirect without starting a callback listener.
