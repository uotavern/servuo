"""Offline and fault-injection tests for tools/shard_rollout.py; no shard is started."""
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'tools'))
import shard_rollout as sr  # noqa: E402


def make_set(directory, tag):
    directory.mkdir(parents=True, exist_ok=True)
    for name in sr.SET:
        (directory/name).write_bytes((tag+name).encode()*100)
        os.chmod(str(directory/name), 0o644)
    return sr.set_hashes(directory)


def write_manifest(path, hashes):
    path.write_text(json.dumps({'assemblies': hashes}))
    return path


def listing(directory):
    return sorted(p.name for p in Path(directory).iterdir())


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(os.path.realpath(self.tmp.name))
        self.shard = self.root/'shard'
        self.current = make_set(self.shard, 'old')
        os.chmod(str(self.shard/'Scripts.dll'), 0o640)
        for rel, data in (('Saves/Mobiles/Mobiles.bin', b'mob-v1'), ('Saves/Items/Items.bin', b'item-v1'),
                          ('Saves/Dueling.bin', b'duel-v1'), ('Config/Server.cfg', b'Port=1\n'), ('Export/DuelReplays/a.jsonl.gz', b'r1')):
            path = self.shard/rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        (self.shard/'ServUO.exe.config').write_text('<configuration/>')
        os.chmod(str(self.shard/'Saves'), 0o700)
        os.chmod(str(self.shard/'Saves'/'Dueling.bin'), 0o600)
        self.candidate_dir = self.root/'candidate'
        self.candidate = make_set(self.candidate_dir, 'new')
        self.cur_m = write_manifest(self.root/'current.json', self.current)
        self.cand_m = write_manifest(self.root/'candidate.json', self.candidate)
        self.backup = self.root/'backup'
        patches = [mock.patch.dict(os.environ, {'SHARD_ROLLOUT_LIVE_ROOTS': str(self.root/'no-live-root')}),
                   mock.patch.object(sr, 'PROC', self.root/'no-proc'),
                   mock.patch.object(sr, 'pids_matching_servuo', lambda: [])]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)

    def run_tool(self, *argv):
        with contextlib.redirect_stdout(io.StringIO()) as out:
            code = sr.main([str(a) for a in argv])
        return code, out.getvalue()

    def snapshot(self, shard=None, backup=None):
        return self.run_tool('snapshot', '--shard-dir', shard or self.shard, '--backup-dir', backup or self.backup, '--max-save-age', 600)

    def swap(self, current=None, backup=None):
        return self.run_tool('swap', '--shard-dir', self.shard, '--backup-dir', backup or self.backup, '--candidate-dir', self.candidate_dir,
                             '--expect-current', current or self.cur_m, '--expect-candidate', self.cand_m)

    def rollback(self, *extra, expect=None, backup=None):
        return self.run_tool('rollback', '--shard-dir', self.shard, '--backup-dir', backup or self.backup,
                             '--expect-restore', expect or self.cur_m, *extra)

    def assert_no_temporaries(self):
        leftovers = [n for n in listing(self.shard) if '.rollout-' in n or '.restore-' in n]
        self.assertEqual(leftovers, [])


