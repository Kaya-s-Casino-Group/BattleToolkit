"""Targeted latest-design bridge checks. EDITOR_URL reuses the user's single service."""
import json
import unittest
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from native_fixture import NativeHost


class Design2951HttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.host = NativeHost()
        cls.base = cls.host.base
        with urllib.request.urlopen(cls.base + '/api/bootstrap') as r:
            cls.token = json.load(r)['token']
        with urllib.request.urlopen(cls.base + '/api/standard') as r:
            cls.xml = r.read().decode('utf-8')

    @classmethod
    def tearDownClass(cls):
        cls.host.close()

    def setUp(self):
        self.open_sessions = {}

    def tearDown(self):
        for (endpoint, sid), state in list(self.open_sessions.items()):
            self.post(endpoint, op='close', sessionId=sid, runId=state['runId'], revision=state['revision'])

    def post(self, endpoint='playtest', **fields):
        request = urllib.request.Request(self.base + '/api/' + endpoint,
            json.dumps(fields).encode(), {'Content-Type': 'application/json', 'X-Editor-Token': self.token})
        with urllib.request.urlopen(request, timeout=40) as r:
            result = json.load(r)
        state = result.get('adventure' if endpoint == 'adventure' else 'battle')
        if state:
            self.open_sessions[endpoint, state['sessionId']] = state
        return result

    def start(self, xml=None):
        result = self.post(op='start', sessionId=str(uuid.uuid4()), seed='2951',
            encounterId='standard-item-lab', xml=xml or self.xml)
        self.assertTrue(result['success'], result)
        return result['battle']

    def action(self, battle, action, preview=False, **extra):
        p = battle['players'][0]
        return self.post(op='preview' if preview else 'command', sessionId=battle['sessionId'],
            runId=battle['runId'], revision=battle['revision'], actor=p['id'], action=action, **extra)

    def test_single_and_adventure_share_latest_standard(self):
        catalog = self.post('adventure', op='catalog', sessionId=str(uuid.uuid4()))
        self.assertTrue(catalog['success'], catalog)
        self.assertEqual(self.xml, catalog['battleXml'])
        self.assertEqual(self.xml, catalog['standardBattleXml'])
        root = ET.fromstring(self.xml)
        self.assertIn('2026-10-21', root.attrib['id'])
        self.assertTrue(root.find("Relics/Relic[@id='15001']/Item") is not None)

    def test_item_preview_commit_and_stale_revision(self):
        battle = self.start(); player = battle['players'][0]
        potion = next(r for r in player['relics'] if r['definitionId'] == '10002')
        self.assertTrue(potion['canUse']); self.assertEqual('Effects', potion['activation'])
        args = dict(itemInstanceId=potion['id'])
        preview = self.action(battle, 'UseItem', True, **args)
        self.assertTrue(preview['preview']['legal'], preview)
        self.assertEqual(battle, preview['battle'])
        result = self.action(battle, 'UseItem', **args)
        self.assertTrue(result['success'], result)
        after = result['battle']['players'][0]
        self.assertEqual(int(player['health']) + 10, int(after['health']))
        self.assertEqual(player['energy'], after['energy'])
        self.assertEqual(player['hand'], after['hand'])
        self.assertNotIn(potion['id'], [r['id'] for r in after['relics']])
        stale = self.action(battle, 'UseItem', **args)
        self.assertFalse(stale['success']); self.assertEqual('StaleRevision', stale['error'])
        self.assertEqual(result['battle'], stale['battle'])

    def test_dice_requires_other_held_target_and_preview_is_stable(self):
        battle = self.start(); relics = battle['players'][0]['relics']
        dice = next(r for r in relics if r['definitionId'] == '14001')
        potion = next(r for r in relics if r['definitionId'] == '10002')
        bad = self.action(battle, 'UseItem', itemInstanceId=dice['id'], targetItemInstanceId=dice['id'])
        self.assertFalse(bad['success']); self.assertEqual(battle, bad['battle'])
        args = dict(itemInstanceId=dice['id'], targetItemInstanceId=potion['id'])
        preview = self.action(battle, 'UseItem', True, **args)
        self.assertTrue(preview['preview']['legal'], preview)
        result = self.action(battle, 'UseItem', **args)
        self.assertTrue(result['success'], result)
        self.assertEqual(preview['preview']['steps'], result['steps'])
        after = result['battle']['players'][0]['relics']
        self.assertNotIn(potion['id'], [r['id'] for r in after])
        self.assertEqual('6', next(r for r in after if r['id'] == dice['id'])['cooldownBattles'])

    def test_long_vitals_are_exact_strings(self):
        root = ET.fromstring(self.xml)
        root.find('Players/Player').set('maxHealth', '9007199254740993')
        root.find("Encounters/Encounter[@id='standard-item-lab']/Player").set('initialHealth', '9007199254740001')
        battle = self.start(ET.tostring(root, encoding='unicode'))
        player = battle['players'][0]
        self.assertEqual('9007199254740993', player['maxHealth'])
        self.assertEqual('9007199254740001', player['health'])
        potion = next(r for r in player['relics'] if r['definitionId'] == '10002')
        result = self.action(battle, 'UseItem', itemInstanceId=potion['id'])
        self.assertTrue(result['success'], result)
        self.assertEqual('9007199254740011', result['battle']['players'][0]['health'])

    def test_adventure_item_endpoint_uses_same_rule_and_replay(self):
        sid = str(uuid.uuid4()); catalog = self.post('adventure', op='catalog', sessionId=sid)
        config = ET.fromstring(catalog['configXml'])
        config.set('startingEncounter', 'standard-item-lab'); config.set('relicCapacity', '32')
        result = self.post('adventure', op='start', sessionId=sid, seed='item-adventure',
            characterId=catalog['characters'][0]['id'], tarotId=catalog['tarots'][0]['id'],
            configXml=ET.tostring(config, encoding='unicode'), battleXml=self.xml)
        self.assertTrue(result['success'], result)
        state = result['adventure']
        result = self.post('adventure', op='command', sessionId=sid, runId=state['runId'],
            revision=state['revision'], action='enter', nodeId=config.attrib['startNode'])
        self.assertTrue(result['success'], result)
        state, battle = result['adventure'], result['battle']
        grenade = next(r for r in battle['players'][0]['relics'] if r['definitionId'] == '11004')
        args = dict(sessionId=sid, runId=state['runId'], revision=state['revision'],
            battleRevision=battle['revision'], actor=battle['players'][0]['id'], action='UseItem', itemInstanceId=grenade['id'])
        preview = self.post('adventure', op='preview', **args)
        self.assertTrue(preview['preview']['legal'], preview)
        used = self.post('adventure', op='battle', **args)
        self.assertTrue(used['success'], used)
        self.assertEqual(2, next(r for r in used['battle']['players'][0]['relics'] if r['id'] == grenade['id'])['charges'])
        restored = self.post('adventure', op='restore', sessionId=str(uuid.uuid4()), save=used['save'])
        self.assertTrue(restored['success'], restored)
        self.assertEqual(used['battle']['players'], restored['battle']['players'])


if __name__ == '__main__':
    unittest.main()
