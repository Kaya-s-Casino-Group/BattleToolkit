"""New elite projection checks through the real engine and local HTTP host."""
import json
import unittest
import urllib.request
import uuid
import xml.etree.ElementTree as ET
from native_fixture import NativeHost


class EliteProjectionHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.host = NativeHost()
        cls.base = cls.host.base
        with urllib.request.urlopen(cls.base + '/api/bootstrap') as response:
            cls.token = json.load(response)['token']
        with urllib.request.urlopen(cls.base + '/api/standard') as response:
            cls.xml = response.read().decode()

    @classmethod
    def tearDownClass(cls):
        cls.host.close()

    def post(self, **fields):
        request = urllib.request.Request(self.base + '/api/playtest', json.dumps(fields).encode(),
            {'Content-Type': 'application/json', 'X-Editor-Token': self.token})
        with urllib.request.urlopen(request, timeout=40) as response:
            result = json.load(response)
        if result.get('battle'):
            self.battle = result['battle']
        return result

    def setUp(self):
        self.battle = None
        result = self.post(op='start', sessionId=str(uuid.uuid4()), seed='elite-projection',
            encounterId='standard-armored-summoner', xml=self.xml)
        self.assertTrue(result['success'], result)

    def tearDown(self):
        if self.battle:
            self.post(op='close', sessionId=self.battle['sessionId'],
                runId=self.battle['runId'], revision=self.battle['revision'])

    def action(self, action, **extra):
        battle = self.battle
        return self.post(op='command', sessionId=battle['sessionId'], runId=battle['runId'],
            revision=battle['revision'], actor=battle['players'][0]['id'], action=action, **extra)

    def end_round(self):
        result = self.action('EndTurn')
        self.assertTrue(result['success'], result)
        if self.battle['players'][0]['window'] == 'Ending':
            result = self.action('FinishTurn')
            self.assertTrue(result['success'], result)
        return result

    def test_cast_status_and_damage_contribution_are_authoritative_and_visible(self):
        enemy = self.battle['enemies'][0]
        self.assertEqual(1, len(self.battle['enemies']))
        self.assertEqual('400', enemy['health'])
        self.assertEqual([], enemy['statuses'])
        self.assertEqual('4', enemy['intentMeta']['damage'])
        self.assertIn('+3层', enemy['intentMeta']['statusSummary'])
        self.assertIn('减1层', enemy['intentMeta']['statusSummary'])
        self.assertEqual('伤害后流血 +3层；行动结束后流血减1层', enemy['intentMeta']['statusSummary'])
        self.assertIn('8', enemy['intentMeta']['growthSummary'])
        self.assertIn('+10', enemy['intentMeta']['growthSummary'])

        result = self.end_round()
        bleeding = next(status for status in self.battle['players'][0]['statuses']
            if status['definitionId'] == 'elite-bleeding')
        self.assertEqual(2, bleeding['stacks'])
        self.assertIn('敌人对玩家攻击', bleeding['description'])
        self.assertIn('50%', bleeding['description'])
        self.assertIn('实际行动结束', bleeding['description'])
        intent = self.battle['enemies'][0]['intentMeta']
        self.assertEqual('36', intent['damage'])
        self.assertIn('18→36', ' '.join(intent['damageDetails']).replace(' ', ''))
        self.assertIn('+18', ' '.join(intent['damageDetails']).replace(' ', ''))
        self.assertNotIn('护甲', ' '.join(self.battle['enemies'][0]['intent']))
        health = int(self.battle['players'][0]['health'])
        result = self.end_round()
        self.assertEqual(36, health - int(self.battle['players'][0]['health']))
        lines = [line for step in result['steps'] for line in step['lines']]
        self.assertTrue(any('流血增伤' in line and '18→36' in line.replace(' ', '') for line in lines), lines)
        self.assertEqual(1, self.battle['players'][0]['statuses'][0]['stacks'])

    def test_bleeding_audit_uses_its_stage_and_still_settles_as_one_shielded_hit(self):
        root = ET.fromstring(self.xml)
        root.find("Players/Player[@id='hero']").set('incomingMultiplier', '0.5')
        previous = self.battle
        result = self.post(op='start', sessionId=previous['sessionId'], runId=previous['runId'],
            encounterId='standard-armored-summoner', seed='elite-shield', xml=ET.tostring(root, encoding='unicode'))
        self.assertTrue(result['success'], result)
        self.end_round()
        battle = self.battle
        result = self.post(op='admin', sessionId=battle['sessionId'], runId=battle['runId'], revision=battle['revision'],
            patch={'vitals': [{'unit': battle['players'][0]['id'], 'block': '10'}]})
        self.assertTrue(result['success'], result)
        intent = self.battle['enemies'][0]['intentMeta']
        self.assertEqual('18', intent['damage'])
        # Base attack 18 first becomes 9 due to another modifier; bleeding adds 9, not overall 18→18.
        detail = ' '.join(intent['damageDetails']).replace(' ', '')
        self.assertIn('9→18', detail)
        self.assertIn('+9', detail)
        health = int(self.battle['players'][0]['health'])
        result = self.end_round()
        self.assertEqual(8, health - int(self.battle['players'][0]['health']))
        self.assertEqual('0', self.battle['players'][0]['block'])
        hits = [line for step in result['steps'] for line in step['lines'] if '伤害 ' in line]
        self.assertEqual(1, len(hits), hits)
        self.assertIn('扣血 8 · 护盾吸收 10', hits[0])

    def test_unpreviewable_attack_growth_does_not_break_snapshot(self):
        root = ET.fromstring(self.xml)
        behavior = root.find("Enemies/Enemy[@id='summoner']/Behavior")
        behavior.set('attack', '9223372036854775807')
        behavior.set('attackPerRound', '1')
        behavior.find("Stage/Action[@id='cast']/Effects/Effect[@kind='Damage']").set('amount', '0')
        previous = self.battle
        result = self.post(op='start', sessionId=previous['sessionId'], runId=previous['runId'],
            encounterId='standard-armored-summoner', seed='elite-overflow', xml=ET.tostring(root, encoding='unicode'))
        self.assertTrue(result['success'], result)
        self.end_round()
        intent = self.battle['enemies'][0]['intentMeta']
        self.assertFalse(intent['available'])
        self.assertEqual('', intent['growthSummary'])
        self.assertIn('意图暂不可预览', ' '.join(self.battle['enemies'][0]['intent']))

    def test_configured_turn_start_decay_keeps_its_actual_intent_timing(self):
        root = ET.fromstring(self.xml)
        decay = root.find("Statuses/Status[@id='elite-bleeding']/StackDecay")
        decay.set('timing', 'TurnStarted')
        previous = self.battle
        result = self.post(op='start', sessionId=previous['sessionId'], runId=previous['runId'],
            encounterId='standard-armored-summoner', seed='elite-turn-start', xml=ET.tostring(root, encoding='unicode'))
        self.assertTrue(result['success'], result)
        self.end_round()
        self.assertEqual(3, self.battle['players'][0]['statuses'][0]['stacks'])
        summary = self.battle['enemies'][0]['intentMeta']['statusSummary']
        self.assertEqual('回合开始流血减1层', summary)

    def test_growth_overflow_is_safe_even_when_action_has_no_damage(self):
        root = ET.fromstring(self.xml)
        behavior = root.find("Enemies/Enemy[@id='summoner']/Behavior")
        behavior.set('attack', '9223372036854775807')
        behavior.set('attackPerRound', '1')
        for effect in behavior.findall("Stage/Action/Effects/Effect[@kind='Damage']"):
            effect.set('kind', 'GainBlock')
            effect.set('amount', '0')
            effect.attrib.pop('amountKind', None)
        previous = self.battle
        result = self.post(op='start', sessionId=previous['sessionId'], runId=previous['runId'],
            encounterId='standard-armored-summoner', seed='elite-nondamage-overflow', xml=ET.tostring(root, encoding='unicode'))
        self.assertTrue(result['success'], result)
        self.end_round()
        intent = self.battle['enemies'][0]['intentMeta']
        self.assertTrue(intent['available'])
        self.assertEqual('', intent['growthSummary'])


if __name__ == '__main__':
    unittest.main()
