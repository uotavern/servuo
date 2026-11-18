"""Offline tests for tools/shard_rollout.py; no shard is started."""
import contextlib
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'tools'))
import shard_rollout  # noqa: E402


def make_set(directory, tag):
    directory.mkdir(parents=True, exist_ok=True)
    for name in shard_rollout.SET:
        (directory/name).write_bytes((tag+name).encode())
    return shard_rollout.set_hashes(directory)


def manifest(path, hashes):
    path.write_text(json.dumps({'assemblies': hashes}))
    return path


class ShardRolloutTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.shard = self.root/'shard'
        self.current = make_set(self.shard, 'old')
        (self.shard/'Saves').mkdir()
        (self.shard/'Saves'/'World.bin').write_bytes(b'world-v1')
        (self.shard/'Config').mkdir()
        (self.shard/'Config'/'Server.cfg').write_text('Port=1\n')
        self.candidate_dir = self.root/'candidate'
        self.candidate = make_set(self.candidate_dir, 'new')
        self.cur_m = manifest(self.root/'cur.json', self.current)
        self.cand_m = manifest(self.root/'cand.json', self.candidate)
        self.backup = self.root/'backup'
        self.addCleanup(self.tmp.cleanup)

    def run_tool(self, *argv):
        with contextlib.redirect_stdout(io.StringIO()):
            return shard_rollout.main([str(a) for a in argv])

    def test_full_cycle_restores_exact_set_and_state(self):
        self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup)
        self.run_tool('swap', '--shard-dir', self.shard, '--backup-dir', self.backup, '--candidate-dir', self.candidate_dir,
                      '--expect-current', self.cur_m, '--expect-candidate', self.cand_m)
        self.assertEqual(shard_rollout.set_hashes(self.shard), self.candidate)
        (self.shard/'Saves'/'World.bin').write_bytes(b'world-v2')
        self.run_tool('rollback', '--shard-dir', self.shard, '--backup-dir', self.backup)
        self.assertEqual(shard_rollout.set_hashes(self.shard), self.current)
        self.assertEqual((self.shard/'Saves'/'World.bin').read_bytes(), b'world-v2')  # assemblies-only rollback
        self.run_tool('rollback', '--shard-dir', self.shard, '--backup-dir', self.backup, '--restore-state')
        self.assertEqual((self.shard/'Saves'/'World.bin').read_bytes(), b'world-v1')
        aside = [p for p in self.shard.iterdir() if p.name.startswith('Saves.rollback-aside-')]
        self.assertEqual(len(aside), 1)
        self.assertEqual((aside[0]/'World.bin').read_bytes(), b'world-v2')
        self.assertEqual(self.run_tool('verify', '--shard-dir', self.shard, '--manifest', self.cur_m), 0)

    def test_swap_refuses_unexpected_current_set(self):
        self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup)
        with self.assertRaises(SystemExit):
            self.run_tool('swap', '--shard-dir', self.shard, '--backup-dir', self.backup, '--candidate-dir', self.candidate_dir,
                          '--expect-current', self.cand_m, '--expect-candidate', self.cand_m)
        self.assertEqual(shard_rollout.set_hashes(self.shard), self.current)

    def test_swap_refuses_tampered_candidate_without_partial_install(self):
        self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup)
        (self.candidate_dir/'Scripts.dll').write_bytes(b'tampered')
        with self.assertRaises(SystemExit):
            self.run_tool('swap', '--shard-dir', self.shard, '--backup-dir', self.backup, '--candidate-dir', self.candidate_dir,
                          '--expect-current', self.cur_m, '--expect-candidate', self.cand_m)
        self.assertEqual(shard_rollout.set_hashes(self.shard), self.current)
        self.assertFalse([p for p in self.shard.iterdir() if p.name.endswith('.rollout-tmp')])

    def test_snapshot_refuses_existing_backup(self):
        self.backup.mkdir()
        with self.assertRaises(SystemExit):
            self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup)

    def test_live_root_requires_approval(self):
        old = shard_rollout.LIVE_ROOTS
        shard_rollout.LIVE_ROOTS = (self.root,)
        self.addCleanup(setattr, shard_rollout, 'LIVE_ROOTS', old)
        with self.assertRaises(SystemExit) as raised:
            self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup)
        self.assertIn('live-approval', str(raised.exception))
        self.assertFalse(self.backup.exists())

    def test_running_shard_is_refused(self):
        old = shard_rollout.running_from
        shard_rollout.running_from = lambda shard: [4242]
        self.addCleanup(setattr, shard_rollout, 'running_from', old)
        with self.assertRaises(SystemExit):
            self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup)
        self.assertFalse(self.backup.exists())

    def test_default_live_root_is_public_install(self):
        if 'SHARD_ROLLOUT_LIVE_ROOTS' in os.environ:
            self.skipTest('live roots overridden by environment')
        self.assertIn(Path('/opt/uoarena'), shard_rollout.LIVE_ROOTS)


if __name__ == '__main__':
    unittest.main()
