#!/usr/bin/env python3
"""Snapshot, swap, verify and roll back the ServUO assembly set of a STOPPED shard.

This tool never starts, stops or signals a shard and never contacts a host.
It only copies files inside an explicit shard directory, and refuses to act
while a process is running from that directory. The three assemblies
(ServUO.exe, Scripts.dll, Ultima.dll) are always handled as one set.

Paths under the public install root (/opt/uoarena by default) are refused
unless --live-approval names the reviewed approval (for example the PR or
issue comment URL that accepted the rollout evidence). See
ROLLOUT_ROLLBACK.md.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

SET = ('ServUO.exe', 'Scripts.dll', 'Ultima.dll')
# Saved with the snapshot so a rollback restores the exact runtime inputs.
STATE = ('ServUO.exe.config', 'Config', 'Saves')
LIVE_ROOTS = tuple(Path(p) for p in os.environ.get('SHARD_ROLLOUT_LIVE_ROOTS', '/opt/uoarena').split(':') if p)


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def tree_hashes(root):
    root = Path(root)
    if root.is_file():
        return {root.name: sha(root)}
    out = {}
    for path in sorted(root.rglob('*')):
        if path.is_symlink():
            raise ValueError('symlink in shard state: '+str(path))
        if path.is_file():
            out[str(path.relative_to(root.parent))] = sha(path)
    return out


def set_hashes(directory):
    return {name: sha(Path(directory)/name) for name in SET}


def load_manifest(path):
    data = json.loads(Path(path).read_text())
    hashes = data.get('assemblies', data)
    if set(hashes) != set(SET):
        raise ValueError('manifest must name exactly '+', '.join(SET))
    return hashes


def guard(shard, approval):
    shard = Path(shard).resolve()
    if not (shard/'ServUO.exe').is_file():
        raise SystemExit('not a shard directory (no ServUO.exe): '+str(shard))
    live = [r for r in LIVE_ROOTS if shard == r.resolve() or r.resolve() in shard.parents]
    if live and not approval:
        raise SystemExit('refusing live install path %s without --live-approval <reviewed approval URL>' % shard)
    return shard


def running_from(shard):
    """PIDs whose command line references this shard's ServUO.exe."""
    try:
        out = subprocess.run(['pgrep', '-f', str(shard/'ServUO.exe')], capture_output=True, text=True).stdout
    except FileNotFoundError:
        raise SystemExit('pgrep is required to prove the shard is stopped')
    return [int(p) for p in out.split() if int(p) != os.getpid()]


def require_stopped(shard):
    pids = running_from(shard)
    if pids:
        raise SystemExit('shard is running from %s (pids %s); stop it first' % (shard, pids))


def install_set(source, shard, expected):
    """Copy all three assemblies to temporaries, verify, then rename each in place."""
    source = Path(source)
    if set_hashes(source) != expected:
        raise SystemExit('source assemblies do not match the expected manifest')
    staged = []
    try:
        for name in SET:
            tmp = shard/('.%s.rollout-tmp' % name)
            shutil.copy2(source/name, tmp)
            if sha(tmp) != expected[name]:
                raise SystemExit('staged copy corrupted: '+name)
            staged.append((tmp, shard/name))
        for tmp, final in staged:
            os.replace(tmp, final)
    finally:
        for tmp, _ in staged:
            if tmp.exists():
                tmp.unlink()
    actual = set_hashes(shard)
    if actual != expected:
        raise SystemExit('post-install hashes differ: '+json.dumps(actual))
    return actual


def cmd_verify(args):
    shard = Path(args.shard_dir).resolve()
    actual = set_hashes(shard)
    expected = load_manifest(args.manifest)
    result = {'shard': str(shard), 'actual': actual, 'expected': expected, 'match': actual == expected}
    print(json.dumps(result, indent=2))
    return 0 if result['match'] else 1


