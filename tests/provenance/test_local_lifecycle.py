import copy
import gzip
import hashlib
import json
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('lifecycle', Path(__file__).with_name('local_lifecycle.py'))
lifecycle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lifecycle)


class ReceiptTests(unittest.TestCase):
    def rows(self):
        return [dict(type='header', seq=0, id='fixture', players=[{'serial':2}, {'serial':3}], rounds=1, training=True, ranked=False),
                dict(type='round_end', seq=1, round=1, winner=None, reason='time_limit'),
                dict(type='end', seq=2, id='fixture', complete=True, aborted=None, dropped=0, winner=None, score=[0,0])]

    def test_recorded_local_fixture_identity(self):
        root = Path(__file__).parent
        report = json.loads((root/'lifecycle-20261118.json').read_text())
        archive = (root/'fixtures/local-training-draw.jsonl.gz').read_bytes()
        raw = gzip.decompress(archive)
        self.assertEqual(hashlib.sha256(archive).hexdigest(), report['replay_sha256'])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), report['raw_sha256'])
        rows = [json.loads(line) for line in raw.splitlines()]
        header, end = lifecycle.receipt(rows, report['serials'])
        self.assertEqual(header['id'], report['match_id'])
        self.assertEqual(end, report['replay_end'])
        self.assertTrue(report['passed'] and report['processes_stopped'])

    def test_terminal_draw(self):
        self.assertEqual(lifecycle.receipt(self.rows(), [2,3])[1]['complete'], True)

    def test_wrong_participant_and_missing_or_duplicate_end_fail(self):
        for rows in [self.rows()[:-1], self.rows()+[self.rows()[-1]]]:
            with self.assertRaises(ValueError): lifecycle.receipt(rows, [2,3])
        with self.assertRaises(ValueError): lifecycle.receipt(self.rows(), [2,4])

    def test_incomplete_abort_identity_sequence_and_winner_fail(self):
        for key, value in [('complete', False), ('aborted', 'disconnect'), ('dropped',1), ('id','other'), ('seq',4), ('winner',2), ('score',[1,0])]:
            rows = copy.deepcopy(self.rows());rows[-1][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                lifecycle.receipt(rows, [2,3])

    def test_different_mode_fails(self):
        for key, value in [('rounds',3), ('training',False), ('ranked',True)]:
            rows = self.rows();rows[0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                lifecycle.receipt(rows, [2,3])

    def test_round_completion_and_ordered_seats_required(self):
        rows = self.rows(); rows[1]['reason'] = 'disconnect'
        with self.assertRaises(ValueError): lifecycle.receipt(rows, [2,3])
        with self.assertRaises(ValueError): lifecycle.receipt(self.rows(), [3,2])

    def test_privileged_ports_rejected(self):
        for port in (80, -1, 65536):
            with self.assertRaises(ValueError): lifecycle.ensure_free(port)


if __name__ == '__main__': unittest.main()
