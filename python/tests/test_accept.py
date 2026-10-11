"""Offline tests of the live acceptance runner and its cases against a fake endpoint (no game needed)."""
import json, os, sys, threading, unittest
from http.server import ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(HERE, '..')); sys.path.insert(0, os.path.join(HERE, '..', 'live'))
import hubner  # noqa: E402
import accept  # noqa: E402
import cases  # noqa: E402
from test_client import Fake  # noqa: E402


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.client = hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp')
        self.fake.replies.update({'health': {'ok': True, 'inGame': True}, 'world_state': {'world': 'Test'}, 'ext_status': {'droppedCalls': 0, 'mainThreadStallReports': 0}, 'status': {'journalError': ''}, 'plans': {'plans': []}})
        self.ctx = accept.Ctx(self.client)

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def run_one(self, case, **kw):
        return accept.run_all([case], self.ctx, guard=False, **kw)[0]

    def test_a_case_that_checks_and_holds_passes(self):
        r = self.run_one(accept.Case('a', 'doc', lambda c: c.check('x', True)))
        self.assertEqual(r['verdict'], accept.PASS)

    def test_a_failed_check_fails_the_case_and_says_which(self):
        r = self.run_one(accept.Case('a', 'doc', lambda c: c.check('the thing', False, 'it was 3')))
        self.assertEqual(r['verdict'], accept.FAIL); self.assertIn('the thing: it was 3', r['reason'])

    def test_a_case_that_asserts_nothing_fails(self):
        self.assertEqual(self.run_one(accept.Case('a', 'doc', lambda c: None))['verdict'], accept.FAIL)

    def test_a_case_that_cannot_run_here_is_skipped_with_the_reason_never_failed(self):
        def run(c): raise accept.Skip('no bed in this world')
        r = self.run_one(accept.Case('a', 'doc', run)); self.assertEqual((r['verdict'], r['reason']), (accept.SKIPPED, 'no bed in this world'))
        r = self.run_one(accept.Case('b', 'doc', lambda c: c.check('x', True), pre=lambda c: 'needs a chest')); self.assertEqual((r['verdict'], r['reason']), (accept.SKIPPED, 'needs a chest'))

    def test_an_exception_is_a_failure_with_its_type(self):
        def run(c): raise KeyError('rules')
        r = self.run_one(accept.Case('a', 'doc', run)); self.assertEqual(r['verdict'], accept.FAIL); self.assertIn('KeyError', r['reason'])

    def test_a_dry_run_skips_acting_cases_but_runs_the_reading_ones(self):
        acted = []
        acts = accept.Case('acts', 'doc', lambda c: acted.append(1) or c.check('x', True), acts=True)
        reads = accept.Case('reads', 'doc', lambda c: c.check('x', True))
        rs = accept.run_all([acts, reads], self.ctx, dry_run=True, guard=False)
        self.assertEqual([r['verdict'] for r in rs], [accept.SKIPPED, accept.PASS]); self.assertEqual(acted, [])

    def test_a_dry_run_still_checks_the_preconditions_of_acting_cases(self):
        r = self.run_one(accept.Case('acts', 'doc', lambda c: c.check('x', True), acts=True, pre=lambda c: 'no quiet spot'), dry_run=True)
        self.assertEqual(r['reason'], 'no quiet spot')

    def test_an_acting_case_that_makes_the_extension_drop_calls_fails(self):
        state = {'n': 0}
        def status(a): state['n'] += 1; return {'droppedCalls': 0 if state['n'] <= 1 else 2, 'mainThreadStallReports': 0}
        self.fake.replies['ext_status'] = status
        r = self.run_one(accept.Case('acts', 'doc', lambda c: c.check('x', True), acts=True))
        self.assertEqual(r['verdict'], accept.FAIL); self.assertIn('no calls dropped', r['reason'])

    def test_an_acting_case_that_leaves_a_plan_behind_fails(self):
        seq = iter([{'plans': []}, {'plans': []}, {'plans': [{'plan': 'x'}]}, {'plans': [{'plan': 'x'}]}])
        self.fake.replies['plans'] = lambda a: next(seq)
        r = self.run_one(accept.Case('acts', 'doc', lambda c: c.check('x', True), acts=True))
        self.assertEqual(r['verdict'], accept.FAIL); self.assertIn('plans as before', r['reason'])

    def test_cleanup_runs_even_when_the_case_fails(self):
        cleaned = []
        def run(c): raise RuntimeError('boom')
        r = self.run_one(accept.Case('a', 'doc', run, clean=lambda c: cleaned.append(1)))
        self.assertEqual(r['verdict'], accept.FAIL); self.assertEqual(cleaned, [1])

    def test_only_selects_by_exact_key_or_prefix(self):
        cs = [accept.Case('plan-check-tower', '', None), accept.Case('plan-rules', '', None), accept.Case('smoke', '', None)]
        self.assertEqual([c.key for c in accept.select(cs, 'plan')], ['plan-check-tower', 'plan-rules'])
        self.assertEqual([c.key for c in accept.select(cs, 'smoke,plan-rules')], ['plan-rules', 'smoke'])
        self.assertEqual(len(accept.select(cs, None)), 3)

    def test_the_guard_blocks_every_case_when_the_world_is_not_on_the_list(self):
        rs = accept.run_all([accept.Case('a', 'd', lambda c: c.check('x', True))], self.ctx, worlds=['Other'])
        self.assertEqual(rs[0]['verdict'], accept.BLOCKED); self.assertIn("'Test' is not on the allow-list", rs[0]['reason'])

    def test_the_guard_passes_a_listed_world(self):
        rs = accept.run_all([accept.Case('a', 'd', lambda c: c.check('x', True))], self.ctx, worlds=['Test'])
        self.assertEqual(rs[0]['verdict'], accept.PASS)

    def test_the_text_report_ends_with_all_ok_or_the_failure_count(self):
        ok = accept.render_text([{'key': 'a', 'verdict': 'pass', 'reason': ''}])
        self.assertTrue(ok.rstrip().endswith('pass 1, fail 0, skipped 0, blocked 0')); self.assertIn('== ALL OK ==', ok)
        bad = accept.render_text([{'key': 'a', 'verdict': 'fail', 'reason': 'x'}, {'key': 'b', 'verdict': 'blocked', 'reason': 'y'}])
        self.assertIn('== 2 FAILED ==', bad)


class GuardTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.url = f'http://127.0.0.1:{self.srv.server_address[1]}/mcp'

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def test_refuses_without_an_explicit_endpoint(self):
        old = os.environ.pop('HUBNER_URL', None)
        try:
            with self.assertRaisesRegex(hubner.GuardError, 'not explicit'): hubner.live_guard(hubner.Client(), worlds=['x'])
        finally:
            if old is not None: os.environ['HUBNER_URL'] = old

    def test_refuses_without_an_allow_list(self):
        old = os.environ.pop('HUBNER_LIVE_WORLDS', None)
        try:
            with self.assertRaisesRegex(hubner.GuardError, 'allow-list'): hubner.live_guard(hubner.Client(self.url))
        finally:
            if old is not None: os.environ['HUBNER_LIVE_WORLDS'] = old

    def test_refuses_when_the_game_is_not_in_a_world(self):
        self.fake.replies['health'] = {'ok': True, 'inGame': False}
        with self.assertRaisesRegex(hubner.GuardError, 'not in a world'): hubner.live_guard(hubner.Client(self.url), worlds=['x'])

    def test_returns_the_world_name_when_all_three_hold(self):
        self.fake.replies.update({'health': {'ok': True, 'inGame': True}, 'world_state': {'world': 'Test'}})
        self.assertEqual(hubner.live_guard(hubner.Client(self.url), worlds=['Test', 'Other']), 'Test')


class CaseTableTests(unittest.TestCase):
    def test_keys_are_unique_and_every_case_says_what_it_proves(self):
        keys = [c.key for c in cases.CASES]
        self.assertEqual(len(keys), len(set(keys)))
        self.assertTrue(all(c.doc.strip() for c in cases.CASES))

    def test_acting_cases_clean_up(self):
        for c in cases.CASES:
            if c.acts: self.assertIsNotNone(c.clean, c.key); self.assertIsNotNone(c.pre, c.key)


