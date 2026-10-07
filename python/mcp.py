"""Tiny MCP-over-HTTP client for the ValheimMCP endpoint. URL is the module default (HUBNER_URL); call(url=...) overrides it per call."""
import base64, json, os, sys, urllib.request
URL = os.environ.get('HUBNER_URL', 'http://127.0.0.1:8731/mcp')   # 8731 = sandbox client; the dedicated-server twin is usually tunnelled to 8741


def rpc(method, params=None, id=1, url=None, token=None, timeout=60):
    body = json.dumps({'jsonrpc': '2.0', 'id': id, 'method': method, 'params': params or {}}).encode()
    hdr = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
    tok = token or os.environ.get('HUBNER_TOKEN')
    if tok: hdr['Authorization'] = 'Bearer ' + tok                     # server.token in valheimmcp.yml (optional)
    req = urllib.request.Request(url or URL, body, hdr)
    with urllib.request.urlopen(req, timeout=timeout) as r: return json.loads(r.read())


def call(name, args=None, url=None, token=None, timeout=60):
    r = rpc('tools/call', {'name': name, 'arguments': args or {}}, url=url, token=token, timeout=timeout)
    if 'error' in r: raise RuntimeError(f"{name}: {r['error'].get('message')}")
    return r['result']


def save_images(result, prefix):
    n = 0
    for c in result.get('content', []):
        if c.get('type') == 'image':
            open(f'{prefix}{n}.png', 'wb').write(base64.b64decode(c['data'])); n += 1
    return n


if __name__ == '__main__':
    print(json.dumps(call(sys.argv[1], json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}), indent=1)[:1500])