class CycleTests(Base):
    def test_full_cycle_restores_exact_set_state_and_modes(self):
        self.snapshot()
        self.swap()
        self.assertEqual(sr.set_hashes(self.shard), self.candidate)
        self.assertEqual(oct(os.stat(str(self.shard/'Scripts.dll')).st_mode & 0o777), oct(0o640))  # mode preserved
        (self.shard/'Saves'/'Dueling.bin').write_bytes(b'duel-v2')
        self.rollback()
        self.assertEqual(sr.set_hashes(self.shard), self.current)
        self.assertEqual((self.shard/'Saves'/'Dueling.bin').read_bytes(), b'duel-v2')  # assemblies-only rollback
        code, out = self.rollback('--restore-state')
        self.assertEqual((self.shard/'Saves'/'Dueling.bin').read_bytes(), b'duel-v1')
        self.assertEqual(os.stat(str(self.shard/'Saves')).st_mode & 0o777, 0o700)
        self.assertEqual(os.stat(str(self.shard/'Saves'/'Dueling.bin')).st_mode & 0o777, 0o600)
        aside = [n for n in listing(self.shard) if n.startswith('Saves.rollback-aside-')]
        self.assertEqual(len(aside), 1)
        self.assertEqual((self.shard/aside[0]/'Dueling.bin').read_bytes(), b'duel-v2')
        self.assertEqual((self.shard/'Export'/'DuelReplays'/'a.jsonl.gz').read_bytes(), b'r1')  # Export untouched
        self.assert_no_temporaries()
        self.assertEqual(self.run_tool('verify', '--shard-dir', self.shard, '--manifest', self.cur_m)[0], 0)

    def test_aside_names_unique_within_one_second(self):
        names = {sr.unique() for _ in range(50)}
        self.assertEqual(len(names), 50)

    def test_manifest_validates_before_writing(self):
        """Review #7."""
        receipt = self.root/'receipt.json'
        receipt.write_text(json.dumps({'baseline': dict(self.current, **{'Ultima.dll': 'not-a-hash'})}))
        for argv in (('--from-receipt', receipt, '--key', 'baseline'), ('--from-receipt', receipt, '--key', 'candidate'),
                     ('--from-receipt', receipt)):
            with self.assertRaises(SystemExit) as raised:
                self.run_tool('manifest', *argv, '--output', self.root/'bad.json')
            self.assertIsInstance(raised.exception.code, str)  # clean message, not a traceback
            self.assertFalse((self.root/'bad.json').exists())

    def test_manifest_from_receipt(self):
        receipt = self.root/'receipt.json'
        receipt.write_text(json.dumps({'baseline': self.current, 'candidate': self.candidate}))
        self.run_tool('manifest', '--from-receipt', receipt, '--key', 'candidate', '--output', self.root/'m.json')
        self.assertEqual(sr.load_manifest(self.root/'m.json'), self.candidate)
        with self.assertRaises(SystemExit):
            self.run_tool('manifest', '--from-receipt', receipt, '--key', 'candidate', '--output', self.root/'m.json')


class SwapFaultTests(Base):
    """Review item 1: a failure in the middle of the rename phase must not leave a mixed set."""

    def test_failure_during_rename_phase_restores_previous_set(self):
        self.snapshot()
        real = os.replace
        calls = []

        def flaky(src, dst):
            calls.append((src, dst))
            if len(calls) == 4:  # second final rename: ServUO.exe new, Scripts.dll moved aside
                raise OSError('injected rename failure')
            return real(src, dst)
        with mock.patch.object(sr.os, 'replace', flaky), self.assertRaises(SystemExit) as raised:
            self.swap()
        self.assertIn('previous set restored unchanged', str(raised.exception))
        self.assertEqual(sr.set_hashes(self.shard), self.current)
        self.assert_no_temporaries()

    def test_failed_post_install_verification_restores_previous_set(self):
        self.snapshot()
        real = sr.same_meta
        with mock.patch.object(sr, 'same_meta', side_effect=lambda a, e: False if e.get('sha256') == self.candidate['Ultima.dll'] else real(a, e)), \
                self.assertRaises(SystemExit):
            self.swap()
        self.assertEqual(sr.set_hashes(self.shard), self.current)
        self.assert_no_temporaries()

    def test_failed_undo_reports_mixed_set(self):
        self.snapshot()
        real = os.replace
        calls = []

        def flaky(src, dst):
            calls.append(src)
            if len(calls) == 4 or (len(calls) > 4 and '.rollout-aside-' in str(src)):
                raise OSError('injected')
            return real(src, dst)
        with mock.patch.object(sr.os, 'replace', flaky), self.assertRaises(SystemExit) as raised:
            self.swap()
        self.assertIn('MIXED SET', str(raised.exception))

    def test_tampered_candidate_refused_without_partial_install(self):
        self.snapshot()
        (self.candidate_dir/'Scripts.dll').write_bytes(b'tampered')
        with self.assertRaises(SystemExit):
            self.swap()
        self.assertEqual(sr.set_hashes(self.shard), self.current)
        self.assert_no_temporaries()

    def test_swap_refuses_unexpected_current_set(self):
        self.snapshot()
        with self.assertRaises(SystemExit):
            self.swap(current=self.cand_m)
        self.assertEqual(sr.set_hashes(self.shard), self.current)

    def test_swap_refuses_when_live_saves_changed_after_snapshot(self):
        """Review #6."""
        self.snapshot()
        (self.shard/'Saves'/'Dueling.bin').write_bytes(b'played-after-snapshot')
        with self.assertRaises(SystemExit) as raised:
            self.swap()
        self.assertIn('changed since the snapshot', str(raised.exception))
        self.assertEqual(sr.set_hashes(self.shard), self.current)

    def test_swap_requires_nonempty_snapshot_state(self):
        self.snapshot()
        data = json.loads((self.backup/'snapshot.json').read_text())
        data['state'] = {k: v for k, v in data['state'].items() if not k.startswith('Saves')}
        (self.backup/'snapshot.json').write_text(json.dumps(data))
        with self.assertRaises(SystemExit) as raised:
            self.swap()
        self.assertIn('no Saves files', str(raised.exception))


