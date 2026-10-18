"""Cross-platform regression against the real standalone .NET HTTP server."""
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import subprocess
import threading
import unittest
import urllib.error
import urllib.request
from urllib.parse import urlsplit

from native_fixture import NativeHost, server
import test_playtest as playtest_tests


class NativePlaytestTests(playtest_tests.PlaytestTests):
    """Run every existing battle-session HTTP scenario through the shipping host."""
    @classmethod
    def setUpClass(cls):
        cls.native = NativeHost()
        cls.base = cls.native.base
        with urllib.request.urlopen(cls.base + '/api/bootstrap', timeout=30) as response:
            cls.token = json.load(response)['token']
        cls.scene = (server.ROOT / 'Engine/Content/SingleBattleDemoNew.xml').read_text(encoding='utf-8')
        cls.example = (server.ROOT / 'Engine/Runtime/Composition/Content/Examples/battle-content.xml').read_text(encoding='utf-8')

    @classmethod
    def tearDownClass(cls):
        cls.native.close()

    def test_lost_command_response_recovers_by_session_id_without_replaying(self):
        battle = self.start()['battle']
        destination = urlsplit(self.base)
        connection = http.client.HTTPConnection(destination.hostname, destination.port, timeout=30)
        data = {'op': 'command', 'sessionId': battle['sessionId'], 'runId': battle['runId'],
                'revision': battle['revision'], 'actor': 'player', 'action': 'Play',
                'selected': [battle['players'][0]['hand'][0]['id']]}
        try:
            connection.request('POST', '/api/playtest', json.dumps(data),
                {'Content-Type': 'application/json', 'X-Editor-Token': self.token})
            response = connection.getresponse()
            self.assertEqual(200, response.status)
            # Drop the response body as if the client disappeared after the command committed.
            response.close()
        finally:
            connection.close()
        recovered = self.state(battle)
        self.assertEqual(2, recovered['players'][0]['energy'])
        self.assertGreater(int(recovered['revision']), int(battle['revision']))
        self.assertFalse(self.action(battle, 'Play', data['selected'])['success'])
        self.assertEqual(recovered, self.state(recovered))


class NativeHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = NativeHost()
        cls.base = cls.native.base
        cls.token = cls.request('/api/bootstrap')[1]['token']
        cls.example = (server.ROOT / 'Engine/Runtime/Composition/Content/Examples/battle-content.xml').read_text(encoding='utf-8')

    @classmethod
    def tearDownClass(cls):
        cls.native.close()

    @classmethod
    def request(cls, path, body=None, headers=None):
        request = urllib.request.Request(cls.base + path,
            data=body.encode('utf-8') if body is not None else None,
            headers=headers if headers is not None else (
                {'Content-Type': 'application/xml', 'X-Editor-Token': cls.token} if body is not None else {}))
        try:
            response = urllib.request.urlopen(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            data = response.read()
            value = json.loads(data) if 'application/json' in response.headers.get('Content-Type', '') else data.decode('utf-8')
            return response.status, value, response.headers

    def test_health_identifies_workspace_and_protocol(self):
        status, health, _ = self.request('/api/health')
        self.assertEqual(200, status)
        self.assertEqual('battle-toolkit-editor', health['application'])
        self.assertEqual(1, health['protocol'])
        self.assertRegex(health['workspace'], r'^[0-9a-fA-F]{64}$')

    def test_repeated_launch_reuses_only_the_same_workspace_service(self):
        before = self.request('/api/bootstrap')[1]['token']
        result = subprocess.run([server.dotnet_runtime(), str(self.native.bridge), '--root', str(server.ROOT),
            '--port', str(urlsplit(self.base).port)], capture_output=True, encoding='utf-8', timeout=30)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn(self.base, result.stdout)
        self.assertEqual(before, self.request('/api/bootstrap')[1]['token'])

    def test_foreign_service_on_requested_port_is_rejected(self):
        class OtherService(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(b'{"application":"unrelated-service"}')

            def log_message(self, *args):
                pass

        foreign = ThreadingHTTPServer(('127.0.0.1', 0), OtherService)
        worker = threading.Thread(target=foreign.serve_forever, daemon=True)
        worker.start()
        try:
            result = subprocess.run([server.dotnet_runtime(), str(self.native.bridge), '--root', str(server.ROOT),
                '--port', str(foreign.server_port)], capture_output=True, encoding='utf-8', timeout=30)
            self.assertNotEqual(0, result.returncode)
            self.assertIn(str(foreign.server_port), result.stderr)
            with urllib.request.urlopen('http://127.0.0.1:' + str(foreign.server_port)) as response:
                self.assertEqual('unrelated-service', json.load(response)['application'])
        finally:
            foreign.shutdown()
            foreign.server_close()
            worker.join()

    def test_metadata_and_xml_validation_use_the_shared_engine(self):
        _, bootstrap, _ = self.request('/api/bootstrap')
        self.assertEqual(52, len(bootstrap['metadata']['cards']))
        self.assertEqual(10, len(bootstrap['metadata']['enums']['HandCategory']))
        status, result, _ = self.request('/api/validate', self.example)
        self.assertEqual(200, status)
        self.assertTrue(result['success'], result)
        _, invalid, _ = self.request('/api/validate', self.example.replace('statusRef="stun"', 'statusRef="missing"'))
        self.assertFalse(invalid['success'])
        self.assertTrue(any('missing' in issue['message'] and issue['line'] > 1 for issue in invalid['diagnostics']))

    def test_static_content_is_utf8_and_same_origin(self):
        for path in ['/', '/style.css', '/app.js', '/schema.js', '/model.js', '/playtest.js']:
            with self.subTest(path=path):
                status, body, headers = self.request(path)
                self.assertEqual(200, status)
                self.assertTrue(body)
                self.assertEqual('no-store', headers['Cache-Control'])
                self.assertIn("frame-ancestors 'none'", headers['Content-Security-Policy'])
                self.assertNotIn('Access-Control-Allow-Origin', headers)

    def test_token_host_and_origin_are_enforced(self):
        for headers in [
            {'Content-Type': 'application/xml'},
            {'Content-Type': 'application/xml', 'X-Editor-Token': self.token, 'Origin': 'https://other.invalid'},
            {'Content-Type': 'application/xml', 'X-Editor-Token': self.token, 'Host': 'other.invalid'},
        ]:
            self.assertEqual(403, self.request('/api/validate', self.example, headers)[0])

    def test_unknown_paths_cannot_read_workspace_files(self):
        for path in ['/server.py', '/../../.git/config', '/Engine/Content/SingleBattleDemoNew.xml', '/api/save']:
            self.assertEqual(404, self.request(path)[0])
        self.assertEqual(404, self.request('/api/execute', '<x/>')[0])

    def test_invalid_payloads_produce_diagnostics_or_transport_errors(self):
        for xml in ['<broken>', '<!DOCTYPE x [<!ENTITY a SYSTEM "file:///etc/passwd">]><BattleContent>&a;</BattleContent>']:
            status, result, _ = self.request('/api/validate', xml)
            self.assertEqual(200, status)
            self.assertFalse(result['readable'])
            self.assertFalse(result['success'])
        headers = {'Content-Type': 'text/plain', 'X-Editor-Token': self.token}
        self.assertEqual(415, self.request('/api/validate', '<x/>', headers)[0])


if __name__ == '__main__':
    unittest.main(verbosity=2)
