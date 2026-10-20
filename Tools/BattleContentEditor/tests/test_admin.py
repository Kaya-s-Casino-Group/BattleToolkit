"""Explicit administrator entry points: authority, atomic preview, precision and save replay."""
import json
import unittest
import urllib.error
import urllib.request
import uuid

from native_fixture import NativeHost


class AdminHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.host = NativeHost()
        cls.addClassCleanup(cls.host.close)
        cls.base = cls.host.base
        with urllib.request.urlopen(cls.base + '/api/bootstrap') as response:
            cls.token = json.load(response)['token']
        with urllib.request.urlopen(cls.base + '/api/standard') as response:
            cls.xml = response.read().decode('utf-8')

    @classmethod
    def tearDownClass(cls):
        cls.host.close()

    def request(self, endpoint, data, token=True):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['X-Editor-Token'] = self.token
        request = urllib.request.Request(self.base + '/api/' + endpoint, json.dumps(data).encode(), headers)
        try:
            response = urllib.request.urlopen(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, json.load(response)

    def ok(self, endpoint, data):
        status, result = self.request(endpoint, data)
        self.assertEqual(200, status, result)
        self.assertTrue(result['success'], result)
        return result

    def start(self):
        return self.ok('playtest', {'op': 'start', 'sessionId': str(uuid.uuid4()), 'seed': 'admin-http',
                                  'xml': self.xml, 'encounterId': 'standard-single-battle'})

    def battle_admin(self, result, op, patch=None):
        b = result['battle']
        request = {'sessionId': b['sessionId'], 'runId': b['runId'], 'revision': b['revision'], 'op': op}
        if patch is not None:
            request['patch'] = patch
        return self.request('playtest', request)[1]

    def test_explicit_admin_reads_order_while_ordinary_state_hides_it(self):
        result = self.start()
        ordinary = result['battle']['players'][0]
        self.assertTrue(all('id' not in card for card in ordinary['piles']['deck']['cards']))
        admin = self.battle_admin(result, 'admin-state')['admin']
        self.assertEqual('bottom-to-top', admin['deckOrder'])
        player = admin['players'][0]
        self.assertEqual(ordinary['deckCount'], len(next(z for z in player['zones'] if z['kind'] == 'deck')['cards']))
        self.assertTrue(all(c['instanceId'] for z in player['zones'] for c in z['cards']))
        self.assertEqual(52, len(admin['catalog']['cards']))
        self.assertGreaterEqual(len(admin['catalog']['relics']), 12)
        status, _ = self.request('playtest', {'op': 'admin-state'}, token=False)
        self.assertEqual(403, status)

    def test_preview_and_commit_are_atomic_exact_and_do_not_cost_actions(self):
        result = self.start()
        player = result['battle']['players'][0]
        patch = {'vitals': [{'unit': player['id'], 'health': '4000000000', 'maxHealth': '5000000000', 'block': '9007199254740993'}],
                 'gold': [{'owner': player['id'], 'amount': '9007199254740993'}],
                 'zones': [{'owner': player['id'], 'kind': 'hand', 'cards': [{'definitionId': 'standard:1:14', 'effects': 'Unplayable'}]}],
                 'relics': [{'owner': player['id'], 'items': [{'definitionId': '11004', 'charges': 2}]}]}
        preview = self.battle_admin(result, 'admin-preview', patch)
        self.assertTrue(preview['preview']['legal'], preview)
        self.assertEqual(result['battle'], preview['battle'])
        self.assertEqual('9007199254740993', preview['admin']['players'][0]['gold'])
        committed = self.battle_admin(result, 'admin', patch)
        self.assertTrue(committed['success'], committed)
        self.assertEqual(preview['admin'], committed['admin'])
        p = committed['battle']['players'][0]
        self.assertEqual(player['energy'], p['energy'])
        self.assertEqual(player['actionsRemaining'], p['actionsRemaining'])
        self.assertEqual('9007199254740993', p['block'])
        self.assertEqual('9007199254740993', p['gold'])
        self.assertEqual(1, len(p['hand']))
        self.assertFalse(p['hand'][0]['canPlay'])
        stale = self.battle_admin(result, 'admin', patch)
        self.assertEqual('StaleRevision', stale['error'])
        invalid = {'gold': [{'owner': player['id'], 'amount': '42'}],
                   'relics': [{'owner': player['id'], 'items': [{'definitionId': 'missing'}]}]}
        rejected = self.battle_admin(committed, 'admin', invalid)
        self.assertFalse(rejected['success'])
        self.assertEqual(committed['battle'], rejected['battle'])
        # Numeric JSON may already have been rounded by a browser; require decimal strings for 64-bit edits.
        bad_number = self.battle_admin(committed, 'admin', {'gold': [{'owner': player['id'], 'amount': 9007199254740993}]})
        self.assertFalse(bad_number['success'])

    def test_ordinary_command_cannot_request_admin_action(self):
        result = self.start()
        b = result['battle']
        _, rejected = self.request('playtest', {'op': 'command', 'sessionId': b['sessionId'], 'runId': b['runId'],
                                              'revision': b['revision'], 'actor': b['players'][0]['id'], 'action': 'AdminEdit'})
        self.assertFalse(rejected['success'])
        self.assertEqual('InvalidAction', rejected['error'])
        self.assertEqual(b, rejected['battle'])

    def test_adventure_admin_persists_gold_into_battle_and_save_replay(self):
        session = str(uuid.uuid4())
        catalog = self.ok('adventure', {'op': 'catalog', 'sessionId': session})
        result = self.ok('adventure', {'op': 'start', 'sessionId': session, 'seed': 'admin-adventure',
                                      'characterId': catalog['characters'][0]['id'], 'tarotId': catalog['tarots'][0]['id']})
        a = result['adventure']
        state = self.ok('adventure', {'op': 'admin-state', 'sessionId': session, 'runId': a['runId'], 'revision': a['revision']})
        player = state['admin']['players'][0]['id']
        patch = {'gold': [{'owner': player, 'amount': '9007199254740993'}],
                 'vitals': [{'unit': player, 'health': '321', 'maxHealth': '4000000000'}],
                 'relics': [{'owner': player, 'items': [{'definitionId': '10002'}]}]}
        result = self.ok('adventure', {'op': 'admin', 'sessionId': session, 'runId': a['runId'], 'revision': a['revision'], 'patch': patch})
        a = result['adventure']
        node = next(n['id'] for n in a['nodes'] if n['state'] == 'available')
        result = self.ok('adventure', {'op': 'command', 'sessionId': session, 'runId': a['runId'], 'revision': a['revision'], 'action': 'enter', 'nodeId': node})
        self.assertEqual('9007199254740993', result['battle']['players'][0]['gold'])
        self.assertEqual('4000000000', result['battle']['players'][0]['maxHealth'])
        restored = self.ok('adventure', {'op': 'restore', 'sessionId': str(uuid.uuid4()), 'save': result['save']})
        self.assertEqual(result['save'], restored['save'])
        self.assertEqual(result['adventure']['player'], restored['adventure']['player'])


if __name__ == '__main__':
    unittest.main()