class SnapshotIdentityTests(Base):
    """Review item 3: rollback and swap must only trust a snapshot of this shard holding the expected set."""

    def setUp(self):
        super().setUp()
        self.other = self.root/'other-shard'
        make_set(self.other, 'old')
        for rel in ('Saves/Mobiles/Mobiles.bin', 'Saves/Items/Items.bin'):
            (self.other/rel).parent.mkdir(parents=True, exist_ok=True)
            (self.other/rel).write_bytes(b'x')
        self.foreign = self.root/'foreign-backup'
        self.snapshot(shard=self.other, backup=self.foreign)

    def test_rollback_refuses_snapshot_of_another_shard(self):
        self.snapshot()
        self.swap()
        with self.assertRaises(SystemExit) as raised:
            self.rollback(backup=self.foreign)
        self.assertIn('belongs to', str(raised.exception))
        self.assertEqual(sr.set_hashes(self.shard), self.candidate)

    def test_swap_refuses_snapshot_of_another_shard(self):
        with self.assertRaises(SystemExit):
            self.swap(backup=self.foreign)
        self.assertEqual(sr.set_hashes(self.shard), self.current)

    def test_rollback_refuses_unexpected_restore_manifest(self):
        self.snapshot()
        self.swap()
        with self.assertRaises(SystemExit):
            self.rollback(expect=self.cand_m)
        self.assertEqual(sr.set_hashes(self.shard), self.candidate)

    def test_rollback_refuses_changed_backup_assembly(self):
        self.snapshot()
        self.swap()
        os.chmod(str(self.backup/'Scripts.dll'), 0o644)
        (self.backup/'Scripts.dll').write_bytes(b'changed')
        with self.assertRaises(SystemExit):
            self.rollback()
        self.assertEqual(sr.set_hashes(self.shard), self.candidate)


