"""Wrath display and target contracts through the real engine and local HTTP host."""
import json
import unittest
import urllib.request
import uuid
import xml.etree.ElementTree as ET

from native_fixture import NativeHost


class WrathProjectionHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.host = NativeHost()
        cls.addClassCleanup(cls.host.close)
        cls.base = cls.host.base
        with urllib.request.urlopen(cls.base + '/api/bootstrap') as response:
            cls.token = json.load(response)['token']
        with urllib.request.urlopen(cls.base + '/api/standard') as response:
            cls.xml = response.read().decode()

    def post(self, **fields):
        request = urllib.request.Request(self.base + '/api/playtest', json.dumps(fields).encode(),
            {'Content-Type': 'application/json', 'X-Editor-Token': self.token})
        with urllib.request.urlopen(request, timeout=40) as response:
            result = json.load(response)
        if result.get('battle'):
            self.battle = result['battle']
        self.assertTrue(result['success'], result)
        return result

    def start(self, encounter='standard-wrath-thousand-hands'):
        # A permanent control immunity keeps player windows available while observing
        # warnings; all Boss timing, targeting and damage still use the real rules.
        root = ET.fromstring(self.xml)
        root.find("Players/Player[@id='hero']").set('maxHealth', '10000')
        immunity = root.find("Statuses/Status[@id='control-immunity']")
        immunity.set('lifetime', 'UntilRemoved')
        immunity.attrib.pop('phase', None)
        high_card = root.find("HandRules/HandRule/Hand[@category='HighCard']")
        ET.SubElement(high_card, 'Effect', {'id': 'test-immunity', 'kind': 'ApplyStatus',
            'statusRef': 'control-immunity', 'target': 'Self'})
        return self.post(op='start', sessionId=str(uuid.uuid4()), seed='wrath-projection',
            encounterId=encounter, xml=ET.tostring(root, encoding='unicode'))

    def setUp(self):
        self.battle = None
        self.start()

    def tearDown(self):
        if self.battle:
            self.post(op='close', sessionId=self.battle['sessionId'],
                runId=self.battle['runId'], revision=self.battle['revision'])

    def action(self, action, op='command', **extra):
        battle = self.battle
        return self.post(op=op, sessionId=battle['sessionId'], runId=battle['runId'],
            revision=battle['revision'], actor=battle['players'][0]['id'], action=action, **extra)

    def end_round(self):
        result = self.action('EndTurn')
        if self.battle['players'][0]['window'] == 'Ending':
            result = self.action('FinishTurn')
        return result

    def body(self):
        return next(enemy for enemy in self.battle['enemies'] if enemy['definitionId'] == 'wrath')

    def play_one_at(self, target, op='command'):
        selected = [self.battle['players'][0]['hand'][0]['id']]
        preview = self.action('Play', op='preview', selected=selected)['preview']
        self.assertTrue(preview['legal'], preview)
        slot = next(slot for slot in preview['targets'] if any(c['id'] == target for c in slot['candidates']))
        return self.action('Play', op=op, selected=selected, targetBindings={slot['slot']: [target]})

    def test_six_units_have_separate_boss_state_and_eight_regular_intents(self):
        self.assertEqual(6, len(self.battle['enemies']))
        body = self.body()
        self.assertIn('wrath', body, 'snapshot must expose the shared authoritative Wrath query')
        self.assertEqual('Body', body['wrath']['role'])
        self.assertEqual('33', body['wrath']['attack'])
        self.assertEqual('99', body['wrath'].get('ultimateBaseDamage'))
        self.assertEqual('99', body['wrath'].get('pendingUltimateBaseDamage'))
        self.assertEqual(0, body['wrath']['charges'])
        self.assertFalse(body['wrath']['warningActive'])
        self.assertEqual(8, len(body['intentSchedule']))
        self.assertEqual(['strike', 'strike-2', 'guard', 'charge'] * 2,
            [intent['actionId'] for intent in body['intentSchedule']])
        roles = [enemy['wrath']['role'] for enemy in self.battle['enemies'] if enemy['id'] != body['id']]
        self.assertEqual(4, roles.count('Blood'))
        self.assertEqual(1, roles.count('Binding'))

    def test_broken_hand_keeps_identity_and_countdown_but_leaves_target_candidates(self):
        body = self.body()
        hand = next(enemy for enemy in self.battle['enemies'] if enemy['id'] != body['id'])
        self.play_one_at(hand['id'])
        broken = next(enemy for enemy in self.battle['enemies'] if enemy['id'] == hand['id'])
        self.assertEqual(6, len(self.battle['enemies']))
        self.assertFalse(broken['alive'])
        self.assertEqual(9, broken['wrath']['reviveRound'])
        self.assertEqual(8, broken['wrath']['reviveRemaining'])
        self.assertEqual('28', self.body()['wrath']['attack'])
        before = self.battle
        selected = [before['players'][0]['hand'][0]['id']]
        for _ in range(3):
            preview = self.action('Play', op='preview', selected=selected)
            self.assertEqual(before, preview['battle'], 'preview must not advance any Boss clock')
            candidates = {candidate['id'] for slot in preview['preview']['targets'] for candidate in slot['candidates']}
            self.assertNotIn(hand['id'], candidates)
            self.assertIn(body['id'], candidates)
        self.end_round()
        broken = next(enemy for enemy in self.battle['enemies'] if enemy['id'] == hand['id'])
        self.assertEqual(7, broken['wrath']['reviveRemaining'])

    def test_warning_cancellation_updates_attack_without_replacing_regular_intent(self):
        self.play_one_at(self.body()['id'])
        for _ in range(5):
            self.end_round()
        body = self.body()
        self.assertEqual(6, self.battle['round'])
        self.assertTrue(body['wrath']['warningActive'])
        self.assertEqual(6, body['wrath']['releaseRound'])
        self.assertEqual(1, body['wrath']['charges'])
        self.assertEqual('43', body['wrath']['attack'])
        self.assertEqual('129', body['wrath'].get('ultimateBaseDamage'))
        self.assertEqual('129', body['wrath'].get('pendingUltimateBaseDamage'))
        self.assertEqual('strike-2', body['intentMeta']['actionId'])
        self.assertEqual('43', body['intentMeta']['damage'], 'the regular attack icon must exclude the extra 129-damage ultimate')
        self.assertEqual('', body['intentMeta']['growthSummary'], 'Boss must not display ordinary fixed base attack as its effective D')
        hand = next(enemy for enemy in self.battle['enemies'] if enemy['id'] != body['id'])
        self.play_one_at(hand['id'])
        body = self.body()
        self.assertFalse(body['wrath']['warningActive'])
        self.assertIsNone(body['wrath']['releaseRound'])
        self.assertEqual(5, body['wrath']['cooldownRemaining'])
        self.assertEqual('38', body['wrath']['attack'])
        self.assertEqual('114', body['wrath'].get('ultimateBaseDamage'))
        self.assertEqual('114', body['wrath'].get('pendingUltimateBaseDamage'))
        self.assertEqual('strike-2', body['intentMeta']['actionId'])
        self.assertEqual('38', body['intentMeta']['damage'])
        shell = next(status for status in body['statuses'] if status['definitionId'] == 'wrath-shell')
        self.assertIn('DamageImmunity', shell['behaviorKeys'])
        self.assertIn('毒', shell['description'])
        self.assertIn('反伤', shell['description'])

    def test_warning_before_charge_projects_the_next_charge_only_into_extra_damage(self):
        self.play_one_at(self.body()['id'])
        for _ in range(11):
            self.end_round()
        body = self.body()
        self.assertEqual(12, self.battle['round'])
        self.assertTrue(body['wrath']['warningActive'])
        self.assertEqual(12, body['wrath']['releaseRound'])
        self.assertEqual(2, body['wrath']['charges'])
        self.assertEqual('53', body['wrath']['attack'])
        self.assertEqual('159', body['wrath'].get('ultimateBaseDamage'))
        self.assertEqual('189', body['wrath'].get('pendingUltimateBaseDamage'))
        self.assertEqual('charge', body['intentMeta']['actionId'])
        self.assertEqual('0', body['intentMeta']['damage'], 'the regular charge icon must exclude the extra ultimate')
        self.end_round()
        self.assertEqual(3, self.body()['wrath']['charges'])
        self.assertEqual('63', self.body()['wrath']['attack'])

    def test_existing_elite_has_no_boss_fields(self):
        previous = self.battle
        self.post(op='close', sessionId=previous['sessionId'], runId=previous['runId'], revision=previous['revision'])
        self.start('standard-armored-summoner')
        enemy = self.battle['enemies'][0]
        self.assertNotIn('wrath', enemy)
        self.assertEqual('4', enemy['intentMeta']['damage'])
        self.assertIn('+3层', enemy['intentMeta']['statusSummary'])

    def test_enemy_phase_regeneration_frames_use_authoritative_entry_state(self):
        hand = next(enemy for enemy in self.battle['enemies'] if enemy['wrath']['role'] == 'Blood')
        self.play_one_at(hand['id'])
        before = self.battle
        preview = self.action('EndTurn', op='preview')
        self.assertEqual(before, preview['battle'])
        self.assertFalse(any(step.get('wrathPlayback') for step in preview['preview']['steps']))
        seen = set()
        for round_number in range(1, 10):
            result = self.end_round()
            frames = [frame for step in result['steps'] for frame in step.get('wrathPlayback', [])]
            self.assertEqual(1, len(frames), 'one committed enemy-phase entry must produce one playback frame')
            frame = frames[0]
            self.assertNotIn(frame['id'], seen)
            seen.add(frame['id'])
            self.assertEqual(round_number, frame['round'])
            self.assertEqual('AwaitingEnemyAction', frame['battle']['phase'])
            projected_hand = next(enemy for enemy in frame['battle']['enemies'] if enemy['id'] == hand['id'])
            cues = [cue for cue in frame['handFloats'] if cue['id'] == hand['id']]
            if round_number < 9:
                self.assertEqual(9 - round_number, projected_hand['wrath']['reviveRemaining'])
                self.assertEqual([{'id': hand['id'], 'remaining': 9 - round_number}], cues)
            else:
                self.assertTrue(projected_hand['alive'])
                self.assertEqual([], cues, 'a regenerated hand must not retain a countdown float')

    def test_target_candidates_use_friendly_unit_names(self):
        selected = [self.battle['players'][0]['hand'][0]['id']]
        preview = self.action('Play', op='preview', selected=selected)['preview']
        candidates = [candidate for slot in preview['targets'] for candidate in slot['candidates']]
        for enemy in self.battle['enemies']:
            candidate = next(candidate for candidate in candidates if candidate['id'] == enemy['id'])
            self.assertEqual(enemy['displayName'], candidate['label'])

    def test_binding_skip_preserves_warning_in_committed_intermediate_frames(self):
        # No card is played, so the helper immunity is never acquired: binding stays fully effective.
        for _ in range(4):
            self.end_round()
        result = self.end_round()
        frames = [frame for step in result['steps'] for frame in step.get('wrathPlayback', [])]
        self.assertEqual([5, 6], [frame['round'] for frame in frames])
        pending = next(enemy for enemy in frames[1]['battle']['enemies'] if enemy['wrath']['role'] == 'Body')
        self.assertTrue(pending['wrath']['warningActive'])
        self.assertEqual(6, pending['wrath']['releaseRound'])
        self.assertEqual(7, self.battle['round'])
        self.assertFalse(self.body()['wrath']['warningActive'])


if __name__ == '__main__':
    unittest.main()
