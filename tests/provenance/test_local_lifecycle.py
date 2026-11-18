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

    def test_combat_requires_events_and_decisive_round(self):
        rows = self.rows()
        rows[1].update(winner=2, reason='defeat')
        rows[-1].update(winner=2, score=[1,0])
        with self.assertRaises(ValueError): lifecycle.receipt(rows, [2,3], combat=True)
        events = [dict(type='damage', actor=2, target=3, amount=20),
                  dict(type='heal', actor=3, target=3, amount=10),
                  dict(type='cast', actor=2, name='EnergyBoltSpell'),
                  dict(type='spell_target', actor=3, target=3, name='GreaterHealSpell')]
        rows = rows[:1]+events+rows[1:]
        for n, row in enumerate(rows): row['seq'] = n
        lifecycle.receipt(rows, [2,3], combat=True)
        for index in range(1, 5):
            damaged = copy.deepcopy(rows);damaged[index]['type'] = 'irrelevant'
            with self.assertRaises(ValueError): lifecycle.receipt(damaged, [2,3], combat=True)
        rows[-2]['reason'] = 'forfeit: offline'
        with self.assertRaises(ValueError): lifecycle.receipt(rows, [2,3], combat=True)

    def test_privileged_ports_rejected(self):
        for port in (80, -1, 65536):
            with self.assertRaises(ValueError): lifecycle.ensure_free(port)


class ListenerReadinessTests(unittest.TestCase):
    def free_port(self):
        import socket
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            return sock.getsockname()[1]

    def test_waits_for_game_listener_started_after_feed(self):
        import socket, threading, time
        port = self.free_port()
        listener = socket.socket()
        def late():
            time.sleep(.6)
            listener.bind(('127.0.0.1', port)); listener.listen(1)
        thread = threading.Thread(target=late); thread.start()
        deadline = time.monotonic()+5
        def remaining(limit=10):
            left = min(limit, deadline-time.monotonic())
            if left <= 0: raise TimeoutError('fixture deadline')
            return left
        try:
            lifecycle.wait_listening(port, remaining)
            self.assertGreater(time.monotonic(), deadline-5+.5)
        finally:
            thread.join(); listener.close()

    def test_listener_wait_is_bounded_and_detects_exit(self):
        import time
        port = self.free_port()
        deadline = time.monotonic()+.5
        def remaining(limit=10):
            left = min(limit, deadline-time.monotonic())
            if left <= 0: raise TimeoutError('fixture deadline')
            return left
        with self.assertRaises(TimeoutError):
            lifecycle.wait_listening(port, remaining)
        with self.assertRaises(RuntimeError):
            lifecycle.wait_listening(port, lambda limit=10: 1, alive=lambda: False)


class FinalizationTests(unittest.TestCase):
    def test_archive_without_metadata_is_not_finished(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            archive, meta = lifecycle.replay_paths(out, 'm1')
            archive.parent.mkdir(parents=True)
            self.assertFalse(lifecycle.finalized(out, None))
            archive.write_bytes(b'x')
            self.assertFalse(lifecycle.finalized(out, 'm1'))
            meta.write_text('{}')
            self.assertTrue(lifecycle.finalized(out, 'm1'))

    def test_wait_finalized_polls_for_late_metadata_within_deadline(self):
        import tempfile, threading, time
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)
            archive, meta = lifecycle.replay_paths(out, 'm1')
            archive.parent.mkdir(parents=True); archive.write_bytes(b'x')
            t = threading.Timer(.4, lambda: meta.write_text('{}')); t.start()
            self.assertEqual(lifecycle.wait_finalized(out, 'm1', lambda limit=10: 1), (archive, meta))
            t.join()
            meta.unlink()
            def expired(limit=10): raise TimeoutError('fixture deadline')
            with self.assertRaises(TimeoutError):
                lifecycle.wait_finalized(out, 'm1', expired)

    def test_no_bridge_pump_after_finalization(self):
        from unittest.mock import Mock
        rpc = Mock()
        self.assertFalse(lifecycle.match_tick(['a','b'], rpc, lambda: True))
        rpc.assert_not_called()
        self.assertTrue(lifecycle.match_tick(['a','b'], rpc, lambda: False))
        self.assertEqual(rpc.call_count, 2)


class SigtermTests(unittest.TestCase):
    def test_sigterm_runs_finally_cleanup(self):
        import subprocess, sys, tempfile
        with tempfile.TemporaryDirectory() as tmp:
            marker = Path(tmp)/'cleanup'
            code = ("import importlib.util,os,signal,sys,time\n"
                    "spec=importlib.util.spec_from_file_location('l',sys.argv[1]);l=importlib.util.module_from_spec(spec);spec.loader.exec_module(l)\n"
                    "l.install_sigterm()\n"
                    "try:\n    os.kill(os.getpid(), signal.SIGTERM); time.sleep(5)\n"
                    "finally:\n    open(sys.argv[2],'w').write('done')\n")
            proc = subprocess.run([sys.executable, '-c', code, str(Path(__file__).with_name('local_lifecycle.py')), str(marker)], timeout=10)
            self.assertEqual(proc.returncode, 143)
            self.assertEqual(marker.read_text(), 'done')


if __name__ == '__main__': unittest.main()