class RestoreStateFaultTests(Base):
    """Review item 4: verify everything first, stage under temporary names, never expose a partial Saves."""

    def prepare(self):
        self.snapshot()
        self.swap()
        (self.shard/'Saves'/'Dueling.bin').write_bytes(b'duel-v2')
        self.before = sr.tree_manifest(self.shard, 'Saves')

    def test_corrupted_backup_state_refused_before_touching_anything(self):
        self.prepare()
        (self.backup/'Saves'/'Items'/'Items.bin').write_bytes(b'corrupt')
        with self.assertRaises(SystemExit):
            self.rollback('--restore-state')
        self.assertEqual(sr.set_hashes(self.shard), self.candidate)  # not even the assemblies were touched
        self.assertEqual(sr.tree_manifest(self.shard, 'Saves'), self.before)

    def test_copy_failure_while_staging_leaves_live_saves_intact(self):
        self.prepare()
        real = sr.copy_file
        count = []

        def flaky(src, dst, entry):
            if '.Saves.restore-' in str(dst):
                count.append(dst)
                if len(count) == 2:
                    raise OSError('injected disk error')
            return real(src, dst, entry)
        with mock.patch.object(sr, 'copy_file', flaky), self.assertRaises(SystemExit) as raised:
            self.rollback('--restore-state')
        self.assertIn('previous state put back', str(raised.exception))
        self.assertEqual(sr.tree_manifest(self.shard, 'Saves'), self.before)
        self.assert_no_temporaries()

    def test_rename_failure_puts_previous_state_back(self):
        self.prepare()
        real = os.replace
        seen = []

        def flaky(src, dst):
            if '.Saves.restore-' in str(src):
                seen.append(src)
                raise OSError('injected rename failure')
            return real(src, dst)
        with mock.patch.object(sr.os, 'replace', flaky), self.assertRaises(SystemExit):
            self.rollback('--restore-state')
        self.assertTrue(seen)
        self.assertEqual(sr.tree_manifest(self.shard, 'Saves'), self.before)
        self.assertEqual((self.shard/'Config'/'Server.cfg').read_bytes(), b'Port=1\n')
        self.assertFalse([n for n in listing(self.shard) if n.startswith('Saves.rollback-aside-')])

    def test_rename_and_undo_failure_says_do_not_start(self):
        """Review #3: staged->live rename AND the undo rename both fail: no raw traceback, explicit stop message."""
        self.prepare()
        real = os.replace

        def flaky(src, dst):
            if '.Saves.restore-' in str(src) or 'Saves.rollback-aside-' in str(src):
                raise OSError('injected')
            return real(src, dst)
        with mock.patch.object(sr.os, 'replace', flaky), self.assertRaises(SystemExit) as raised:
            self.rollback('--restore-state')
        message = str(raised.exception)
        self.assertIn('DO NOT START THE SHARD', message)
        aside = [n for n in listing(self.shard) if n.startswith('Saves.rollback-aside-')]
        self.assertEqual(len(aside), 1)
        self.assertIn(str(self.shard/aside[0]), message)
        self.assertFalse((self.shard/'Saves').exists())
        self.assertEqual(sr.tree_manifest(self.shard, aside[0]), sr.rebase(self.before, 'Saves', aside[0]))
        self.assertEqual(code_of(raised.exception), 1)

    def test_failed_verification_after_swap_in_cleans_restore_failed_copies(self):
        """Review #5: no hidden .restore-failed-* copies remain after a failed and undone restore."""
        self.prepare()
        real = sr.verify_tree

        def flaky(base, name, expected, what):
            if what == 'restored Saves':
                raise RuntimeError('injected verification failure')
            return real(base, name, expected, what)
        with mock.patch.object(sr, 'verify_tree', flaky), self.assertRaises(SystemExit) as raised:
            self.rollback('--restore-state')
        self.assertIn('previous state put back', str(raised.exception))
        self.assertEqual(sr.tree_manifest(self.shard, 'Saves'), self.before)
        self.assert_no_temporaries()
        self.assertFalse([n for n in listing(self.shard) if 'restore-failed' in n or 'rollback-aside' in n])

    def test_insufficient_space_refused(self):
        self.prepare()
        with mock.patch.object(sr.shutil, 'disk_usage', return_value=mock.Mock(free=1)), self.assertRaises(SystemExit) as raised:
            self.rollback('--restore-state')
        self.assertIn('not enough free space', str(raised.exception))
        self.assertEqual(sr.set_hashes(self.shard), self.candidate)


class SymlinkTests(Base):
    """Review items 5 and 6."""

    def test_symlinked_set_member_refused(self):
        self.snapshot()
        real = self.root/'elsewhere.dll'
        os.replace(str(self.shard/'Ultima.dll'), str(real))
        os.symlink(str(real), str(self.shard/'Ultima.dll'))
        with self.assertRaises(SystemExit) as raised:
            self.swap()
        self.assertIn('symlink', str(raised.exception))
        self.assertEqual(real.read_bytes(), ('old'+'Ultima.dll').encode()*100)

    def test_symlinked_saves_refused(self):
        os.replace(str(self.shard/'Saves'), str(self.root/'saves-real'))
        os.symlink(str(self.root/'saves-real'), str(self.shard/'Saves'))
        with self.assertRaises(SystemExit):
            self.snapshot()
        self.assertFalse(self.backup.exists())

    def test_symlink_inside_config_refused(self):
        os.symlink('/etc/hosts', str(self.shard/'Config'/'linked.cfg'))
        with self.assertRaises(SystemExit):
            self.snapshot()

    def test_copy_never_writes_through_a_planted_temp_symlink(self):
        victim = self.root/'victim'
        victim.write_bytes(b'keep')
        planted = self.shard/'.planted'
        os.symlink(str(victim), str(planted))
        with self.assertRaises(FileExistsError):
            sr.copy_file(self.candidate_dir/'ServUO.exe', planted, sr.meta(self.shard/'ServUO.exe'))
        self.assertEqual(victim.read_bytes(), b'keep')


