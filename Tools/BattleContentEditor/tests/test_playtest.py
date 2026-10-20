"""Real HTTP -> standalone C# host -> unchanged rule-engine integration tests."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('playtest_editor_server', HERE / 'server.py')
server_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server_module)


def changed_xml(source, edit):
    root = ET.fromstring(source)
    edit(root)
    return ET.tostring(root, encoding='unicode')


class PlaytestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.TemporaryDirectory(prefix='content-playtest-test-')
        cls.compiler = server_module.Compiler(server_module.build(Path(cls.folder.name)))
        cls.server = server_module.make_server(cls.compiler, 0)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = 'http://127.0.0.1:' + str(cls.server.server_port)
        with urllib.request.urlopen(cls.base + '/api/bootstrap', timeout=30) as response:
            cls.token = json.load(response)['token']
        cls.scene = (server_module.ROOT / 'Engine/Content/SingleBattleDemoNew.xml').read_text(encoding='utf-8')
        cls.example = (server_module.ROOT / 'Engine/Runtime/Composition/Content/Examples/battle-content.xml').read_text(encoding='utf-8')

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()
        cls.folder.cleanup()

    def setUp(self):
        self.sessions = {}

    def tearDown(self):
        for session_id, battle in self.sessions.items():
            self.request({'op': 'close', 'sessionId': session_id, 'runId': battle['runId'], 'revision': battle['revision']})

    def request(self, data, headers=None, raw=False):
        body = data if raw else json.dumps(data)
        request = urllib.request.Request(self.base + '/api/playtest', data=body.encode('utf-8'),
            headers=headers if headers is not None else {'Content-Type': 'application/json', 'X-Editor-Token': self.token})
        try:
            response = urllib.request.urlopen(request, timeout=40)
        except urllib.error.HTTPError as error:
            response = error
        result = json.load(response)
        if result.get('battle'):
            self.sessions[result['battle']['sessionId']] = result['battle']
        return response.status, result

    def start(self, xml=None, encounter='single-battle-new', seed='104729', battle=None, draw=True):
        data = {'op': 'start', 'sessionId': battle['sessionId'] if battle else str(uuid.uuid4()),
                'xml': xml if xml is not None else self.scene, 'encounterId': encounter, 'seed': seed}
        if battle:
            data['runId'] = battle['runId']
        status, result = self.request(data)
        self.assertEqual(200, status, result)
        self.assertTrue(result['success'], result)
        if draw:
            for player in result['battle']['players']:
                if player.get('manualDraw') and player['window'] == 'Draw':
                    result = self.action(result['battle'], 'Draw', actor=player['id'], drawCount=player['drawLimit'])
                    self.assertTrue(result['success'], result)
        return result

    def action(self, battle, action, selected=None, op='command', actor=None, bindings=None, **extra):
        data = {'op': op, 'sessionId': battle['sessionId'], 'runId': battle['runId'],
                'revision': battle['revision'], 'actor': actor or (battle['actors'][0] if battle['actors'] else battle['players'][0]['id']), 'action': action,
                'selected': selected or [], 'targetBindings': bindings or {}}
        data.update(extra)
        status, result = self.request(data)
        self.assertEqual(200, status, result)
        if op == 'command' and action == 'EndTurn' and result.get('success'):
            next_player = next((p for p in result['battle']['players'] if p['id'] == data['actor']), None)
            if next_player and next_player.get('manualDraw') and next_player['window'] == 'Ending' and next_player['canAct']:
                result = self.action(result['battle'], 'FinishTurn', actor=data['actor'])
        return result

    def state(self, battle):
        status, result = self.request({'op': 'state', 'sessionId': battle['sessionId']})
        self.assertEqual(200, status, result)
        self.assertTrue(result['success'], result)
        return result['battle']

    def assert_success(self, result):
        self.assertTrue(result['success'], result)
        return result['battle']

    @staticmethod
    def comparable(battle):
        result = copy.deepcopy(battle)
        result.pop('sessionId', None)
        result.pop('runId', None)
        return result

    def test_standard_straight_flush_uses_highest_score_with_poison_and_separate_healing(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8')
        def edit(root):
            deck = root.find("Decks/Deck[@id='standard-deck']")
            for card in list(deck):
                deck.remove(card)
            for rank in range(2, 7):
                ET.SubElement(deck, 'Card', id='flush-' + str(rank), face='standard:1:' + str(rank))
            root.find("Encounters/Encounter[@id='standard-single-battle']/Player").set('initialHealth', '400')
        battle = self.start(changed_xml(xml, edit), encounter='standard-single-battle', draw=False)['battle']
        battle = self.assert_success(self.action(battle, 'SkipDraw'))
        cards = [c['id'] for c in battle['players'][0]['hand']]
        preview = self.action(battle, 'Play', cards, op='preview')['preview']
        self.assertTrue(preview['legal'], preview)
        self.assertIsNone(preview['targetMessage'])
        self.assertEqual('StraightFlush', preview['hand']['category'])
        self.assertEqual('同花顺', preview['hand']['label'])
        self.assertEqual('8', str(preview['hand']['multiplier']))
        self.assertEqual('160', str(preview['hand']['total']))
        self.assertTrue({'Straight', 'Flush'}.issubset({
            mechanism['category'] for mechanism in preview['hand']['mechanisms']}))
        self.assertEqual(battle, self.state(battle))
        played = self.action(battle, 'Play', cards)
        self.assertTrue(played['success'], played)
        lines = '\n'.join(line for step in played['steps'] for line in step['lines'])
        self.assertIn('中毒', lines)
        self.assertIn('层', lines)
        self.assertIn('中毒者回合开始', lines)
        self.assertNotIn('所属阵营阶段结束', lines)
        self.assertEqual(5, played['battle']['enemies'][0]['statuses'][0]['stacks'])
        self.assertEqual('440', played['battle']['enemies'][0]['health'])
        self.assertEqual('416', played['battle']['players'][0]['health'])

    def test_standard_full_house_uses_highest_score_once_for_area_damage_and_control(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8')
        def edit(root):
            deck = root.find("Decks/Deck[@id='standard-deck']")
            for card in list(deck):
                deck.remove(card)
            for index, (suit, rank) in enumerate([(1, 7), (2, 7), (3, 7), (1, 9), (2, 9)]):
                ET.SubElement(deck, 'Card', id='full-house-' + str(index), face='standard:' + str(suit) + ':' + str(rank))
            encounter = root.find("Encounters/Encounter[@id='standard-single-battle']")
            ET.SubElement(encounter, 'Enemy', id='enemy-two', ref='rotator')
        battle = self.start(changed_xml(xml, edit), encounter='standard-single-battle', draw=False)['battle']
        battle = self.assert_success(self.action(battle, 'SkipDraw'))
        cards = [card['id'] for card in battle['players'][0]['hand']]
        preview = self.action(battle, 'Play', cards, op='preview')['preview']
        self.assertTrue(preview['legal'], preview)
        self.assertEqual(('FullHouse', '4', '156'), (
            preview['hand']['category'], str(preview['hand']['multiplier']), str(preview['hand']['total'])))
        self.assertTrue({'FullHouse', 'ThreeOfAKind'}.issubset({
            mechanism['category'] for mechanism in preview['hand']['mechanisms']}))
        self.assertEqual(battle, self.state(battle))
        after = self.assert_success(self.action(battle, 'Play', cards))
        self.assertEqual(['444', '444'], [enemy['health'] for enemy in after['enemies']])
        self.assertEqual(1, sum(status['definitionId'] == 'stun' for enemy in after['enemies'] for status in enemy['statuses']))
        self.assertIn('control-immunity', [status['definitionId'] for status in after['players'][0]['statuses']])

    def test_standard_suited_triple_scores_all_flush_cards_and_keeps_area_effect(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8')
        def edit(root):
            deck = root.find("Decks/Deck[@id='standard-deck']")
            for card in list(deck):
                deck.remove(card)
            for index, rank in enumerate([7, 7, 7, 2, 3]):
                ET.SubElement(deck, 'Card', id='suited-triple-' + str(index), face='standard:1:' + str(rank))
            encounter = root.find("Encounters/Encounter[@id='standard-single-battle']")
            encounter.find('Player').set('initialHealth', '400')
            ET.SubElement(encounter, 'Enemy', id='enemy-two', ref='rotator')
        battle = self.start(changed_xml(xml, edit), encounter='standard-single-battle', draw=False)['battle']
        battle = self.assert_success(self.action(battle, 'SkipDraw'))
        cards = [card['id'] for card in battle['players'][0]['hand']]
        preview = self.action(battle, 'Play', cards, op='preview')['preview']
        self.assertTrue(preview['legal'], preview)
        self.assertEqual(('Flush', '26', '4', '104'), (
            preview['hand']['category'], str(preview['hand']['points']),
            str(preview['hand']['multiplier']), str(preview['hand']['total'])))
        self.assertTrue({'Flush', 'ThreeOfAKind'}.issubset({
            mechanism['category'] for mechanism in preview['hand']['mechanisms']}))
        self.assertEqual(battle, self.state(battle))
        after = self.assert_success(self.action(battle, 'Play', cards))
        self.assertEqual(['496', '496'], [enemy['health'] for enemy in after['enemies']])
        self.assertEqual('420', after['players'][0]['health'])

    def test_five_card_flush_mechanism_survives_a_higher_scoring_pattern(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8')
        for ranks, category, health in [
            ([2, 3, 4, 5, 6], 'StraightFlush', '416'),
            ([7, 7, 7, 9, 9], 'FullHouse', '431'),
        ]:
            with self.subTest(category=category):
                def edit(root):
                    root.find("Players/Player[@id='hero']").set('minFlushCards', '5')
                    root.find("Encounters/Encounter[@id='standard-single-battle']/Player").set('initialHealth', '400')
                    deck = root.find("Decks/Deck[@id='standard-deck']")
                    for card in list(deck):
                        deck.remove(card)
                    for index, rank in enumerate(ranks):
                        ET.SubElement(deck, 'Card', id='same-suit-' + str(index), face='standard:1:' + str(rank))
                battle = self.start(changed_xml(xml, edit), encounter='standard-single-battle', draw=False)['battle']
                battle = self.assert_success(self.action(battle, 'SkipDraw'))
                cards = [card['id'] for card in battle['players'][0]['hand']]
                preview = self.action(battle, 'Play', cards, op='preview')['preview']
                self.assertTrue(preview['legal'], preview)
                self.assertEqual(category, preview['hand']['category'])
                self.assertIn('Flush', [item['category'] for item in preview['hand']['mechanisms']])
                self.assertEqual(battle, self.state(battle))
                after = self.assert_success(self.action(battle, 'Play', cards))
                self.assertEqual(health, after['players'][0]['health'])

    def test_dated_poison_timing_subject_matches_preview_description_and_execution(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle-2026-10-20.xml').read_text(encoding='utf-8')
        cases = [
            ('Holder', 'TurnStarted', '中毒者回合开始', '回合开始：敌人 1 · enemy', '800'),
            ('Holder', 'PhaseEnding', '中毒者所属阵营阶段结束', '敌人阶段结束', '787'),
            ('Applier', 'TurnStarted', '施加者回合开始', '回合开始：玩家 1 · player', '787'),
            ('Applier', 'PhaseEnding', '施加者所属阵营阶段结束', '玩家阶段结束', '800'),
        ]
        for subject, timing, description, expected_clock, player_health in cases:
            with self.subTest(subject=subject, timing=timing):
                def edit(root):
                    periodic = root.find("Statuses/Status[@id='bleed']/PeriodicDamage")
                    periodic.set('timingSubject', subject)
                    periodic.set('timing', timing)
                    enemy = root.find("Enemies/Enemy[@id='rotator']")
                    for child in list(enemy):
                        enemy.remove(child)
                    enemy.set('maxHealth', '45')
                    enemy.set('damage', '13')
                    deck = root.find("Decks/Deck[@id='standard-deck']")
                    for card in list(deck):
                        deck.remove(card)
                    for index, rank in enumerate(range(2, 7)):
                        ET.SubElement(deck, 'Card', id='poison-clock-' + str(index),
                                      face='standard:' + str(index % 4 + 1) + ':' + str(rank))
                battle = self.start(changed_xml(xml, edit), encounter='standard-single-battle', draw=False)['battle']
                battle = self.assert_success(self.action(battle, 'SkipDraw'))
                cards = [card['id'] for card in battle['players'][0]['hand']]
                preview = self.action(battle, 'Play', cards, op='preview')
                self.assertTrue(preview['preview']['legal'], preview)
                self.assertEqual(battle, self.state(battle))
                played = self.action(battle, 'Play', cards)
                battle = self.assert_success(played)
                self.assertEqual(preview['preview']['steps'], played['steps'])
                self.assertEqual('5', battle['enemies'][0]['health'])
                poison = next(status for status in battle['enemies'][0]['statuses'] if status['definitionId'] == 'bleed')
                self.assertEqual(5, poison['stacks'])
                self.assertIn(description + '：1 伤害/层，随后减 1 层', poison['name'])
                preview = self.action(battle, 'EndTurn', op='preview')
                self.assertTrue(preview['preview']['legal'], preview)
                self.assertEqual(battle, preview['battle'])
                self.assertEqual(battle, self.state(battle))
                ended = self.action(battle, 'EndTurn')
                after = self.assert_success(ended)
                self.assertEqual(preview['preview']['steps'], ended['steps'])
                lines = [line for step in ended['steps'] for line in step['lines']]
                poison_index = next(index for index, line in enumerate(lines) if '持续效果 · 伤害 5' in line)
                clocks = [line for line in lines[:poison_index]
                          if line.startswith('回合开始：') or line in ('玩家阶段结束', '敌人阶段结束')]
                self.assertEqual(expected_clock, clocks[-1])
                self.assertEqual('0', after['enemies'][0]['health'])
                self.assertEqual(player_health, after['players'][0]['health'])
                self.assertTrue(after['finished'])
                self.assertEqual('胜利', after['outcome'])

    def test_enemy_behavior_templates_expose_world_effect_intents(self):
        for file, word in [('07_RotationGrunt', '试探'), ('08_ArmoredSummoner', '召唤'), ('09_GluttonyBoss', '吞食')]:
            with self.subTest(file=file):
                xml = (server_module.ROOT / ('Engine/Content/Templates/' + file + '.xml')).read_text(encoding='utf-8')
                battle = self.start(xml, encounter=file)['battle']
                self.assertIn(word, ' '.join(battle['enemies'][0]['intent']))
                result = self.action(battle, 'EndTurn')
                after = self.assert_success(result)
                if file == '08_ArmoredSummoner':
                    self.assertEqual(3, len(after['enemies']))
                    self.assertEqual('800', after['players'][0]['health'])
                    self.assertIn('护甲召唤精英：召唤 轮换小怪 × 2（下个敌方回合行动）',
                                  [line for step in result['steps'] for line in step['lines']])
                if file == '09_GluttonyBoss':
                    self.assertEqual(1, after['players'][0]['discardCount'])
                    self.assertIn('暴食Boss：生成废牌 × 1，塞入 玩家 的弃牌堆',
                                  [line for step in result['steps'] for line in step['lines']])

    def test_world_effect_logs_use_actual_counts_and_card_zones(self):
        template = (server_module.ROOT / 'Engine/Content/Templates/08_ArmoredSummoner.xml').read_text(encoding='utf-8')
        for limit, expected in [(1, 0), (2, 1)]:
            xml = changed_xml(template, lambda root: root.find('.//Summon').set('livingLimit', str(limit)))
            battle = self.start(xml, encounter='08_ArmoredSummoner')['battle']
            result = self.action(battle, 'EndTurn')
            after = self.assert_success(result)
            self.assertEqual(1 + expected, len(after['enemies']))
            lines = [line for step in result['steps'] for line in step['lines'] if '：召唤 ' in line]
            self.assertEqual(expected, len(lines))
            if expected:
                self.assertIn('召唤 轮换小怪 × 1', lines[0])
        template = (server_module.ROOT / 'Engine/Content/Templates/09_GluttonyBoss.xml').read_text(encoding='utf-8')

        def edit(root):
            for effect in root.findall('.//AddCards'):
                effect.set('zone', 'deck')
                effect.set('count', '3')

        battle = self.start(changed_xml(template, edit), encounter='09_GluttonyBoss')['battle']
        result = self.action(battle, 'EndTurn')
        after = self.assert_success(result)
        self.assertEqual(battle['players'][0]['deckCount'] + 3, after['players'][0]['deckCount'])
        self.assertIn('暴食Boss：生成废牌 × 3，塞入 玩家 的抽牌堆（随机洗入）',
                      [line for step in result['steps'] for line in step['lines']])
        pile = after['players'][0]['piles']['deck']
        self.assertEqual(after['players'][0]['deckCount'], pile['count'])
        self.assertEqual(3, pile['unplayableCount'])
        self.assertEqual([3], [card['count'] for card in pile['cards'] if not card['canPlay']])
        self.assertEqual([{'actor': battle['enemies'][0]['id'], 'owner': battle['players'][0]['id'],
                           'zone': 'deck', 'position': 'Shuffle', 'count': 3, 'unplayableCount': 3}],
                         [fact for step in result['steps'] for fact in step['cardCreations']])

    def test_configurable_insertion_preview_and_actual_hand(self):
        template = (server_module.ROOT / 'Engine/Content/Templates/09_GluttonyBoss.xml').read_text(encoding='utf-8')
        for zone, position in [('deck', 'Shuffle'), ('deck', 'Top'), ('deck', 'Bottom'), ('hand', 'Default')]:
            with self.subTest(zone=zone, position=position):
                def edit(root):
                    for effect in root.findall('.//AddCards'):
                        effect.set('zone', zone)
                        effect.set('position', position)
                        effect.set('count', '3')
                battle = self.start(changed_xml(template, edit), encounter='09_GluttonyBoss', draw=False)['battle']
                effect = battle['enemies'][0]['intentMeta']['worldEffects'][0]
                resolved = 'Bottom' if position == 'Default' else position
                self.assertEqual((zone, resolved), (effect['zone'], effect['position']))
                preview = self.action(battle, 'EndTurn', op='preview')
                self.assertTrue(preview['preview']['legal'])
                self.assertEqual(battle, self.state(battle))
                result = self.action(battle, 'EndTurn')
                after = self.assert_success(result)
                self.assertEqual([{'actor': 'enemy', 'owner': 'player', 'zone': zone, 'position': resolved,
                                   'count': 3, 'unplayableCount': 3}],
                                 [fact for step in result['steps'] for fact in step['cardCreations']])
                self.assertEqual(0, after['players'][0]['discardCount'])
                if zone == 'hand':
                    self.assertEqual(3, len(after['players'][0]['hand']))
                    self.assertTrue(all(not card['canPlay'] and card['canDiscard'] for card in after['players'][0]['hand']))
                    self.assertEqual(52, after['players'][0]['deckCount'])
                else:
                    self.assertEqual(55, after['players'][0]['deckCount'])
                    after = self.assert_success(self.action(after, 'Draw', drawCount=3))
                    junk = sum(not card['canPlay'] for card in after['players'][0]['hand'])
                    if position == 'Top':
                        self.assertEqual(3, junk)
                    elif position == 'Bottom':
                        self.assertEqual(0, junk)

    def test_standard_boss_piles_aggregate_junk_and_report_committed_creations(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8')
        battle = self.start(xml, encounter='standard-gluttony-boss')['battle']
        self.assertEqual({'count': 0, 'unplayableCount': 0, 'cards': []}, battle['players'][0]['piles']['discard'])
        for expected in (1, 2):
            if battle['players'][0]['window'] == 'Draw':
                battle = self.assert_success(self.action(battle, 'SkipDraw'))
            result = self.action(battle, 'EndTurn')
            after = self.assert_success(result)
            player = after['players'][0]
            pile = player['piles']['discard']
            self.assertEqual(expected, player['discardCount'])
            self.assertEqual(expected, pile['count'])
            self.assertEqual(expected, pile['unplayableCount'])
            self.assertEqual(1, len(pile['cards']))
            junk = pile['cards'][0]
            self.assertEqual(expected, junk['count'])
            self.assertFalse(junk['canPlay'])
            self.assertTrue(junk['canDiscard'])
            self.assertFalse(junk['disabled'])
            self.assertIn('Unplayable', junk['effects'])
            self.assertNotIn('id', junk)
            self.assertEqual(0, player['piles']['deck']['unplayableCount'])
            self.assertEqual([{'actor': battle['enemies'][0]['id'], 'owner': player['id'],
                               'zone': 'discard', 'position': 'Bottom', 'count': 1, 'unplayableCount': 1}],
                             [fact for step in result['steps'] for fact in step['cardCreations']])
            battle = after

    def test_pile_composition_is_sorted_without_instance_ids_or_draw_order(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8')
        xml = changed_xml(xml, lambda root: root.find('Players/Player').set('openingDeal', 'false'))
        first = self.start(xml, encounter='standard-gluttony-boss', seed='pile-a', draw=False)['battle']
        second = self.start(xml, encounter='standard-gluttony-boss', seed='pile-b', draw=False)['battle']
        pile = first['players'][0]['piles']['deck']
        self.assertEqual(pile, second['players'][0]['piles']['deck'])
        self.assertEqual(52, pile['count'])
        self.assertEqual(0, pile['unplayableCount'])
        self.assertEqual(52, len(pile['cards']))
        for card in pile['cards']:
            self.assertEqual({'label', 'rank', 'suit', 'disabled', 'canPlay', 'canDiscard', 'effects', 'count'}, set(card))
            self.assertEqual(1, card['count'])

    def test_previewed_junk_creation_does_not_change_piles(self):
        xml = (server_module.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8')
        battle = self.start(xml, encounter='standard-gluttony-boss')['battle']
        # Standard ends immediately, so preview EndTurn directly includes the enemy settlement.
        preview = self.action(battle, 'EndTurn', op='preview')
        self.assertTrue(preview['preview']['legal'])
        self.assertEqual([{'actor': battle['enemies'][0]['id'], 'owner': battle['players'][0]['id'],
                           'zone': 'discard', 'position': 'Bottom', 'count': 1, 'unplayableCount': 1}],
                         [fact for step in preview['preview']['steps'] for fact in step['cardCreations']])
        self.assertEqual(battle, preview['battle'])
        self.assertEqual(battle, self.state(battle))
        committed = self.assert_success(self.action(battle, 'EndTurn'))
        self.assertEqual(1, committed['players'][0]['piles']['discard']['unplayableCount'])

    def test_presentation_labels_statuses_and_relics_come_from_frozen_content(self):
        template = (server_module.ROOT / 'Engine/Content/Templates/08_ArmoredSummoner.xml').read_text(encoding='utf-8')

        def edit(root):
            root.find("Players/Player[@id='hero']").set('name', '旅人甲')
            root.find("Enemies/Enemy[@id='summoner']").set('name', '改名召唤者')
            root.find("Enemies/Enemy[@id='rotator']").set('name', '纸片护卫')
            root.find("Relics/Relic[@id='straight-dot']").set('name', '红色护符')
            ET.SubElement(root.find('Loadouts/Loadout'), 'Relic', {'instanceId': 'equipped-token', 'ref': 'straight-dot', 'level': '1'})
            ET.SubElement(root.find("Enemies/Enemy[@id='summoner']/Behavior/OpeningEffects"), 'Effect',
                          {'id': 'opening-bleed', 'kind': 'ApplyStatus', 'statusRef': 'bleed', 'target': 'Self', 'stacks': '2'})

        battle = self.start(changed_xml(template, edit), encounter='08_ArmoredSummoner')['battle']
        player, enemy = battle['players'][0], battle['enemies'][0]
        self.assertEqual(('hero', '旅人甲'), (player['definitionId'], player['displayName']))
        self.assertEqual(('summoner', '改名召唤者'), (enemy['definitionId'], enemy['displayName']))
        self.assertNotEqual(enemy['id'], enemy['definitionId'])
        armor = next(status for status in enemy['statuses'] if status['definitionId'] == 'layer-armor')
        bleed = next(status for status in enemy['statuses'] if status['definitionId'] == 'bleed')
        self.assertEqual(['HitArmor'], armor['behaviorKeys'])
        self.assertFalse(armor['isNegative'])
        self.assertIn('Negative', bleed['tags'])
        self.assertEqual(['PeriodicDamage'], bleed['behaviorKeys'])
        self.assertTrue(bleed['isNegative'])
        self.assertEqual('红色护符', player['relics'][0]['displayName'])
        self.assertEqual('straight-dot', player['relics'][0]['definitionId'])
        self.assertIn('施加状态', player['relics'][0]['description'])
        intent = enemy['intentMeta']
        self.assertEqual(('summon', '召唤护卫', 'normal'), (intent['actionId'], intent['name'], intent['stageId']))
        self.assertTrue(intent['available'])
        self.assertFalse(intent['skipped'])
        self.assertEqual(('Summon', 2, 'rotator', '纸片护卫'),
                         tuple(intent['worldEffects'][0][key] for key in ('kind', 'count', 'enemyRef', 'displayName')))
        self.assertEqual(battle, self.state(battle))
        after = self.assert_success(self.action(battle, 'EndTurn'))
        summons = [unit for unit in after['enemies'] if unit['definitionId'] == 'rotator']
        self.assertEqual(2, len(summons))
        self.assertTrue(all(unit['displayName'] == '纸片护卫' for unit in summons))
        self.assertEqual('红色护符', after['players'][0]['relics'][0]['displayName'])

    def test_standard_pack_and_small_assets_are_served_without_exposing_other_files(self):
        with urllib.request.urlopen(self.base + '/api/standard', timeout=30) as response:
            root = ET.fromstring(response.read())
            self.assertEqual('application/xml', response.headers.get_content_type())
            self.assertEqual({'standard-single-battle', 'standard-armored-summoner', 'standard-gluttony-boss'},
                             {node.get('id') for node in root.findall('Encounters/Encounter')})
        folder = server_module.HERE / 'web' / 'assets'
        folder.mkdir(exist_ok=True)
        prefix = 'http-test-' + uuid.uuid4().hex
        files = []
        try:
            for extension, body, expected_type in [('.png', b'\x89PNG\r\n\x1a\n', 'image/png'),
                    ('.svg', b'<svg xmlns="http://www.w3.org/2000/svg"/>', 'image/svg+xml')]:
                path = folder / (prefix + extension)
                files.append(path)
                path.write_bytes(body)
                with urllib.request.urlopen(self.base + '/assets/' + path.name, timeout=30) as response:
                    self.assertEqual(body, response.read())
                    self.assertEqual(expected_type, response.headers.get_content_type())
                    self.assertEqual('nosniff', response.headers['X-Content-Type-Options'])
                    self.assertEqual('no-store', response.headers['Cache-Control'])
            large = folder / (prefix + '.webp')
            files.append(large)
            large.write_bytes(b'x' * (server_module.MAX_ASSET_BYTES + 1))
            unsupported = folder / (prefix + '.txt')
            files.append(unsupported)
            unsupported.write_text('not an image', encoding='utf-8')
            paths = ['/assets/' + large.name, '/assets/' + unsupported.name, '/assets/../server.py',
                     '/assets/%2e%2e/server.py', '/assets/subfolder/test.png', '/assets/.hidden.png']
            with tempfile.TemporaryDirectory() as temporary:
                external = Path(temporary) / 'private.png'
                external.write_bytes(b'outside the web folder')
                link = folder / (prefix + '-linked.png')
                try:
                    link.symlink_to(external)
                    files.append(link)
                    paths.append('/assets/' + link.name)
                except OSError:
                    pass  # Windows may require a privilege for test symlinks.
                for route in paths:
                    with self.subTest(route=route), self.assertRaises(urllib.error.HTTPError) as caught:
                        urllib.request.urlopen(self.base + route, timeout=30)
                    self.assertEqual(404, caught.exception.code)
        finally:
            for path in files:
                path.unlink(missing_ok=True)

    def test_manual_draw_skip_and_overflow_commands_are_transactional(self):
        def edit(root):
            hand = root.find("HandRules/HandRule/Hand[@category='HighCard']")
            for effect in list(hand):
                hand.remove(effect)
            ET.SubElement(hand, 'Effect', {'id': 'draw', 'kind': 'DrawCards', 'amount': '10', 'target': 'Self'})
        battle = self.start(changed_xml(self.scene, edit), draw=False)['battle']
        player = battle['players'][0]
        self.assertEqual([], player['hand'])
        self.assertEqual('Draw', player['window'])
        drawn = self.assert_success(self.action(battle, 'Draw', drawCount=5))
        self.assertEqual(5, len(drawn['players'][0]['hand']))
        selected = [drawn['players'][0]['hand'][0]['id']]
        played = self.assert_success(self.action(drawn, 'Play', selected))
        self.assertEqual(14, len(played['players'][0]['hand']))
        forced = self.assert_success(self.action(played, 'EndTurn'))
        self.assertEqual('ForcedDiscard', forced['players'][0]['window'])
        self.assertEqual(6, forced['players'][0]['requiredDiscards'])
        ids = [card['id'] for card in forced['players'][0]['hand']]
        invalid = self.action(forced, 'ConfirmOverflow', ids[:5])
        self.assertFalse(invalid['success'])
        self.assertEqual(forced, self.state(forced))
        confirmed = self.assert_success(self.action(forced, 'ConfirmOverflow', ids[:6]))
        self.assertEqual('Ending', confirmed['players'][0]['window'])
        self.assertEqual('0', confirmed['players'][0]['block'])
        ended = self.assert_success(self.action(confirmed, 'FinishTurn'))
        skipped = self.assert_success(self.action(ended, 'SkipDraw'))
        self.assertEqual(8, len(skipped['players'][0]['hand']))
        self.assertEqual('Action', skipped['players'][0]['window'])

    def test_scene_opening_uses_xml_and_authoritative_state(self):
        result = self.start()
        battle = result['battle']
        player, enemy = battle['players'][0], battle['enemies'][0]
        self.assertEqual('single-battle-new', battle['encounterId'])
        self.assertEqual('104729', battle['seed'])
        self.assertTrue(battle['fingerprint'])
        self.assertIsInstance(battle['revision'], str)
        self.assertEqual(('800', 3, 8, None), (player['health'], player['energy'], len(player['hand']), player['discardsRemaining']))
        self.assertEqual('1200', enemy['health'])
        self.assertEqual(44, player['deckCount'])
        self.assertTrue(player['canAct'])
        self.assertTrue(result['steps'])
        self.assertTrue(any(step['lines'] for step in result['steps']))

    def test_preview_is_pure_and_returns_the_real_hand_and_events(self):
        battle = self.start()['battle']
        selected = [battle['players'][0]['hand'][0]['id']]
        first = self.action(battle, 'Play', selected, op='preview')
        self.assertTrue(first['success'], first)
        self.assertTrue(first['preview']['legal'], first)
        self.assertEqual('HighCard', first['preview']['hand']['category'])
        self.assertTrue(first['preview']['steps'])
        for _ in range(3):
            preview = self.action(battle, 'Play', selected, op='preview')
            self.assertEqual(first['preview'], preview['preview'])
            self.assertEqual(battle, preview['battle'])
        self.assertEqual(battle, self.state(battle))
        played = self.assert_success(self.action(battle, 'Play', selected))
        expected_damage = int(first['preview']['hand']['total'])
        self.assertEqual(1200 - expected_damage, int(played['enemies'][0]['health']))
        self.assertEqual(2, played['players'][0]['energy'])
        self.assertEqual(7, len(played['players'][0]['hand']))

    def test_discard_preview_does_not_consume_relic_charges(self):
        battle = self.start(self.example, 'example-fight')['battle']
        selected = [battle['players'][0]['hand'][0]['id']]
        relic = battle['players'][0]['relics'][0]
        self.assertEqual(3, relic['charges'])
        for _ in range(3):
            preview = self.action(battle, 'Discard', selected, op='preview')
            self.assertTrue(preview['preview']['legal'], preview)
            self.assertEqual(battle, preview['battle'])
        discarded = self.assert_success(self.action(battle, 'Discard', selected))
        self.assertEqual(2, discarded['players'][0]['relics'][0]['charges'])
        self.assertGreater(int(discarded['players'][0]['block']), 0)

    def test_play_discard_end_turn_resolve_an_entire_round(self):
        battle = self.start()['battle']
        played = self.assert_success(self.action(battle, 'Play', [battle['players'][0]['hand'][0]['id']]))
        discarded = self.assert_success(self.action(played, 'Discard', [played['players'][0]['hand'][0]['id']]))
        player = discarded['players'][0]
        self.assertGreater(discarded['round'], battle['round'])
        self.assertEqual(('568', '0', 3, 6, None), (player['health'], player['block'], player['energy'], len(player['hand']), player['discardsRemaining']))
        self.assertEqual('Draw', player['window'])
        self.assertTrue(player['canAct'])

    def test_fixed_seed_restart_repeats_cards_results_and_logs(self):
        original = self.start(seed='9007199254740993')['battle']
        selected = [original['players'][0]['hand'][0]['id']]
        result = self.action(original, 'Play', selected)
        self.assert_success(result)
        restarted = self.start(seed=original['seed'], battle=result['battle'])['battle']
        self.assertNotEqual(original['runId'], restarted['runId'])
        self.assertEqual(self.comparable(original), self.comparable(restarted))
        repeated = self.action(restarted, 'Play', selected)
        self.assertEqual(self.comparable(result['battle']), self.comparable(repeated['battle']))
        self.assertEqual(result['steps'], repeated['steps'])

    def test_failed_restarts_keep_the_installed_battle(self):
        battle = self.start()['battle']
        for xml, encounter in (('<broken>', 'single-battle-new'),
                               (self.scene.replace('statusRef="stun"', 'statusRef="missing"'), 'single-battle-new'),
                               (self.scene, 'missing-encounter')):
            with self.subTest(encounter=encounter, xml=xml[:20]):
                _, result = self.request({'op': 'start', 'sessionId': battle['sessionId'], 'runId': battle['runId'],
                                         'xml': xml, 'encounterId': encounter, 'seed': '2'})
                self.assertFalse(result['success'], result)
                self.assertEqual(battle, self.state(battle))
        self.assertTrue(self.action(battle, 'Discard', [battle['players'][0]['hand'][0]['id']])['success'])

    def test_configuration_is_frozen_until_successful_restart(self):
        battle = self.start()['battle']
        changed = self.scene.replace('maxHealth="800"', 'maxHealth="999"').replace('damage="240"', 'damage="10"')
        ended = self.assert_success(self.action(battle, 'EndTurn'))
        self.assertEqual('560', ended['players'][0]['health'])
        restarted = self.start(changed, battle=ended)['battle']
        self.assertEqual('999', restarted['players'][0]['health'])
        self.assertNotEqual(battle['fingerprint'], restarted['fingerprint'])
        ended = self.assert_success(self.action(restarted, 'EndTurn'))
        self.assertEqual('989', ended['players'][0]['health'])

    def test_stale_revision_and_previous_run_commands_cannot_reapply(self):
        original = self.start()['battle']
        selected = [original['players'][0]['hand'][0]['id']]
        after = self.assert_success(self.action(original, 'Discard', selected))
        stale = self.action(original, 'Discard', selected)
        self.assertFalse(stale['success'], stale)
        self.assertEqual(after, self.state(after))
        restarted = self.start(battle=after)['battle']
        stale = self.action(after, 'EndTurn')
        self.assertFalse(stale['success'], stale)
        self.assertEqual(restarted, self.state(restarted))

    def test_two_tabs_have_independent_sessions(self):
        first, second = self.start()['battle'], self.start(seed='2')['battle']
        self.assertNotEqual(first['sessionId'], second['sessionId'])
        self.assert_success(self.action(first, 'EndTurn'))
        self.assertEqual(second, self.state(second))
        self.assertNotEqual([c['id'] for c in first['players'][0]['hand']], [c['id'] for c in second['players'][0]['hand']])

    def test_relic_modified_requested_targets_are_exposed_and_enforced(self):
        def edit(root):
            relic = ET.SubElement(root.find('Relics'), 'Relic', {'id': 'manual-target'})
            ET.SubElement(relic, 'HandModifier', {'id': 'target', 'category': 'HighCard', 'operation': 'ChangeTarget',
                'stepId': 'damage/1', 'target': 'Opponents', 'selection': 'Requested', 'requestSlot': 'attack'})
            ET.SubElement(root.find("Loadouts/Loadout[@id='baseline']"), 'Relic', {'instanceId': 'manual-target', 'ref': 'manual-target'})
            ET.SubElement(root.find("Encounters/Encounter[@id='single-battle-new']"), 'Enemy', {'id': 'enemy-2', 'ref': 'guardian'})
        battle = self.start(changed_xml(self.scene, edit))['battle']
        selected = [battle['players'][0]['hand'][0]['id']]
        result = self.action(battle, 'Play', selected, op='preview')
        self.assertTrue(result['success'], result)
        targets = result['preview']['targets']
        self.assertEqual(1, len(targets))
        self.assertEqual('attack', targets[0]['slot'])
        self.assertEqual({'enemy', 'enemy-2'}, {item['id'] for item in targets[0]['candidates']})
        illegal = self.action(battle, 'Play', selected, bindings={'attack': ['player']})
        self.assertFalse(illegal['success'], illegal)
        self.assertEqual(battle, self.state(battle))
        result = self.action(battle, 'Play', selected, op='preview', bindings={'attack': ['enemy-2']})
        self.assertTrue(result['preview']['legal'], result)
        after = self.assert_success(self.action(battle, 'Play', selected, bindings={'attack': ['enemy-2']}))
        self.assertEqual('1200', after['enemies'][0]['health'])
        self.assertLess(int(after['enemies'][1]['health']), 1200)

    def test_zero_cost_actions_remain_available_at_zero_energy(self):
        def edit(root):
            player = root.find('Players/Player')
            player.set('initialEnergy', '0')
            player.set('playEnergyCost', '0')
            player.set('discardEnergyCost', '0')
        battle = self.start(changed_xml(self.scene, edit))['battle']
        self.assertEqual(0, battle['players'][0]['energy'])
        self.assertTrue(battle['players'][0]['canAct'])
        after = self.assert_success(self.action(battle, 'Play', [battle['players'][0]['hand'][0]['id']]))
        self.assertEqual(0, after['players'][0]['energy'])
        self.assertEqual(battle['round'], after['round'])
        self.assertTrue(after['players'][0]['canAct'])
        ended = self.assert_success(self.action(after, 'EndTurn'))
        self.assertGreater(ended['round'], after['round'])

    def test_finished_battle_rejects_actions_without_mutation(self):
        xml = self.scene.replace('maxHealth="1200"', 'maxHealth="1"')
        battle = self.start(xml)['battle']
        ended = self.assert_success(self.action(battle, 'Play', [battle['players'][0]['hand'][0]['id']]))
        self.assertTrue(ended['finished'])
        self.assertFalse(ended['players'][0]['canAct'])
        for action in ('Play', 'Discard', 'EndTurn'):
            result = self.action(ended, action, [ended['players'][0]['hand'][0]['id']] if action != 'EndTurn' else [])
            self.assertFalse(result['success'], result)
            self.assertEqual(ended, self.state(ended))

    def test_invalid_actor_card_and_internal_action_are_rejected(self):
        battle = self.start()['battle']
        for action, selected, actor in (('Play', ['absent-card'], 'player'), ('EndTurn', [], 'enemy'), ('EnemyAction', [], 'enemy')):
            result = self.action(battle, action, selected, actor=actor)
            self.assertFalse(result['success'], result)
            self.assertEqual(battle, self.state(battle))

    def test_lost_command_response_recovers_by_session_id_without_replaying(self):
        battle = self.start()['battle']
        original = self.compiler.playtest
        committed = []

        def lose_response(request):
            result = original(request)
            if request.get('op') == 'command' and not committed:
                committed.append(result)
                raise RuntimeError('test: response lost after commit')
            return result

        self.compiler.playtest = lose_response
        try:
            status, result = self.request({'op': 'command', 'sessionId': battle['sessionId'],
                'runId': battle['runId'], 'revision': battle['revision'], 'actor': 'player',
                'action': 'Play', 'selected': [battle['players'][0]['hand'][0]['id']]})
            self.assertEqual(503, status)
            self.assertIn('error', result)
            self.assertTrue(committed[0]['success'])
        finally:
            self.compiler.playtest = original
        # Recovery deliberately has no runId/revision: the browser may not have received them.
        recovered = self.state(battle)
        self.assertEqual(committed[0]['battle'], recovered)
        self.assertEqual(2, recovered['players'][0]['energy'])
        self.assertGreater(int(recovered['revision']), int(battle['revision']))
        duplicate = self.action(battle, 'Play', [battle['players'][0]['hand'][0]['id']])
        self.assertFalse(duplicate['success'])
        self.assertEqual(recovered, self.state(recovered))

    def test_close_rejects_stale_run_and_revision_before_releasing_session(self):
        battle = self.start()['battle']
        for run_id, revision in ((str(uuid.uuid4()), battle['revision']),
                                 (battle['runId'], str(int(battle['revision']) + 1))):
            _, result = self.request({'op': 'close', 'sessionId': battle['sessionId'],
                                     'runId': run_id, 'revision': revision})
            self.assertFalse(result['success'], result)
            self.assertEqual(battle, self.state(battle))
        _, result = self.request({'op': 'close', 'sessionId': battle['sessionId'],
                                 'runId': battle['runId'], 'revision': battle['revision']})
        self.assertTrue(result['success'], result)
        _, gone = self.request({'op': 'state', 'sessionId': battle['sessionId']})
        self.assertFalse(gone['success'])

    def test_enemy_acts_once_only_after_all_players_end_their_turn(self):
        def edit(root):
            encounter = root.find("Encounters/Encounter[@id='single-battle-new']")
            ET.SubElement(encounter, 'Player', {'id': 'player-2', 'ref': 'hero', 'loadoutRef': 'baseline'})
        battle = self.start(changed_xml(self.scene, edit))['battle']
        self.assertEqual({'player', 'player-2'}, set(battle['actors']))
        self.assertEqual([8, 8], [len(player['hand']) for player in battle['players']])
        first = self.action(battle, 'EndTurn', actor='player')
        after_first = self.assert_success(first)
        self.assertEqual(battle['round'], after_first['round'])
        self.assertEqual(['800', '800'], [player['health'] for player in after_first['players']])
        self.assertEqual(['player-2'], after_first['actors'])
        self.assertEqual(0, len([step for step in first['steps'] if step['action'] == 'EnemyAction']))
        second = self.action(after_first, 'EndTurn', actor='player-2')
        after_second = self.assert_success(second)
        self.assertGreater(after_second['round'], battle['round'])
        self.assertEqual({'player', 'player-2'}, set(after_second['actors']))
        self.assertEqual(1, len([step for step in second['steps'] if step['action'] == 'EnemyAction']))
        self.assertEqual(240, sum(800 - int(player['health']) for player in after_second['players']))
        self.assertEqual([3, 3], [player['energy'] for player in after_second['players']])

    def test_multiple_effect_slots_bind_different_targets_in_one_action(self):
        def edit(root):
            hand = root.find("HandRules/HandRule/Hand[@category='HighCard']")
            first = hand.find('Effect')
            first.set('selection', 'Requested')
            first.set('requestSlot', 'main')
            ET.SubElement(hand, 'Effect', {'id': 'second-hit', 'kind': 'Damage', 'amount': '7',
                'target': 'Opponents', 'selection': 'Requested', 'requestSlot': 'secondary'})
            ET.SubElement(root.find("Encounters/Encounter[@id='single-battle-new']"), 'Enemy',
                          {'id': 'enemy-2', 'ref': 'guardian'})
        battle = self.start(changed_xml(self.scene, edit))['battle']
        selected = [battle['players'][0]['hand'][0]['id']]
        preview = self.action(battle, 'Play', selected, op='preview')
        self.assertEqual({'main', 'secondary'}, {slot['slot'] for slot in preview['preview']['targets']})
        self.assertFalse(preview['preview']['legal'])
        partial = self.action(battle, 'Play', selected, bindings={'main': ['enemy']})
        self.assertFalse(partial['success'])
        self.assertEqual(battle, self.state(battle))
        bindings = {'main': ['enemy'], 'secondary': ['enemy-2']}
        preview = self.action(battle, 'Play', selected, op='preview', bindings=bindings)
        self.assertTrue(preview['preview']['legal'], preview)
        after = self.assert_success(self.action(battle, 'Play', selected, bindings=bindings))
        self.assertEqual(1200 - int(preview['preview']['hand']['total']), int(after['enemies'][0]['health']))
        self.assertEqual('1193', after['enemies'][1]['health'])

    def test_playtest_endpoint_checks_origin_token_and_json(self):
        data = {'op': 'state', 'sessionId': str(uuid.uuid4())}
        for headers in ({'Content-Type': 'application/json'},
                        {'Content-Type': 'application/json', 'X-Editor-Token': self.token, 'Origin': 'https://other.invalid'}):
            self.assertEqual(403, self.request(data, headers=headers)[0])
        self.assertEqual(415, self.request(data, headers={'Content-Type': 'text/plain', 'X-Editor-Token': self.token})[0])
        self.assertEqual(400, self.request('{broken', raw=True)[0])
        self.assertEqual(400, self.request('[]', raw=True)[0])
        status, result = self.request(data)
        self.assertEqual(200, status)
        self.assertFalse(result['success'])

    def test_close_releases_only_the_requested_session(self):
        first, second = self.start()['battle'], self.start()['battle']
        _, result = self.request({'op': 'close', 'sessionId': first['sessionId'], 'runId': first['runId'], 'revision': first['revision']})
        self.assertTrue(result['success'], result)
        _, gone = self.request({'op': 'state', 'sessionId': first['sessionId']})
        self.assertFalse(gone['success'])
        self.assertEqual(second, self.state(second))


if __name__ == '__main__':
    unittest.main(verbosity=2)