def cmd_snapshot(args):
    shard = guard(args.shard_dir, args.live_approval)
    require_stopped(shard)
    backup = Path(args.backup_dir)
    if backup.exists() or backup.is_symlink():
        raise SystemExit('backup directory must not exist: '+str(backup))
    backup.mkdir(parents=True)
    for name in SET:
        shutil.copy2(shard/name, backup/name)
    files = {}
    for name in STATE:
        src = shard/name
        if src.is_dir():
            shutil.copytree(src, backup/name, symlinks=True)
        elif src.is_file():
            shutil.copy2(src, backup/name)
        else:
            continue
        files.update(tree_hashes(backup/name))
    manifest = {'schema': 1, 'created': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'shard': str(shard),
                'assemblies': set_hashes(backup), 'state': files, 'live_approval': args.live_approval}
    if manifest['assemblies'] != set_hashes(shard):
        raise SystemExit('snapshot copy differs from shard assemblies')
    (backup/'snapshot.json').write_text(json.dumps(manifest, indent=2)+'\n')
    print(json.dumps({'backup': str(backup), 'assemblies': manifest['assemblies'], 'state_files': len(files)}, indent=2))
    return 0


def cmd_swap(args):
    shard = guard(args.shard_dir, args.live_approval)
    require_stopped(shard)
    current = load_manifest(args.expect_current)
    candidate = load_manifest(args.expect_candidate)
    snapshot = json.loads((Path(args.backup_dir)/'snapshot.json').read_text())
    if snapshot['assemblies'] != current or set_hashes(args.backup_dir) != current:
        raise SystemExit('snapshot does not hold the expected current set; take a fresh snapshot')
    if set_hashes(shard) != current:
        raise SystemExit('shard does not hold the expected current set; refusing to swap an unknown pair')
    actual = install_set(args.candidate_dir, shard, candidate)
    print(json.dumps({'swapped': str(shard), 'from': current, 'to': actual}, indent=2))
    return 0


def cmd_rollback(args):
    shard = guard(args.shard_dir, args.live_approval)
    require_stopped(shard)
    backup = Path(args.backup_dir)
    snapshot = json.loads((backup/'snapshot.json').read_text())
    expected = snapshot['assemblies']
    restored = install_set(backup, shard, expected)
    report = {'rolled_back': str(shard), 'assemblies': restored, 'saves_restored': False}
    if args.restore_state:
        for name in STATE:
            src = backup/name
            if not src.exists():
                continue
            if tree_hashes(src) != {k: v for k, v in snapshot['state'].items() if k == name or k.startswith(name+'/')}:
                raise SystemExit('snapshot state corrupted: '+name)
            dst = shard/name
            aside = shard/('%s.rollback-aside-%d' % (name, int(time.time())))
            if dst.exists():
                os.replace(dst, aside)  # keep the candidate-era state for forensics
                report.setdefault('kept_aside', []).append(str(aside))
            if src.is_dir():
                shutil.copytree(src, dst, symlinks=True)
            else:
                shutil.copy2(src, dst)
            if tree_hashes(dst) != tree_hashes(src):
                raise SystemExit('restored state differs from snapshot: '+name)
        report['saves_restored'] = True
    print(json.dumps(report, indent=2))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    p = sub.add_parser('verify', help='compare the installed set with a manifest (read-only)')
    p.add_argument('--shard-dir', required=True)
    p.add_argument('--manifest', required=True)
    p.set_defaults(func=cmd_verify)
    for name, func, text in (('snapshot', cmd_snapshot, 'copy set, config and saves of a stopped shard'),
                             ('swap', cmd_swap, 'install a verified candidate set over the verified current set'),
                             ('rollback', cmd_rollback, 'restore the snapshotted set (and optionally state)')):
        p = sub.add_parser(name, help=text)
        p.add_argument('--shard-dir', required=True)
        p.add_argument('--live-approval', help='reviewed approval URL; required for live install roots')
        p.add_argument('--backup-dir', required=True)
        p.set_defaults(func=func)
        if name == 'swap':
            p.add_argument('--candidate-dir', required=True)
            p.add_argument('--expect-current', required=True)
            p.add_argument('--expect-candidate', required=True)
        if name == 'rollback':
            p.add_argument('--restore-state', action='store_true',
                           help='also restore Config, ServUO.exe.config and Saves from the snapshot (candidate-era state is kept aside)')
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main())