class SnapshotGuardTests(Base):
    def test_stale_save_refused(self):
        old = time.time()-3600
        for rel in ('Saves/Mobiles/Mobiles.bin', 'Saves/Items/Items.bin'):
            os.utime(str(self.shard/rel), (old, old))
        with self.assertRaises(SystemExit) as raised:
            self.snapshot()
        self.assertIn('stale', str(raised.exception))
        self.assertFalse(self.backup.exists())

    def test_saved_after_threshold(self):
        future = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time()+3600))
        with self.assertRaises(SystemExit):
            self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup, '--saved-after', future)
        past = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(time.time()-3600))
        self.assertEqual(self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup, '--saved-after', past)[0], 0)

    def test_snapshot_refuses_existing_backup(self):
        self.backup.mkdir()
        with self.assertRaises(SystemExit):
            self.snapshot()

    def test_snapshot_space_check(self):
        with mock.patch.object(sr.shutil, 'disk_usage', return_value=mock.Mock(free=10)), self.assertRaises(SystemExit):
            self.snapshot()
        self.assertFalse(self.backup.exists())

    def test_snapshot_excludes_export(self):
        self.snapshot()
        self.assertFalse((self.backup/'Export').exists())
        state = json.loads((self.backup/'snapshot.json').read_text())['state']
        self.assertTrue(any(k.startswith('Saves/') for k in state))
        self.assertFalse(any(k.startswith('Export') for k in state))


class LiveRootTests(Base):
    def test_live_root_requires_approval(self):
        with mock.patch.dict(os.environ, {'SHARD_ROLLOUT_LIVE_ROOTS': str(self.root)}), self.assertRaises(SystemExit) as raised:
            self.snapshot()
        self.assertIn('live-approval', str(raised.exception))
        self.assertFalse(self.backup.exists())

    def test_empty_live_roots_value_is_an_error(self):
        with mock.patch.dict(os.environ, {'SHARD_ROLLOUT_LIVE_ROOTS': ''}), self.assertRaises(SystemExit) as raised:
            self.snapshot()
        self.assertIn('set but empty', str(raised.exception))

    def test_default_live_root_is_public_install(self):
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop('SHARD_ROLLOUT_LIVE_ROOTS', None)
            self.assertEqual(sr.live_roots(), (Path('/opt/uoarena'),))


class OwnershipTests(Base):
    """Review item 7: owners are carried over (the tool runs as root, the shard as uoarena)."""

    def test_chown_applied_when_owner_differs(self):
        target = self.root/'f'
        target.write_bytes(b'x')
        entry = dict(sr.meta(target), uid=4242, gid=4343, mode=0o600)
        with mock.patch.object(sr.os, 'chown') as chown:
            sr.apply_meta(target, entry)
        chown.assert_called_once_with(str(target), 4242, 4343, follow_symlinks=False)
        self.assertEqual(os.stat(str(target)).st_mode & 0o777, 0o600)

    def test_chown_permission_error_is_explicit(self):
        target = self.root/'f'
        target.write_bytes(b'x')
        entry = dict(sr.meta(target), uid=4242)
        with mock.patch.object(sr.os, 'chown', side_effect=PermissionError()), self.assertRaises(SystemExit) as raised:
            sr.apply_meta(target, entry)
        self.assertIn('run as root', str(raised.exception))

    def test_swap_carries_current_owner_and_mode_to_new_files(self):
        self.snapshot()
        seen = []
        real = sr.apply_meta
        with mock.patch.object(sr, 'apply_meta', side_effect=lambda p, e: (seen.append((Path(p).name, e['uid'], e['gid'], e['mode'])), real(p, e))):
            self.swap()
        st = os.stat(str(self.shard/'Scripts.dll'))
        self.assertIn(('.Scripts.dll.rollout-', st.st_uid, st.st_gid, 0o640), [(n[:21], u, g, m) for n, u, g, m in seen])
        code, out = self.run_tool('verify', '--shard-dir', self.shard, '--manifest', self.cand_m, '--owner', '%d:%d' % (st.st_uid, st.st_gid))
        self.assertEqual(code, 0)