class CaseOnFakeTests(unittest.TestCase):
    """A few real cases against canned answers: they pass on a right answer and fail on a wrong one."""
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.ctx = accept.Ctx(hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp'))

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def case(self, key): return [c for c in cases.CASES if c.key == key][0]

    def test_plan_rules_passes_on_the_real_shape_and_fails_when_an_origin_is_missing(self):
        good = {'rules': [{'id': 'a', 'origin': 'hard', 'code': 'PrivateZone'}, {'id': 'b', 'origin': 'ghost', 'code': 'MoreSpace'}, {'id': 'c', 'origin': 'taste', 'code': ''}, {'id': 'd', 'origin': 'ghost', 'code': 'NoBuildZone'}, {'id': 'e', 'origin': 'hard', 'code': 'ExtensionMissingStation'}]}
        self.fake.replies['plan_rules'] = good
        self.assertEqual(accept.run_case(self.case('plan-rules'), self.ctx)['verdict'], accept.PASS)
        self.fake.replies['plan_rules'] = {'rules': good['rules'][:2]}
        self.assertEqual(accept.run_case(self.case('plan-rules'), self.ctx)['verdict'], accept.FAIL)

    def test_chat_tags_is_skipped_on_an_empty_chat_not_passed(self):
        self.fake.replies['chat_tail'] = {'last': 0, 'count': 0, 'messages': []}
        r = accept.run_case(self.case('chat-tags'), self.ctx)
        self.assertEqual(r['verdict'], accept.SKIPPED)

    def test_chat_tags_fails_on_a_row_without_the_new_fields(self):
        self.fake.replies['chat_tail'] = {'messages': [{'trust': 'other', 'hints': []}]}
        self.assertEqual(accept.run_case(self.case('chat-tags'), self.ctx)['verdict'], accept.FAIL)

    def test_bed_check_is_skipped_when_there_is_no_bed(self):
        self.fake.replies['bed_check'] = {'beds': 0, 'rows': []}
        self.assertEqual(accept.run_case(self.case('bed-check'), self.ctx)['verdict'], accept.SKIPPED)

    def test_bed_check_catches_a_bed_that_claims_it_can_sleep_without_a_fire(self):
        row = {'pos': [0, 0, 0], 'exposureOk': True, 'heat': False, 'canSleep': True, 'canClaim': True, 'firstFailure': None}
        self.fake.replies['bed_check'] = {'beds': 1, 'rows': [row]}
        self.assertEqual(accept.run_case(self.case('bed-check'), self.ctx)['verdict'], accept.FAIL)

    def test_the_tower_case_is_skipped_when_the_prefab_is_missing(self):
        self.fake.replies['prefab_search'] = {'names': []}
        self.assertEqual(accept.run_case(self.case('plan-check-tower'), self.ctx)['verdict'], accept.SKIPPED)

    def test_the_acting_cases_are_skipped_without_a_test_spot(self):
        self.ctx.test_xz = (0.0, 0.0)
        self.assertEqual(accept.run_case(self.case('remove-collapse'), self.ctx)['verdict'], accept.SKIPPED)


if __name__ == '__main__':
    unittest.main()


class WorldClockCaseTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.ctx = accept.Ctx(hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp'))
        self.case = [c for c in cases.CASES if c.key == 'world-clock'][0]

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    GOOD = {'dayFraction': 0.9, 'isNight': True, 'isAfternoon': False, 'secondsToDusk': 1500, 'secondsToDawn': 400, 'dayLengthSec': 1800, 'sleepWindow': True, 'characters': 2, 'inBed': 1, 'notInBed': ['Freya'], 'nightSkips': False}

    def test_passes_on_a_consistent_clock(self):
        self.fake.replies['world_clock'] = self.GOOD; self.fake.replies['env_state'] = {'dayFraction': 0.9}
        self.assertEqual(accept.run_case(self.case, self.ctx)['verdict'], accept.PASS)

    def test_fails_when_the_night_skips_with_somebody_out_of_bed(self):
        self.fake.replies['world_clock'] = dict(self.GOOD, nightSkips=True); self.fake.replies['env_state'] = {'dayFraction': 0.9}
        self.assertEqual(accept.run_case(self.case, self.ctx)['verdict'], accept.FAIL)

    def test_fails_when_the_client_sees_another_time(self):
        self.fake.replies['world_clock'] = self.GOOD; self.fake.replies['env_state'] = {'dayFraction': 0.4}
        self.assertEqual(accept.run_case(self.case, self.ctx)['verdict'], accept.FAIL)


class RecipeCaseTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.ctx = accept.Ctx(hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp')); self.ctx.test_xz = (10.0, 10.0)

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def case(self, key): return [c for c in cases.CASES if c.key == key][0]

    BRONZE = {'count': 1, 'recipes': [{'item': 'Bronze', 'makes': 2, 'station': 'forge', 'stationLevel': 1, 'upgradeOnly': False, 'ingredients': [{'item': 'Copper', 'amount': 2}, {'item': 'Tin', 'amount': 1}]}]}

    def test_missing_for_passes_when_the_numbers_follow_the_recipe(self):
        self.fake.replies['recipe_info'] = self.BRONZE
        self.fake.replies['missing_for'] = {'chests': 0, 'complete': False, 'steps': ['Bronze x6: 3 crafts at forge level 1 from 6 Copper + 3 Tin'], 'missing': {'Copper': 6, 'Tin': 3}, 'stations': {'forge': 1}}
        self.assertEqual(accept.run_case(self.case('missing-for'), self.ctx)['verdict'], accept.PASS)

    def test_missing_for_fails_when_a_craft_is_not_rounded_up(self):
        self.fake.replies['recipe_info'] = self.BRONZE
        self.fake.replies['missing_for'] = {'chests': 0, 'complete': False, 'steps': ['Bronze x5'], 'missing': {'Copper': 5, 'Tin': 3}, 'stations': {'forge': 1}}
        self.assertEqual(accept.run_case(self.case('missing-for'), self.ctx)['verdict'], accept.FAIL)

    def test_missing_for_is_skipped_when_there_is_stock_at_the_spot(self):
        self.fake.replies['recipe_info'] = self.BRONZE
        self.fake.replies['missing_for'] = {'chests': 2, 'complete': True, 'steps': [], 'missing': {}, 'stations': {}}
        self.assertEqual(accept.run_case(self.case('missing-for'), self.ctx)['verdict'], accept.SKIPPED)

    def test_plan_materials_fails_when_the_sum_does_not_scale(self):
        self.fake.replies['prefab_search'] = {'names': ['wood_wall']}
        seq = iter([{'materials': {'Wood': 2}, 'unknownPrefabs': []}, {'materials': {'Wood': 5}, 'unknownPrefabs': []}, {'materials': {}, 'unknownPrefabs': ['no_such_piece']}])
        self.fake.replies['plan_materials'] = lambda a: next(seq)
        self.assertEqual(accept.run_case(self.case('plan-materials'), self.ctx)['verdict'], accept.FAIL)


class CaptureSceneTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.client = hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp')

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def test_a_scene_is_saved_next_to_the_unit_tests_fixtures(self):
        import tempfile, capture_scene
        self.fake.replies['scene_capture'] = {'name': 'x', 'objects': 3}
        self.fake.replies['scene_get'] = json.dumps({'schema': 'hubner.scene/1', 'name': 'x'})
        with tempfile.TemporaryDirectory() as d:
            info, path = capture_scene.capture(self.client, 'x', 1, 2, 10, 'note', dest=d)
            self.assertEqual(info['objects'], 3)
            self.assertTrue(path.endswith('x.scene.json')); self.assertEqual(json.load(open(path))['schema'], 'hubner.scene/1')
        self.assertEqual([c for c in self.fake.calls if c[0] == 'scene_capture'][0][1], {'name': 'x', 'x': 1, 'z': 2, 'radius': 10, 'note': 'note'})

    def test_something_that_is_not_json_is_not_saved(self):
        import tempfile, capture_scene
        self.fake.replies['scene_capture'] = {'name': 'x'}; self.fake.replies['scene_get'] = 'not json {'
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(ValueError): capture_scene.capture(self.client, 'x', 1, 2, dest=d)
            self.assertEqual(os.listdir(d), [])


class SceneCaseTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.ctx = accept.Ctx(hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp')); self.ctx.test_xz = (5.0, 5.0)
        self.case = [c for c in cases.CASES if c.key == 'scene-capture'][0]

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def scene(self, n_objects=2, h=4):
        return json.dumps({'schema': 'hubner.scene/1', 'prefabs': {'p': {}}, 'objects': [{'prefab': 'p'}] * n_objects, 'terrain': {'nx': 2, 'nz': 2, 'h': [0] * h, 'source': 's'}})

    def test_passes_on_a_consistent_scene(self):
        self.fake.replies['scene_capture'] = {'objects': 2, 'withRecordedSupport': 1}; self.fake.replies['scene_get'] = self.scene()
        self.assertEqual(accept.run_case(self.case, self.ctx)['verdict'], accept.PASS)

    def test_fails_when_objects_are_lost_between_capture_and_get(self):
        self.fake.replies['scene_capture'] = {'objects': 3, 'withRecordedSupport': 1}; self.fake.replies['scene_get'] = self.scene()
        self.assertEqual(accept.run_case(self.case, self.ctx)['verdict'], accept.FAIL)

    def test_fails_on_a_short_terrain_grid(self):
        self.fake.replies['scene_capture'] = {'objects': 2, 'withRecordedSupport': 1}; self.fake.replies['scene_get'] = self.scene(h=3)
        self.assertEqual(accept.run_case(self.case, self.ctx)['verdict'], accept.FAIL)

    def test_is_skipped_when_nothing_is_there(self):
        self.fake.replies['scene_capture'] = {'objects': 0}
        self.assertEqual(accept.run_case(self.case, self.ctx)['verdict'], accept.SKIPPED)


class PlantCaseTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.ctx = accept.Ctx(hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp')); self.ctx.test_xz = (5.0, 5.0)
        self.fake.replies['prefab_search'] = {'names': ['sapling_carrot']}

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def case(self, key): return [c for c in cases.CASES if c.key == key][0]

    def rows(self, *statuses): return {'plants': [{'status': s} for s in statuses], 'errors': ['item 1: wood_wall is not a plant']}

    def test_passes_when_close_carrots_block_and_far_ones_do_not(self):
        seq = iter([self.rows('Healthy', 'Healthy'), self.rows('NoSpace', 'NoSpace'), self.rows()])
        self.fake.replies['plant_check'] = lambda a: next(seq)
        self.assertEqual(accept.run_case(self.case('plant-check'), self.ctx)['verdict'], accept.PASS)

    def test_fails_when_close_carrots_do_not_block(self):
        seq = iter([self.rows('Healthy', 'Healthy'), self.rows('Healthy', 'Healthy'), self.rows()])
        self.fake.replies['plant_check'] = lambda a: next(seq)
        self.assertEqual(accept.run_case(self.case('plant-check'), self.ctx)['verdict'], accept.FAIL)

    def test_is_skipped_on_ground_the_carrot_cannot_use(self):
        self.fake.replies['plant_check'] = lambda a: self.rows('NotCultivated', 'NotCultivated')
        self.assertEqual(accept.run_case(self.case('plant-check'), self.ctx)['verdict'], accept.SKIPPED)

    def test_farm_state_fails_when_a_plant_is_ready_before_it_could_be(self):
        self.fake.replies['farm_state'] = {'total': 1, 'plants': [{'prefab': 'x', 'readyFromSeconds': 500, 'readyBySeconds': 100, 'hadTimeToGrow': False}]}
        self.assertEqual(accept.run_case(self.case('farm-state'), self.ctx)['verdict'], accept.FAIL)


class ThreatCaseTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.srv = ThreadingHTTPServer(('127.0.0.1', 0), self.fake.handler())
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.ctx = accept.Ctx(hubner.Client(f'http://127.0.0.1:{self.srv.server_address[1]}/mcp')); self.ctx.test_xz = (5.0, 5.0)

    def tearDown(self):
        self.srv.shutdown(); self.srv.server_close()

    def case(self, key): return [c for c in cases.CASES if c.key == key][0]

    def test_threats_fails_when_a_tamed_animal_is_a_threat(self):
        self.fake.replies['threats_near'] = {'total': 1, 'passiveMobs': False, 'creatures': [{'prefab': 'Boar', 'threat': 'now', 'tamed': True, 'alert': True, 'haveTarget': True, 'distance': 3}]}
        self.assertEqual(accept.run_case(self.case('threats-near'), self.ctx)['verdict'], accept.FAIL)

    def test_threats_fails_when_the_order_is_wrong(self):
        self.fake.replies['threats_near'] = {'total': 2, 'passiveMobs': False, 'creatures': [{'prefab': 'a', 'threat': 'watch', 'tamed': False, 'alert': False, 'haveTarget': False, 'distance': 3}, {'prefab': 'b', 'threat': 'now', 'tamed': False, 'alert': True, 'haveTarget': True, 'distance': 5}]}
        self.assertEqual(accept.run_case(self.case('threats-near'), self.ctx)['verdict'], accept.FAIL)

    def test_threats_passes_on_a_well_formed_answer(self):
        self.fake.replies['threats_near'] = {'total': 2, 'passiveMobs': True, 'creatures': [{'prefab': 'a', 'threat': 'now', 'tamed': False, 'alert': True, 'haveTarget': True, 'distance': 5}, {'prefab': 'b', 'threat': 'ignore', 'tamed': True, 'alert': False, 'haveTarget': False, 'distance': 8}]}
        self.assertEqual(accept.run_case(self.case('threats-near'), self.ctx)['verdict'], accept.PASS)

    def test_raid_state_fails_when_the_roll_is_later_than_the_interval(self):
        self.fake.replies['raid_state'] = {'events': [{'name': 'x'}], 'secondsToNextRoll': 5000, 'rollIntervalSeconds': 2760, 'chancePercent': 20, 'current': None}
        self.assertEqual(accept.run_case(self.case('raid-state'), self.ctx)['verdict'], accept.FAIL)
