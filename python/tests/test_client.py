"""Offline tests of the Python client against a fake MCP endpoint (no game needed): python3 -m unittest discover -s python/tests"""
import json, os, sys, threading, unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import hubner, hubner_mcp  # noqa: E402


class Fake:
    """Records tools/call requests; replies from `replies[name]` (a dict, or a callable of the arguments) or an empty JSON object."""
    def __init__(self):
        self.calls, self.replies, self.status, self.error_body = [], {}, 200, None

    def handler(self):
        fake = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a): pass

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                if fake.status != 200:
                    out = json.dumps(fake.error_body or {'ok': False, 'error': 'refused'}).encode()
                    self.send_response(fake.status); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(out))); self.end_headers(); self.wfile.write(out); return
                p = body['params']; fake.calls.append((p['name'], p['arguments'], self.headers.get('Authorization')))
                r = fake.replies.get(p['name'], {})
                r = r(p['arguments']) if callable(r) and r is not Exception else r
                if isinstance(r, str): result = {'content': [{'type': 'text', 'text': r}], 'isError': False}
                elif r is Exception: result = {'content': [{'type': 'text', 'text': 'boom'}], 'isError': True}
                else: result = {'content': [{'type': 'text', 'text': json.dumps(r)}], 'isError': False}
                out = json.dumps({'jsonrpc': '2.0', 'id': body['id'], 'result': result}).encode()
                self.send_response(200); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(out))); self.end_headers(); self.wfile.write(out)
        return H


class ClientTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.url = f'http://127.0.0.1:{self.srv.server_address[1]}/mcp'

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def test_tool_returns_parsed_json_and_raises_on_error(self):
        c = hubner.Client(self.url)
        self.fake.replies['status'] = {'role': 'client'}
        self.assertEqual(c.tool('status'), {'role': 'client'})
        self.fake.replies['status'] = Exception
        with self.assertRaisesRegex(RuntimeError, 'boom'): c.tool('status')

    def test_twin_adds_the_confirm_string_to_writes_and_sandbox_does_not(self):
        twin, sandbox = hubner.Client(self.url, server=True), hubner.Client(self.url)
        twin.zdo_delete(['1:2']); sandbox.zdo_delete(['1:2'])
        self.assertEqual(self.fake.calls[0][1].get('confirm'), 'server-write')
        self.assertNotIn('confirm', self.fake.calls[1][1])

    def test_big_plans_run_as_a_sliced_job(self):
        c = hubner.Client(self.url, server=True)
        self.fake.replies['job_start'] = {'job': 7}
        self.fake.replies['job_status'] = {'state': 'done', 'result': {'spawned': 400}}
        r = c.plan_apply('p', [{'prefab': 'x', 'x': 0, 'y': 0, 'z': 0}] * (hubner.JOB_THRESHOLD + 1))
        self.assertEqual(r, {'spawned': 400})
        self.assertEqual([n for n, _, _ in self.fake.calls], ['job_start', 'job_status'])
        self.assertEqual(self.fake.calls[0][1]['args']['confirm'], 'server-write')

    def test_small_plans_run_directly(self):
        c = hubner.Client(self.url)
        self.fake.replies['plan_apply'] = {'spawned': 1}
        c.plan_apply('p', [{'prefab': 'x', 'x': 0, 'y': 0, 'z': 0}])
        self.assertEqual([n for n, _, _ in self.fake.calls], ['plan_apply'])

    def test_token_is_sent_as_a_bearer_header(self):
        hubner.Client(self.url, token='s3cret').tool('status')
        self.assertEqual(self.fake.calls[0][2], 'Bearer s3cret')

    def test_http_refusals_surface_the_servers_reason(self):
        self.fake.status, self.fake.error_body = 403, {'ok': False, 'error': 'Host header refused (not a loopback name): evil'}
        with self.assertRaisesRegex(RuntimeError, r'HTTP 403.*Host header refused'): hubner.Client(self.url).tool('status')
        self.fake.status, self.fake.error_body = 413, {'ok': False, 'error': 'request body too large (limit 8 MB)'}
        with self.assertRaisesRegex(RuntimeError, r'HTTP 413.*too large'): hubner.Client(self.url).tool('status')

    def test_terrain_paint_refuses_values_that_could_add_arguments(self):
        c = hubner.Client(self.url)
        for bad in ('dirt level=5', 'dirt;quit', 'a b'):
            with self.assertRaises(ValueError): c.terrain_paint(0, 0, paint=bad)
        with self.assertRaises(ValueError): c.terrain_paint(0, 0, ids='Fir Pine')
        self.assertEqual(self.fake.calls, [])                                   # refused before anything was sent

    def test_terrain_paint_builds_a_fixed_key_command_and_checks_the_height_nodes(self):
        c = hubner.Client(self.url)
        self.fake.replies['terraform_map'] = {'modifiedHeightNodes': 3}
        self.fake.replies['run_command'] = 'painted'
        orig = hubner.time.sleep; hubner.time.sleep = lambda s: None
        try: out = c.terrain_paint(10, 20, paint='cultivated', circle=4, ids=['Fir', 'Pine'])
        finally: hubner.time.sleep = orig
        self.assertEqual(out, 'painted')
        cmd = [a for n, a, _ in self.fake.calls if n == 'run_command'][0]['text']
        self.assertEqual(cmd, 'terrain paint=cultivated from=10.0,20.0 circle=4 id=Fir,Pine')
        self.fake.replies['terraform_map'] = lambda a, n=[0]: {'modifiedHeightNodes': (n.__setitem__(0, n[0] + 1) or n[0])}      # 1 then 2: the height changed
        hubner.time.sleep = lambda s: None
        try:
            with self.assertRaisesRegex(RuntimeError, 'HEIGHT changed'): c.terrain_paint(10, 20)
        finally: hubner.time.sleep = orig

    def test_module_does_not_shadow_the_mcp_sdk(self):
        self.assertFalse(os.path.exists(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'mcp.py')))
        self.assertTrue(hasattr(hubner_mcp, 'call'))


if __name__ == '__main__':
    unittest.main()
