import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import Mock

ROOT = Path(__file__).parent
spec = importlib.util.spec_from_file_location('combat', ROOT/'combat_policy.py')
combat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(combat)
spec = importlib.util.spec_from_file_location('lifecycle', ROOT/'local_lifecycle.py')
lifecycle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lifecycle)


class CombatTests(unittest.TestCase):
    def test_finalized_receipt_prevents_post_resurrection_cast(self):
        rpc = Mock()
        p = combat.Combat([None,None], [2,3], rpc, Mock(), {}, finished=lambda:True)
        with self.assertRaises(combat.MatchFinished):
            p.act(0, {'type':'CastSpell','spell':42})
        rpc.assert_not_called()

    def test_receipt_finishing_during_cursor_wait_stops_without_timeout(self):
        # The loser may already be resurrected; finalized archive is authoritative.
        finished = Mock(side_effect=[False, True])
        rpc = Mock(return_value={'obs':{'player':{'dead':False}}})
        p = combat.Combat([None,None], [2,3], rpc, Mock(), {}, finished=finished)
        with self.assertRaises(combat.MatchFinished):
            p.cast(0,42,3)
        self.assertEqual(sum(c.args[1].get('cmd')=='act' for c in rpc.call_args_list),1)

    def test_actual_combat_receipt_not_wrapper_success(self):
        evidence = ROOT/'combat-evidence'
        raw = gzip.decompress((evidence/'attempt2-combat.jsonl.gz').read_bytes())
        meta = json.loads((evidence/'attempt2-combat.meta.json').read_text())
        self.assertEqual(hashlib.sha256(raw).hexdigest(), meta['sha256'])
        rows = [json.loads(line) for line in raw.splitlines()]
        header, end = lifecycle.receipt(rows, [2,3], combat=True)
        self.assertEqual(header['id'],meta['id'])
        self.assertEqual(end['score'],[1,0])
        self.assertEqual(sum(r['amount'] for r in rows if r['type']=='damage'),202)
        self.assertEqual(sum(r['amount'] for r in rows if r['type']=='heal'),78)
        executed = json.loads((evidence/'attempt2-wrapper-failed.json').read_text())
        self.assertFalse(executed['passed'])
        self.assertTrue(executed['processes_stopped'])
        self.assertEqual(executed['strength_potion']['after']['strength'],120)
        self.assertEqual(executed['round_reset_strength'],100)
        self.assertEqual(executed['heal_potion']['after']['hits']-executed['heal_potion']['before']['hits'],28)

    def _walker(self, positions):
        calls = []
        seq = iter(positions)
        def rpc(client, cmd):
            calls.append(cmd)
            if cmd['cmd'] == 'observe':
                return {'obs':{'player':{'pos':next(seq)}}}
            return {'ok':True}
        return calls, rpc

    def test_approach_uses_single_steps_never_walkto_route(self):
        # Live reruns 1-2: a lingering WalkTo route stalled bridge pumps past
        # the response deadline, during the fight and after release.
        calls, rpc = self._walker([{'x':x,'y':3720} for x in range(6077, 6087)])
        p = combat.Combat([None,None], [2,3], rpc, Mock(), {})
        p.approach({'x':6090,'y':3721})
        acts = [c['action'] for c in calls if c['cmd']=='act']
        self.assertTrue(acts)
        self.assertTrue(all(a == {'type':'Walk','dir':3,'run':True} for a in acts[:1]))
        self.assertNotIn('WalkTo', [a['type'] for a in acts])
        self.assertTrue(all(a['dir'] in (2,3) for a in acts))
        self.assertLessEqual(p.report['approach']['gap'], 4)

    def test_approach_fails_closed_out_of_spell_range(self):
        stuck = {'x':6077,'y':3720}
        calls, rpc = self._walker([stuck]*1000)
        p = combat.Combat([None,None], [2,3], rpc, Mock(), {})
        with self.assertRaises(ValueError):
            p.approach({'x':6090,'y':3720}, seconds=0.5)
        self.assertEqual(p.report['approach']['gap'], 13)

    def test_approach_in_range_needs_no_step(self):
        calls, rpc = self._walker([{'x':6087,'y':3721}])
        p = combat.Combat([None,None], [2,3], rpc, Mock(), {})
        p.approach({'x':6090,'y':3721})
        self.assertEqual([c for c in calls if c['cmd']=='act'], [])

    def test_fight_approaches_before_first_cast_without_walkto(self):
        rpc = Mock()
        p = combat.Combat([None,None], [2,3], rpc, Mock(), {})
        order = []
        p.observe = lambda seat: {'player':{'strength':100,'pos':{'x':6090,'y':3721}}}
        p.approach = lambda target: order.append(('approach', target['x'], target['y']))
        def cast(*a):
            order.append(('cast',)+a); raise combat.MatchFinished()
        p.cast = cast
        with self.assertRaises(combat.MatchFinished):
            p.fight()
        self.assertEqual(order, [('approach', 6090, 3721), ('cast', 0, 42, 3)])
        self.assertNotIn('WalkTo', [c.args[1].get('action',{}).get('type') for c in rpc.call_args_list])

    def test_rerun1_actual_combat_receipt_but_wrapper_failed(self):
        evidence = ROOT/'combat-evidence'/'rerun-20261118'
        raw = gzip.decompress((evidence/'run1-combat.jsonl.gz').read_bytes())
        meta = json.loads((evidence/'run1-combat.meta.json').read_text())
        self.assertEqual(hashlib.sha256(raw).hexdigest(), meta['sha256'])
        rows = [json.loads(line) for line in raw.splitlines()]
        header, end = lifecycle.receipt(rows, [2,3], combat=True)
        self.assertEqual(header['id'], 'ab4d18bcd24947e8bd472ad00a3cfea5')
        self.assertEqual((end['winner'], end['score']), (2, [1,0]))
        self.assertEqual(sum(r['amount'] for r in rows if r['type']=='damage'), 170)
        self.assertEqual(sum(r['amount'] for r in rows if r['type']=='heal'), 52)
        executed = json.loads((evidence/'run1-wrapper-failed.json').read_text())
        self.assertFalse(executed['passed'])
        self.assertTrue(executed['processes_stopped'])
        self.assertIn('WalkTo', [a['action']['type'] for a in executed['combat_actions']])
        for name in ('run2-wrapper-failed.json', 'run3-wrapper-failed.json'):
            failed = json.loads((evidence/name).read_text())
            self.assertFalse(failed['passed']); self.assertTrue(failed['processes_stopped'])

if __name__ == '__main__': unittest.main()
