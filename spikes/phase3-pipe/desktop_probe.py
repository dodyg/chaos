"""Probe the unpackaged Release desktop --mcp entry point without an account."""
import json
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parents[2]
exe = root / "src/Chaos.Desktop/bin/Release/net11.0-windows10.0.19041.0/Chaos.Desktop.exe"
with tempfile.TemporaryDirectory(prefix="chaos-mcp-") as data_root:
    process = subprocess.Popen([str(exe), "--mcp", "--data-dir", data_root], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    try:
        def send(message):
            process.stdin.write(json.dumps(message) + "\n")
            process.stdin.flush()

        send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "q0-probe", "version": "1"}}})
        assert json.loads(process.stdout.readline())["result"]["protocolVersion"] == "2025-03-26"
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        assert json.loads(process.stdout.readline())["result"]["tools"] == []
        process.stdin.close()
        assert process.wait(timeout=10) == 0
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
print("desktop --mcp initialized, listed zero tools and exited cleanly")
