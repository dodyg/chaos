"""Exercise the disposable server started on http://127.0.0.1:55441."""
import base64
import hashlib
import json
import urllib.error
import urllib.parse
import urllib.request

root = "http://127.0.0.1:55441"
verifier = "phase3-spike-verifier-0123456789-abcdefghijklmnopqrstuvwxyz"
challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip("=")

try:
    urllib.request.urlopen(urllib.request.Request(root + "/mcp", data=b"{}", headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream"}), timeout=10)
    raise AssertionError("MCP accepted an unauthenticated request")
except urllib.error.HTTPError as response:
    assert response.code == 401, response.code

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

params = urllib.parse.urlencode({"client_id": "phase3-probe", "redirect_uri": "http://127.0.0.1:55442/callback",
    "response_type": "code", "code_challenge": challenge, "code_challenge_method": "S256"})
try:
    urllib.request.build_opener(NoRedirect).open(root + "/authorize?" + params)
    raise AssertionError("authorization did not redirect")
except urllib.error.HTTPError as response:
    assert response.code == 302, (response.code, response.read())
    code = urllib.parse.parse_qs(urllib.parse.urlparse(response.headers["Location"]).query)["code"][0]

data = urllib.parse.urlencode({"grant_type": "authorization_code", "client_id": "phase3-probe", "code": code,
    "redirect_uri": "http://127.0.0.1:55442/callback", "code_verifier": verifier}).encode()
with urllib.request.urlopen(urllib.request.Request(root + "/token", data=data), timeout=10) as response:
    token = json.load(response)["access_token"]

def mcp(payload, session=None):
    headers = {"Authorization": "Bearer " + token, "Content-Type": "application/json",
               "Accept": "application/json, text/event-stream"}
    if session:
        headers["Mcp-Session-Id"] = session
        headers["MCP-Protocol-Version"] = "2025-03-26"
    request = urllib.request.Request(root + "/mcp", data=json.dumps(payload).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=10) as response:
        body = response.read().decode()
        session = response.headers.get("Mcp-Session-Id", session)
    if body.startswith("event:"):
        body = next(line[6:] for line in body.splitlines() if line.startswith("data: "))
    return json.loads(body), session

result, session = mcp({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
    "protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "q0-probe", "version": "1"}}})
assert result["result"]["protocolVersion"] == "2025-03-26"
result, _ = mcp({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {
    "name": "greet", "arguments": {"name": "Chaos"}}}, session)
assert result["result"]["content"][0]["text"] == "Hello, Chaos!"
print("unauthenticated refusal, PKCE authorization, token, MCP initialize and tool call passed")
