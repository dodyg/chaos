# Per-user named-pipe relay experiment

Run `dotnet build spikes/phase3-pipe/Phase3Pipe.csproj -c Release`, then pipe one line to
`dotnet spikes/phase3-pipe/bin/Release/net11.0-windows/Phase3Pipe.dll`. It starts a separate
windowless host if one is not listening, relays the line through a `CurrentUserOnly` named
pipe and prints the response. This is not the desktop executable, MCP transport or a token
authentication implementation. The cross-user access check and independent observation of the
host window state remain open.

The desktop implementation can also be checked after a Release build with
`python spikes/phase3-pipe/desktop_probe.py`. It exercises the actual desktop executable's
stdio shim and its windowless one-session fallback, with an empty tool list.