class RunningShardTests(Base):
    """Review item 2: relative/symlinked invocations, open files, lab shard distinction, pgrep errors, systemd."""

    def fake_proc(self, processes):
        proc = self.root/'proc'
        (proc/'self').mkdir(parents=True)
        for pid, spec in processes.items():
            cwd, argv, maps, fds = spec[:4]
            ppid = spec[4] if len(spec) > 4 else 1
            base = proc/str(pid)
            (base/'fd').mkdir(parents=True)
            (base/'stat').write_text('%d (%s) S %d 0 0\n' % (pid, os.path.basename(argv[0]) if argv else '?', ppid))
            os.symlink(cwd, str(base/'cwd'))
            (base/'cmdline').write_bytes(b'\0'.join(a.encode() for a in argv)+b'\0')
            (base/'maps').write_text(''.join('7f00-7f01 r--p 00000000 08:01 1 %s\n' % m for m in maps))
            for i, target in enumerate(fds):
                os.symlink(target, str(base/'fd'/str(i)))
        return proc

    def test_detects_relative_symlinked_and_open_file_users_not_lab_shard(self):
        lab = self.root/'lab-shard'
        make_set(lab, 'lab')
        alias = self.root/'shard-alias'
        os.symlink(str(self.shard), str(alias))
        proc = self.fake_proc({
            100: (str(lab), ['/usr/bin/mono', str(lab/'ServUO.exe'), '-service'], [str(lab/'Scripts.dll')], []),
            101: (str(self.shard), ['/usr/bin/mono', 'ServUO.exe', '-service'], [], []),
            102: ('/', ['/usr/bin/mono', str(alias/'ServUO.exe')], [], []),
            103: ('/', ['tail', '-f'], [], [str(self.shard/'Saves'/'Dueling.bin')]),
            104: (str(self.shard), ['/bin/bash'], [], []),  # an operator shell in the directory is not a shard
        })
        with mock.patch.object(sr, 'PROC', proc), mock.patch.object(sr, 'pids_matching_servuo', lambda: [100, 101, 102]):
            self.assertEqual(sr.running_from(self.shard), [101, 102, 103])
            self.assertEqual(sr.running_from(lab), [100])
            with self.assertRaises(SystemExit) as raised:
                self.snapshot()
        self.assertIn('in use by pids [101, 102, 103]', str(raised.exception))
        self.assertFalse(self.backup.exists())

    def test_operator_shell_and_sudo_ancestors_do_not_block(self):
        """Review #2: a login shell with cwd in the shard and a sudo grandparent whose argv names
        shard files are this tool's ancestors, not a running shard."""
        proc = self.fake_proc({
            os.getpid(): ('/', ['python3', 'tools/shard_rollout.py', 'rollback', '--shard-dir', str(self.shard)], [], [], 300),
            300: (str(self.shard), ['-bash'], [], ['/dev/pts/0'], 301),
            301: ('/root', ['sudo', 'sh', '-c', 'sha256sum %s/ServUO.exe; python3 tools/shard_rollout.py rollback --shard-dir %s' % (self.shard, self.shard)], [], [], 1),
            302: (str(self.shard), ['/bin/bash', '--login'], [], []),  # unrelated login shell parked in the shard
        })
        with mock.patch.object(sr, 'PROC', proc), mock.patch.object(sr, 'pids_matching_servuo', lambda: [301]):
            self.assertEqual(sr.ancestors() >= {os.getpid(), 300, 301}, True)
            self.assertEqual(sr.running_from(self.shard), [])
            self.assertEqual(self.snapshot()[0], 0)

    def test_ancestor_holding_open_shard_file_still_blocks(self):
        proc = self.fake_proc({
            os.getpid(): ('/', ['python3', 'tools/shard_rollout.py'], [], [], 300),
            300: (str(self.shard), ['-bash'], [], [str(self.shard/'Saves'/'Dueling.bin')], 1),
            101: (str(self.shard), ['/usr/bin/mono', 'ServUO.exe', '-service'], [], []),
        })
        with mock.patch.object(sr, 'PROC', proc), mock.patch.object(sr, 'pids_matching_servuo', lambda: [101]):
            self.assertEqual(sr.running_from(self.shard), [101, 300])

    def test_non_mono_process_with_shard_argument_does_not_block(self):
        proc = self.fake_proc({
            400: ('/', ['less', str(self.shard/'Config'/'Server.cfg')], [], []),
            401: ('/', ['tail', '-f', str(self.shard/'ServUO.exe.config')], [], []),
        })
        with mock.patch.object(sr, 'PROC', proc), mock.patch.object(sr, 'pids_matching_servuo', lambda: [401]):
            self.assertEqual(sr.running_from(self.shard), [])

    def test_mono_identified_by_exe_link(self):
        proc = self.fake_proc({500: (str(self.shard), ['ServUO-renamed', '-service'], [], [])})
        os.symlink('/usr/bin/mono-sgen', str(proc/'500'/'exe'))
        with mock.patch.object(sr, 'PROC', proc), mock.patch.object(sr, 'pids_matching_servuo', lambda: []):
            self.assertEqual(sr.running_from(self.shard), [500])

    def test_lab_shard_alone_does_not_block(self):
        lab = self.root/'lab-shard'
        make_set(lab, 'lab')
        data = self.root/'data'  # shared client data (/opt/uoarena/data), outside the public shard
        data.mkdir()
        (data/'map0.mul').write_bytes(b'm')
        proc = self.fake_proc({100: (str(lab), ['/usr/bin/mono', str(lab/'ServUO.exe'), '-service'],
                                     [str(lab/'ServUO.exe'), str(data/'map0.mul')], [str(data/'map0.mul')])})
        with mock.patch.object(sr, 'PROC', proc), mock.patch.object(sr, 'pids_matching_servuo', lambda: [100]):
            self.assertEqual(self.snapshot()[0], 0)

    def test_pgrep_error_is_fatal(self):
        failed = subprocess.CompletedProcess(['pgrep'], 2, stdout='', stderr='pgrep: bad')
        with mock.patch.object(sr.subprocess, 'run', return_value=failed), self.assertRaises(SystemExit) as raised:
            ORIGINAL_PIDS()
        self.assertIn('pgrep failed', str(raised.exception))

    def test_pgrep_no_match_is_stopped(self):
        none = subprocess.CompletedProcess(['pgrep'], 1, stdout='', stderr='')
        with mock.patch.object(sr.subprocess, 'run', return_value=none):
            self.assertEqual(ORIGINAL_PIDS(), [])

    def test_active_unit_refused(self):
        with mock.patch.object(sr, 'unit_state', return_value='active'), self.assertRaises(SystemExit) as raised:
            self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup, '--max-save-age', 600, '--unit', 'arena-shard.service')
        self.assertIn('is active', str(raised.exception))

    def test_unknown_unit_state_refused(self):
        with mock.patch.object(sr, 'unit_state', return_value=''), self.assertRaises(SystemExit):
            self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup, '--max-save-age', 600, '--unit', 'x.service')

    def test_inactive_unit_allowed(self):
        with mock.patch.object(sr, 'unit_state', return_value='inactive'):
            self.assertEqual(self.run_tool('snapshot', '--shard-dir', self.shard, '--backup-dir', self.backup, '--max-save-age', 600, '--unit', 'x.service')[0], 0)


ORIGINAL_PIDS = sr.pids_matching_servuo


def code_of(exc):
    return 1 if isinstance(exc.code, str) else exc.code


if __name__ == '__main__':
    unittest.main()
