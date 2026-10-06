"""Tiny MCP-over-HTTP client for the sandbox ValheimMCP endpoint (via tunnel.sh)."""
import base64, json, sys, urllib.request
import os
URL = os.environ.get('HUBNER_URL', 'http://127.0.0.1:8731/mcp')   # 8731 = Ducky sandbox client; http://127.0.0.1:8741/mcp = dedicated-server twin (tunnel-server.sh)
def rpc(method, params=None, id=1):
    body = json.dumps({'jsonrpc': '2.0', 'id': id, 'method': method, 'params': params or {}}).encode()
    req = urllib.request.Request(URL, body, {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'})
    with urllib.request.urlopen(req, timeout=60) as r: return json.loads(r.read())
def call(name, args=None):
    return rpc('tools/call', {'name': name, 'arguments': args or {}})['result']
def save_images(result, prefix):
    n = 0
    for c in result.get('content', []):
        if c.get('type') == 'image':
            open(f'{prefix}{n}.png', 'wb').write(base64.b64decode(c['data'])); n += 1
    return n
if __name__ == '__main__':
    print(json.dumps(call(sys.argv[1], json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}), indent=1)[:1500])
