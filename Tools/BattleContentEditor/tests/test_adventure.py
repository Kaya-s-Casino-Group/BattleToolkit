"""Real HTTP coverage for configured adventure progression and battle handoff."""
import copy
import json
import unittest
import urllib.error
import urllib.request
import uuid
import xml.etree.ElementTree as ET

from native_fixture import NativeHost, server


class AdventureHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.native = NativeHost()
        cls.base = cls.native.base
        with urllib.request.urlopen(cls.base + '/api/bootstrap', timeout=30) as response:
            cls.token = json.load(response)['token']

    @classmethod
    def tearDownClass(cls):
        cls.native.close()

    def setUp(self):
        self.sessions = {}

    def tearDown(self):
        for session_id, adventure in list(self.sessions.items()):
            self.request({'op': 'close', 'sessionId': session_id,
                          'runId': adventure['runId'], 'revision': adventure['revision']})

    def request(self, data, headers=None, raw=False):
        request = urllib.request.Request(
            self.base + '/api/adventure',
            data=(data if raw else json.dumps(data)).encode('utf-8'),
            headers=headers if headers is not None else {
                'Content-Type': 'application/json', 'X-Editor-Token': self.token})
        try:
            response = urllib.request.urlopen(request, timeout=40)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            result = json.load(response)
            if result.get('adventure'):
                adventure = result['adventure']
                self.sessions[adventure['sessionId']] = adventure
            return response.status, result

    def ok(self, data):
        status, result = self.request(data)
        self.assertEqual(200, status, result)
        self.assertTrue(result.get('success'), result)
        return result

    def catalog(self):
        return self.ok({'op': 'catalog', 'sessionId': str(uuid.uuid4())})

    def start(self, **changes):
        catalog = self.catalog()
        data = {
            'op': 'start', 'sessionId': str(uuid.uuid4()), 'seed': 'adventure-http-1',
            'characterId': catalog['characters'][0]['id'],
            'tarotId': catalog['tarots'][0]['id'],
        }
        data.update(changes)
        return self.ok(data)

    def current(self, result):
        return self.ok({'op': 'state', 'sessionId': result['adventure']['sessionId']})

    def command(self, result, action, **fields):
        adventure = result['adventure']
        data = {'op': 'command', 'sessionId': adventure['sessionId'],
                'runId': adventure['runId'], 'revision': adventure['revision'],
                'action': action}
        data.update(fields)
        return self.request(data)[1]

    def accepted(self, result):
        self.assertTrue(result.get('success'), result)
        return result

    def short_route(self, first='Battle'):
        """A normal configured route; every victory still requires real card play."""
        catalog = self.catalog()
        config = ET.fromstring(catalog['configXml'])
        encounter = config.attrib['startingEncounter']
        nodes = config.find('Nodes')
        nodes.clear()
        route = ([('fight', 'Battle'), ('shop', 'Shop'), ('rest', 'Rest'), ('boss', 'Boss')]
                 if first == 'Battle' else [('shop', 'Shop'), ('rest', 'Rest'), ('boss', 'Boss')])
        config.set('startNode', route[0][0])
        config.set('startingGold', '10000')
        for index, (node_id, kind) in enumerate(route):
            attributes = {'id': node_id, 'name': kind, 'kind': kind,
                          'chapter': '1', 'row': str(index), 'column': '0'}
            if index + 1 < len(route):
                attributes['next'] = route[index + 1][0]
            if kind in ['Battle', 'Boss']:
                attributes.update(encounter=encounter, gold='25')
            ET.SubElement(nodes, 'Node', **attributes)
        battle = ET.fromstring(catalog['battleXml'])
        for enemy in battle.findall('Enemies/Enemy'):
            enemy.set('maxHealth', '1')
        return self.start(configXml=ET.tostring(config, encoding='unicode'),
                          battleXml=ET.tostring(battle, encoding='unicode'))

    def battle_action(self, result, action, selected=None, preview=False, **extra):
        adventure = result['adventure']
        battle = result['battle']
        request = {'op': 'preview' if preview else 'battle',
                   'sessionId': adventure['sessionId'], 'runId': adventure['runId'],
                   'revision': adventure['revision'], 'battleRevision': battle['revision'],
                   'actor': battle['players'][0]['id'], 'action': action,
                   'selected': selected or [], 'targetBindings': {}}
        request.update(extra)
        return self.request(request)[1]

    def win_battle(self, result):
        self.assertEqual('Battle', result['adventure']['phase'])
        for _ in range(40):
            if result['adventure']['phase'] != 'Battle':
                break
            player = result['battle']['players'][0]
            if player['window'] == 'Draw':
                result = self.accepted(self.battle_action(result, 'SkipDraw'))
            elif player['window'] == 'Ending':
                result = self.accepted(self.battle_action(result, 'FinishTurn'))
            else:
                playable = [card for card in player['hand'] if card['canPlay']]
                if playable and int(player['energy']) > 0:
                    result = self.accepted(self.battle_action(result, 'Play', [playable[0]['id']]))
                else:
                    result = self.accepted(self.battle_action(result, 'EndTurn'))
        self.assertNotEqual('Battle', result['adventure']['phase'], result)
        return result

    def test_endpoint_requires_editor_token_and_same_origin(self):
        for headers in [
            {'Content-Type': 'application/json'},
            {'Content-Type': 'application/json', 'X-Editor-Token': self.token,
             'Origin': 'https://unrelated.invalid'},
            {'Content-Type': 'application/json', 'X-Editor-Token': self.token,
             'Host': 'unrelated.invalid'},
        ]:
            self.assertEqual(403, self.request({'op': 'catalog'}, headers=headers)[0])

    def test_invalid_transport_is_rejected(self):
        self.assertEqual(400, self.request('{', raw=True)[0])
        self.assertEqual(400, self.request('[]', raw=True)[0])
        self.assertEqual(415, self.request({'op': 'catalog'}, headers={
            'Content-Type': 'text/plain', 'X-Editor-Token': self.token})[0])

    def test_catalog_has_valid_default_xml_and_explicit_character_tarot_choices(self):
        catalog = self.catalog()
        self.assertTrue(catalog['characters'])
        self.assertTrue(catalog['tarots'])
        self.assertTrue(ET.fromstring(catalog['configXml']).tag)
        self.assertEqual('BattleContent', ET.fromstring(catalog['battleXml']).tag)
        for entries in [catalog['characters'], catalog['tarots']]:
            ids = [entry['id'] for entry in entries]
            self.assertEqual(len(ids), len(set(ids)))
            self.assertTrue(all(ids))
        adventure = self.start()['adventure']
        self.assertEqual(3, adventure['chapterCount'])
        self.assertEqual(1, sum(node['state'] == 'available' for node in adventure['nodes']))
        for node in adventure['nodes']:
            self.assertNotIn('encounter', node)
            self.assertNotIn('encounterId', node)
            self.assertNotIn('enemy', node)
            self.assertNotIn('enemies', node)

    def test_catalog_preserves_custom_content_and_progression_after_restore(self):
        config = ET.fromstring(self.catalog()['configXml'])
        config.set('version', 'adventure.http-custom.1')
        config.find('Tarots/Tarot').set('name', '自定义初始塔罗')
        result = self.start(configXml=ET.tostring(config, encoding='unicode'))
        for request in [
            {'op': 'catalog', 'sessionId': result['adventure']['sessionId']},
            {'op': 'catalog', 'sessionId': str(uuid.uuid4()), 'save': result['save']},
        ]:
            catalog = self.ok(request)
            self.assertEqual('adventure.http-custom.1', catalog['version'])
            self.assertEqual('自定义初始塔罗', catalog['tarots'][0]['name'])
            self.assertEqual('adventure.http-custom.1', ET.fromstring(catalog['configXml']).attrib['version'])
            self.assertEqual((server.ROOT / 'Engine/Content/StandardAdventure.xml').read_text(encoding='utf-8'),
                             catalog['standardConfigXml'])
            self.assertEqual((server.ROOT / 'Engine/Content/StandardSingleBattle.xml').read_text(encoding='utf-8'),
                             catalog['standardBattleXml'])

    def test_invalid_selection_cannot_start(self):
        catalog = self.catalog()
        for field, value in [('characterId', ''), ('characterId', 'missing'),
                             ('tarotId', ''), ('tarotId', 'missing')]:
            data = {'op': 'start', 'sessionId': str(uuid.uuid4()), 'seed': 'selection',
                    'characterId': catalog['characters'][0]['id'],
                    'tarotId': catalog['tarots'][0]['id']}
            data[field] = value
            status, result = self.request(data)
            self.assertEqual(200, status, result)
            self.assertFalse(result.get('success'), result)

    def test_invalid_configuration_does_not_replace_existing_run(self):
        result = self.start()
        adventure = result['adventure']
        for field in ['configXml', 'battleXml']:
            status, invalid = self.request({
                'op': 'start', 'sessionId': adventure['sessionId'],
                'runId': adventure['runId'], 'seed': 'invalid',
                'characterId': self.catalog()['characters'][0]['id'],
                'tarotId': self.catalog()['tarots'][0]['id'], field: '<broken>'})
            self.assertEqual(200, status, invalid)
            self.assertFalse(invalid.get('success'), invalid)
            self.assertEqual(adventure, self.current(result)['adventure'])

    def test_unknown_actions_cannot_claim_victory_or_mutate_resources(self):
        result = self.start()
        before = copy.deepcopy(result['adventure'])
        for action in ['win', 'complete', 'finishBattle', 'setHealth', 'skip']:
            rejected = self.command(result, action, health=999999, gold=999999,
                                    winner='player', battleWon=True)
            self.assertFalse(rejected.get('success'), rejected)
            self.assertEqual(before, self.current(result)['adventure'])

    def test_invalid_restore_is_atomic(self):
        result = self.start()
        for save in ['', '<broken>', '{"version":999999}', result['save'] + 'broken']:
            status, rejected = self.request({
                'op': 'restore', 'sessionId': result['adventure']['sessionId'],
                'runId': result['adventure']['runId'], 'save': save})
            self.assertEqual(200, status, rejected)
            self.assertFalse(rejected.get('success'), rejected)
            self.assertEqual(result['adventure'], self.current(result)['adventure'])

    def test_map_only_accepts_reachable_nodes(self):
        result = self.short_route()
        for node in ['missing', 'boss', 'shop', 'rest']:
            rejected = self.command(result, 'enter', nodeId=node)
            self.assertFalse(rejected.get('success'), rejected)
            self.assertEqual(result['adventure'], self.current(result)['adventure'])
        result = self.accepted(self.command(result, 'enter', nodeId='fight'))
        self.assertEqual('Battle', result['adventure']['phase'])
        self.assertIsNotNone(result['battle'])
        rejected = self.command(result, 'continue', health=999999, gold=999999, victory=True)
        self.assertFalse(rejected.get('success'), rejected)
        self.assertEqual(result['adventure'], self.current(result)['adventure'])

    def test_battle_preview_and_stale_command_preserve_authoritative_state(self):
        result = self.accepted(self.command(self.short_route(), 'enter', nodeId='fight'))
        for action in ['Start', 'EnemyAction', 'SkipPlayerTurn', 'CollectPlayed']:
            rejected = self.battle_action(result, action)
            self.assertFalse(rejected.get('success'), rejected)
            self.assertEqual(result['battle'], self.current(result)['battle'])
        preview = self.accepted(self.battle_action(result, 'SkipDraw', preview=True))
        self.assertTrue(preview['preview']['legal'], preview)
        current = self.current(result)
        self.assertEqual(result['adventure'], current['adventure'])
        self.assertEqual(result['battle'], current['battle'])
        progressed = self.accepted(self.battle_action(result, 'SkipDraw'))
        stale = self.battle_action(result, 'SkipDraw')
        self.assertFalse(stale.get('success'), stale)
        self.assertEqual(progressed['adventure'], self.current(progressed)['adventure'])
        self.assertEqual(progressed['battle'], self.current(progressed)['battle'])

    def test_real_battle_win_unlocks_shop_and_rest_requires_choice(self):
        result = self.accepted(self.command(self.short_route(), 'enter', nodeId='fight'))
        result = self.win_battle(result)
        self.assertEqual('Reward', result['adventure']['phase'])
        result = self.accepted(self.command(result, 'continue'))
        self.assertEqual('Map', result['adventure']['phase'])
        result = self.accepted(self.command(result, 'enter', nodeId='shop'))
        self.assertEqual('Shop', result['adventure']['phase'])
        result = self.accepted(self.command(result, 'continue'))
        result = self.accepted(self.command(result, 'enter', nodeId='rest'))
        self.assertEqual('Rest', result['adventure']['phase'])
        rejected = self.command(result, 'continue')
        self.assertFalse(rejected.get('success'), rejected)
        self.assertEqual(result['adventure'], self.current(result)['adventure'])
        option = next(option for option in result['adventure']['rest']['options'] if option['enabled'])
        result = self.accepted(self.command(result, 'rest', optionId=option['id']))
        self.assertTrue(result['adventure']['canContinue'])
        rejected = self.command(result, 'rest', optionId=option['id'])
        self.assertFalse(rejected.get('success'), rejected)
        result = self.accepted(self.command(result, 'continue'))
        result = self.accepted(self.command(result, 'enter', nodeId='boss'))
        result = self.win_battle(result)
        self.assertEqual('Reward', result['adventure']['phase'])
        result = self.accepted(self.command(result, 'continue'))
        self.assertEqual('Completed', result['adventure']['phase'])
        self.assertEqual(1, result['adventure']['progression']['wins'])
        self.assertFalse(self.command(result, 'continue').get('success'))
        self.assertEqual(1, self.current(result)['adventure']['progression']['wins'])
        restored = self.ok({'op': 'restore', 'sessionId': str(uuid.uuid4()), 'save': result['save']})
        unlocked = next(tarot for tarot in restored['adventure']['tarots']
                        if tarot['unlocked'] and tarot['id'] != restored['adventure']['tarot']['id'])
        continued = self.ok({
            'op': 'start', 'sessionId': restored['adventure']['sessionId'],
            'runId': restored['adventure']['runId'], 'seed': 'next-adventure',
            'characterId': restored['adventure']['character']['id'], 'tarotId': unlocked['id']})
        self.assertEqual('Map', continued['adventure']['phase'])
        self.assertEqual(1, continued['adventure']['progression']['wins'])
        self.assertEqual(unlocked['id'], continued['adventure']['tarot']['id'])

    def test_shop_changes_permanent_deck_prices_and_owned_relics(self):
        result = self.accepted(self.command(self.short_route(first='Shop'), 'enter', nodeId='shop'))
        before = result['adventure']
        card = before['deck'][0]
        duplicate = next(service for service in before['shop']['services'] if service['id'] == 'duplicate')
        result = self.accepted(self.command(result, 'service', itemId='duplicate', cardId=card['id']))
        after = result['adventure']
        self.assertEqual(len(before['deck']) + 1, len(after['deck']))
        self.assertEqual(int(before['player']['gold']) - int(duplicate['price']), int(after['player']['gold']))
        next_duplicate = next(service for service in after['shop']['services'] if service['id'] == 'duplicate')
        self.assertEqual(int(duplicate['price']) + int(duplicate['increase']), int(next_duplicate['price']))
        stale = self.command({'adventure': before}, 'service', itemId='duplicate', cardId=card['id'])
        self.assertFalse(stale.get('success'), stale)
        self.assertEqual(after, self.current(result)['adventure'])
        suits = ['Spades', 'Hearts', 'Clubs', 'Diamonds']
        suit = next(suit for suit in suits if suit != card['suit'])
        result = self.accepted(self.command(result, 'service', itemId='suit', cardId=card['id'], suit=suit))
        changed = next(value for value in result['adventure']['deck'] if value['id'] == card['id'])
        self.assertEqual((card['rank'], suit), (changed['rank'], changed['suit']))
        before = result['adventure']
        result = self.accepted(self.command(result, 'service', itemId='delete', cardId=card['id']))
        self.assertEqual(len(before['deck']) - 1, len(result['adventure']['deck']))
        self.assertNotIn(card['id'], [value['id'] for value in result['adventure']['deck']])
        item = next(item for item in result['adventure']['shop']['items'] if item['canBuy'])
        before = result['adventure']
        result = self.accepted(self.command(result, 'buy', itemId=item['id']))
        self.assertEqual(int(before['player']['gold']) - int(item['price']),
                         int(result['adventure']['player']['gold']))
        self.assertFalse(self.command(result, 'buy', itemId=item['id']).get('success'))
        owned = next(relic for relic in result['adventure']['relics']
                     if relic['id'] not in [old['id'] for old in before['relics']])
        before = result['adventure']
        result = self.accepted(self.command(result, 'sell', itemId=owned['id']))
        self.assertEqual(int(before['player']['gold']) + int(owned['sellPrice']),
                         int(result['adventure']['player']['gold']))
        self.assertNotIn(owned['id'], [relic['id'] for relic in result['adventure']['relics']])

    def test_active_battle_save_restores_and_continues_deterministically(self):
        result = self.accepted(self.command(self.short_route(), 'enter', nodeId='fight'))
        result = self.accepted(self.battle_action(result, 'SkipDraw'))
        restored = self.ok({'op': 'restore', 'sessionId': str(uuid.uuid4()), 'save': result['save']})

        def gameplay(value):
            value = copy.deepcopy(value)
            value.pop('sessionId', None)
            value.pop('runId', None)
            value.pop('seed', None)
            if value.get('battle'):
                value['battle'] = gameplay(value['battle'])
            return value

        self.assertEqual(gameplay(result['adventure']), gameplay(restored['adventure']))
        selected = [result['battle']['players'][0]['hand'][0]['id']]
        original_after = self.accepted(self.battle_action(result, 'Play', selected))
        restored_after = self.accepted(self.battle_action(restored, 'Play', selected))
        self.assertEqual(gameplay(original_after['adventure']), gameplay(restored_after['adventure']))
        self.assertEqual(gameplay(original_after['battle']), gameplay(restored_after['battle']))
        damaged_save = json.loads(restored_after['save'])
        state = json.loads(damaged_save['state'])
        state['Revision'] += 1
        damaged_save['state'] = json.dumps(state)
        rejected = self.request({
            'op': 'restore', 'sessionId': restored_after['adventure']['sessionId'],
            'runId': restored_after['adventure']['runId'], 'save': json.dumps(damaged_save)})[1]
        self.assertFalse(rejected.get('success'), rejected)
        self.assertEqual(restored_after['adventure'], self.current(restored_after)['adventure'])

    def test_close_requires_current_identity_and_revision_and_keeps_save_recoverable(self):
        original = self.short_route()
        result = self.accepted(self.command(original, 'enter', nodeId='fight'))
        adventure = result['adventure']
        for run_id, revision in [
            (str(uuid.uuid4()), adventure['revision']),
            (adventure['runId'], original['adventure']['revision']),
        ]:
            rejected = self.request({
                'op': 'close', 'sessionId': adventure['sessionId'],
                'runId': run_id, 'revision': revision})[1]
            self.assertFalse(rejected.get('success'), rejected)
            self.assertEqual(adventure, self.current(result)['adventure'])
        closed = self.ok({
            'op': 'close', 'sessionId': adventure['sessionId'],
            'runId': adventure['runId'], 'revision': adventure['revision']})
        self.assertTrue(closed['closed'])
        missing = self.request({'op': 'state', 'sessionId': adventure['sessionId']})[1]
        self.assertFalse(missing.get('success'), missing)
        self.assertEqual('SessionNotFound', missing['error'])
        restored = self.ok({'op': 'restore', 'sessionId': adventure['sessionId'], 'save': result['save']})
        self.assertEqual(adventure['phase'], restored['adventure']['phase'])
        self.assertEqual(adventure['player'], restored['adventure']['player'])
        self.assertEqual(adventure['revision'], restored['adventure']['revision'])
        self.assertEqual(result['battle']['players'], restored['battle']['players'])

    def test_cross_chapter_continue_exposes_next_chapter_on_map(self):
        catalog = self.catalog()
        config = ET.fromstring(catalog['configXml'])
        encounter = config.attrib['startingEncounter']
        config.set('startNode', 'chapter-one-boss')
        nodes = config.find('Nodes')
        nodes.clear()
        ET.SubElement(nodes, 'Node', id='chapter-one-boss', kind='Boss', chapter='1',
                      encounter=encounter, next='chapter-two-boss')
        ET.SubElement(nodes, 'Node', id='chapter-two-boss', kind='Boss', chapter='2', encounter=encounter)
        battle = ET.fromstring(catalog['battleXml'])
        for enemy in battle.findall('Enemies/Enemy'):
            enemy.set('maxHealth', '1')
        result = self.start(configXml=ET.tostring(config, encoding='unicode'),
                            battleXml=ET.tostring(battle, encoding='unicode'))
        result = self.accepted(self.command(result, 'enter', nodeId='chapter-one-boss'))
        result = self.win_battle(result)
        self.assertEqual(1, result['adventure']['chapter'])
        result = self.accepted(self.command(result, 'continue'))
        self.assertEqual('Map', result['adventure']['phase'])
        self.assertEqual(2, result['adventure']['chapter'])
        self.assertEqual(1, result['adventure']['completedChapters'])
        self.assertEqual(['chapter-two-boss'], [
            node['id'] for node in result['adventure']['nodes'] if node['state'] == 'available'])
        result = self.accepted(self.command(result, 'enter', nodeId='chapter-two-boss'))
        result = self.win_battle(result)
        result = self.accepted(self.command(result, 'continue'))
        self.assertEqual('Completed', result['adventure']['phase'])
        self.assertEqual(2, result['adventure']['completedChapters'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
