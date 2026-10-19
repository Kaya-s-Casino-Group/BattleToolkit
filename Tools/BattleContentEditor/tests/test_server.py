import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request

HERE = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('editor_server', HERE / 'server.py')
server_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server_module)


class HostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.TemporaryDirectory(prefix='content-editor-test-')
        cls.compiler = server_module.Compiler(server_module.build(Path(cls.folder.name)))
        cls.server = server_module.make_server(cls.compiler, 0)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = 'http://127.0.0.1:' + str(cls.server.server_port)
        cls.token = cls.request('/api/bootstrap')[1]['token']
        cls.example = (server_module.ROOT / 'Engine/Runtime/Composition/Content/Examples/battle-content.xml').read_text(encoding='utf-8')

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown(); cls.server.server_close(); cls.thread.join(); cls.folder.cleanup()

    @classmethod
    def request(cls, path, xml=None, headers=None):
        request = urllib.request.Request(cls.base + path, data=xml.encode() if xml is not None else None,
            headers=headers if headers is not None else ({'Content-Type': 'application/xml', 'X-Editor-Token': cls.token} if xml is not None else {}))
        try:
            response = urllib.request.urlopen(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        body = response.read()
        return response.status, json.loads(body) if 'application/json' in response.headers.get('Content-Type', '') else body.decode(), response.headers

    def test_metadata_is_from_real_rules_and_cards(self):
        status, result, _ = self.request('/api/bootstrap')
        self.assertEqual(200, status)
        self.assertEqual(10, len(result['metadata']['enums']['HandCategory']))
        self.assertEqual(52, len(result['metadata']['cards']))
        self.assertEqual('battle-content.2', result['metadata']['contract'])
        self.assertIn('Cleanse', result['metadata']['enums']['CombatEffectKind'])
        self.assertIn('Negative', result['metadata']['enums']['StatusTags'])

    def test_example_http_matches_direct_compiler(self):
        status, result, _ = self.request('/api/validate', self.example)
        self.assertEqual(200, status)
        self.assertTrue(result['success'])
        self.assertEqual(self.compiler.call(self.example), result)

    def test_semantic_error_keeps_readable_source_and_exact_location(self):
        xml = self.example.replace('statusRef="stun"', 'statusRef="missing"')
        _, result, _ = self.request('/api/validate', xml)
        self.assertTrue(result['readable']); self.assertFalse(result['success'])
        self.assertTrue(any('missing' in d['message'] and d['line'] > 1 and '/Effect[' in d['path'] for d in result['diagnostics']))

    def test_unknown_fields_are_compiler_errors(self):
        _, result, _ = self.request('/api/validate', self.example.replace('id="hero"', 'id="hero" mystery="keep-me"'))
        self.assertFalse(result['success'])
        self.assertTrue(any(d['code'] == 'UnknownAttribute' and d['field'] == 'mystery' for d in result['diagnostics']))

    def test_malformed_input_and_dtd_are_not_readable(self):
        for xml in ('<broken>', '<!DOCTYPE BattleContent [<!ENTITY a SYSTEM "file:///etc/passwd">]><BattleContent schemaVersion="1" id="x" version="1">&a;</BattleContent>'):
            _, result, _ = self.request('/api/validate', xml)
            self.assertFalse(result['readable']); self.assertFalse(result['success'])
            self.assertTrue(result['diagnostics'])

    def test_authenticity_and_origin_required(self):
        for headers in ({'Content-Type': 'application/xml'},
                        {'Content-Type': 'application/xml', 'X-Editor-Token': self.token, 'Origin': 'https://other.invalid'},
                        {'Content-Type': 'application/xml', 'X-Editor-Token': self.token, 'Host': 'other.invalid'}):
            self.assertEqual(403, self.request('/api/validate', self.example, headers)[0])

    def test_size_and_content_type_bounds(self):
        self.assertEqual(415, self.request('/api/validate', '<x/>', {'X-Editor-Token': self.token, 'Content-Type': 'text/plain'})[0])
        self.assertEqual(413, self.request('/api/validate', 'x', {'X-Editor-Token': self.token, 'Content-Type': 'application/xml', 'Content-Length': str(server_module.MAX_BODY+1)})[0])

    def test_no_arbitrary_files_or_actions(self):
        for path in ('/server.py', '/../../.git/config', '/api/execute', '/api/save'):
            self.assertEqual(404, self.request(path)[0])
        self.assertEqual(404, self.request('/api/execute', '<x/>')[0])

    def test_static_files_are_same_origin_and_never_cached(self):
        for path in ('/', '/app.js', '/schema.js', '/model.js', '/style.css'):
            status, body, headers = self.request(path)
            self.assertEqual(200, status); self.assertTrue(body)
            self.assertEqual('no-store', headers['Cache-Control'])
            self.assertIn("frame-ancestors 'none'", headers['Content-Security-Policy'])
            self.assertNotIn('Access-Control-Allow-Origin', headers)

    def test_service_error_is_not_a_successful_validation(self):
        original = self.compiler.call
        try:
            self.compiler.call = lambda xml=None: (_ for _ in ()).throw(RuntimeError('test unavailable'))
            status, result, _ = self.request('/api/validate', '<x/>')
            self.assertEqual(503, status); self.assertIn('error', result)
        finally:
            self.compiler.call = original


if __name__ == '__main__':
    unittest.main(verbosity=2)
