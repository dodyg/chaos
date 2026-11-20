"""Run after `dotnet build` to exercise stdio without an external MCP client."""
import json
import pathlib
import subprocess

assembly = pathlib.Path(__file__).parent / "bin" / "Debug" / "net11.0" / "Phase3McpStdio.dll"
with subprocess.Popen(
    ["dotnet", str(assembly)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL, text=True, encoding="utf-8"
) as server:
    def send(message):
        server.stdin.write(json.dumps(message) + "\n")
        server.stdin.flush()

    def call(message):
        send(message)
        return json.loads(server.stdout.readline())

    initialized = call({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2025-03-26", "capabilities": {},
        "clientInfo": {"name": "q0-probe", "version": "1"}}})
    assert initialized["result"]["protocolVersion"] == "2025-03-26"
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    tools = call({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    assert any(tool["name"] == "greet" for tool in tools["result"]["tools"])
    result = call({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
        "name": "greet", "arguments": {"name": "Chaos"}}})
    assert result["result"]["content"][0]["text"] == "Hello, Chaos!"
    server.stdin.close()
    assert server.wait(timeout=10) == 0
print("initialize, tools/list and tools/call passed (2025-03-26)")
