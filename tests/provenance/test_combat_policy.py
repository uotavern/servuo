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


if __name__ == '__main__': unittest.main()
