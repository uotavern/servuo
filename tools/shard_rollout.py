#!/usr/bin/env python3
"""Snapshot, swap, verify and roll back the ServUO assembly set of a STOPPED shard.

This tool never starts, stops or signals a shard and never contacts a host.
It only copies files inside an explicit shard directory and a new backup
directory, and refuses to act while any process runs from, maps, or holds
files of that shard. The three assemblies (ServUO.exe, Scripts.dll,
Ultima.dll) are always handled as one set; a failed swap restores the
previous set before exiting.

Live roots: shard directories under /opt/uoarena are refused unless
--live-approval names the reviewed approval. SHARD_ROLLOUT_LIVE_ROOTS
(colon-separated) replaces that default; setting it to an empty value is an
error, so the guard can never be disabled by accident.

State: ServUO.exe.config, Config/ and Saves/ are snapshotted with content
hashes, modes and owners. Export/ (replay archives) is deliberately NOT
snapshotted or restored: it is append-only public output, covered by the
daily backup, and a rollback must not delete replays recorded meanwhile.

See ROLLOUT_ROLLBACK.md. Runs on the Python 3.8+ standard library.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import stat
import subprocess
import sys
import time

SET = ('ServUO.exe', 'Scripts.dll', 'Ultima.dll')
STATE = ('ServUO.exe.config', 'Config', 'Saves')
# World.Save rewrites these on every save; their mtime proves a fresh save.
SAVE_CORE = ('Saves/Mobiles', 'Saves/Items')
DEFAULT_LIVE_ROOT = '/opt/uoarena'
PROC = Path('/proc')
RUNNING_UNIT_STATES = {'active', 'activating', 'deactivating', 'reloading', 'refreshing'}
STOPPED_UNIT_STATES = {'inactive', 'failed'}
SPACE_MARGIN = 64 << 20


def fail(message):
    raise SystemExit('shard_rollout: '+message)


def unique():
    return '%s-%d-%d-%s' % (time.strftime('%Y%m%dT%H%M%SZ', time.gmtime()), time.time_ns(), os.getpid(), secrets.token_hex(3))


def sha(path):
    h = hashlib.sha256()
    fd = os.open(str(path), os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
    with os.fdopen(fd, 'rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def within(path, root):
    path, root = Path(os.path.realpath(str(path))), Path(os.path.realpath(str(root)))
    return path == root or root in path.parents


def live_roots():
    raw = os.environ.get('SHARD_ROLLOUT_LIVE_ROOTS')
    if raw is None:
        return (Path(DEFAULT_LIVE_ROOT),)
    roots = tuple(Path(p) for p in raw.split(':') if p.strip())
    if not roots:
        fail('SHARD_ROLLOUT_LIVE_ROOTS is set but empty; unset it to use the default %s' % DEFAULT_LIVE_ROOT)
    return roots


# ---------------------------------------------------------------- metadata

def meta(path, with_hash=True):
    st = os.lstat(str(path))
    if stat.S_ISLNK(st.st_mode):
        fail('symlink refused: %s' % path)
    if stat.S_ISDIR(st.st_mode):
        entry = {'type': 'dir'}
    elif stat.S_ISREG(st.st_mode):
        entry = {'type': 'file', 'bytes': st.st_size}
        if with_hash:
            entry['sha256'] = sha(path)
    else:
        fail('non-regular entry refused: %s' % path)
    entry.update(mode=stat.S_IMODE(st.st_mode), uid=st.st_uid, gid=st.st_gid)
    return entry


def tree_manifest(base, name):
    """{relative path: meta} for base/name and everything below; refuses symlinks."""
    root = Path(base)/name
    out = {name: meta(root)}
    if out[name]['type'] == 'dir':
        for current, dirs, files in os.walk(str(root)):
            dirs.sort()
            for entry in sorted(dirs)+sorted(files):
                path = Path(current)/entry
                out[str(path.relative_to(base))] = meta(path)
    return out


def tree_bytes(manifest):
    return sum(m.get('bytes', 0) for m in manifest.values())


def set_hashes(directory):
    return {name: meta(Path(directory)/name)['sha256'] for name in SET}


def check_space(directory, needed, what):
    free = shutil.disk_usage(str(directory)).free
    if free < needed*1.1+SPACE_MARGIN:
        fail('not enough free space for %s in %s: need %d bytes (+10%% +64 MiB), free %d' % (what, directory, needed, free))
    return free


def fsync_dir(path):
    try:
        fd = os.open(str(path), os.O_RDONLY)
    except OSError:
        return
    try:
        os.fsync(fd)
    except OSError:
        pass
    finally:
        os.close(fd)


def apply_meta(path, entry):
    st = os.lstat(str(path))
    if (st.st_uid, st.st_gid) != (entry['uid'], entry['gid']):
        try:
            os.chown(str(path), entry['uid'], entry['gid'], follow_symlinks=False)
        except PermissionError:
            fail('cannot set owner %d:%d on %s (run as root)' % (entry['uid'], entry['gid'], path))
    os.chmod(str(path), entry['mode'])


def same_meta(actual, expected):
    keys = ('type', 'mode', 'uid', 'gid') + (('sha256',) if expected.get('type') == 'file' else ())
    return all(actual.get(k) == expected.get(k) for k in keys)


def copy_file(src, dst, entry):
    """Copy into a NEW path (O_EXCL|O_NOFOLLOW: never writes through a symlink), fsync, set owner/mode."""
    fd = os.open(str(dst), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    with os.fdopen(fd, 'wb') as out:
        sfd = os.open(str(src), os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
        with os.fdopen(sfd, 'rb') as inp:
            shutil.copyfileobj(inp, out, 1 << 20)
        out.flush()
        os.fsync(out.fileno())
    apply_meta(dst, entry)


def copy_tree(src_base, name, dst_path, manifest):
    """Copy src_base/name to dst_path (new) using owner/mode from manifest keyed by name-relative paths."""
    src = Path(src_base)/name
    if manifest[name]['type'] == 'file':
        copy_file(src, dst_path, manifest[name])
        return
    os.mkdir(str(dst_path), 0o700)
    dirs = [(dst_path, manifest[name])]
    for rel in sorted(k for k in manifest if k.startswith(name+'/')):
        target = Path(dst_path)/rel[len(name)+1:]
        if manifest[rel]['type'] == 'dir':
            os.mkdir(str(target), 0o700)
            dirs.append((target, manifest[rel]))
        else:
            copy_file(Path(src_base)/rel, target, manifest[rel])
    for path, entry in reversed(dirs):  # restrictive dir modes only after contents exist
        fsync_dir(path)
        apply_meta(path, entry)


def verify_tree(base, name, expected, what):
    actual = tree_manifest(base, name)
    if set(actual) != set(expected):
        fail('%s: entries differ from manifest (missing %s, extra %s)' % (
            what, sorted(set(expected)-set(actual))[:5], sorted(set(actual)-set(expected))[:5]))
    bad = [k for k in expected if not same_meta(actual[k], expected[k])]
    if bad:
        fail('%s: content/owner/mode differs for %s' % (what, bad[:5]))


def rebase(manifest, old, new):
    return {new+k[len(old):]: v for k, v in manifest.items()}


# --------------------------------------------------------------- liveness

def pids_matching_servuo():
    try:
        result = subprocess.run(['pgrep', '-f', 'ServUO.exe'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True)
    except FileNotFoundError:
        fail('pgrep is required to prove the shard is stopped')
    if result.returncode == 1:
        return []
    if result.returncode != 0:
        fail('pgrep failed (rc %d): %s' % (result.returncode, result.stderr.strip()))
    return [int(p) for p in result.stdout.split() if int(p) != os.getpid()]


def proc_paths(pid, strict):
    """Paths a process uses: cwd, command-line arguments (resolved against cwd), mapped and open files."""
    base = PROC/str(pid)
    paths = []
    try:
        cwd = os.readlink(str(base/'cwd'))
        paths.append(cwd)
        args = (base/'cmdline').read_bytes().split(b'\0')
        for arg in args:
            if arg:
                text = arg.decode(errors='replace')
                paths.append(text if text.startswith('/') else os.path.join(cwd, text))
        for line in (base/'maps').read_text(errors='replace').splitlines():
            parts = line.split(None, 5)
            if len(parts) == 6 and parts[5].startswith('/'):
                paths.append(parts[5].replace(' (deleted)', ''))
        for fd in os.listdir(str(base/'fd')):
            target = os.readlink(str(base/'fd'/fd))
            if target.startswith('/'):
                paths.append(target.replace(' (deleted)', ''))
    except (FileNotFoundError, ProcessLookupError):
        return []  # exited meanwhile
    except PermissionError:
        if strict:
            fail('cannot inspect pid %d; run as root to prove the shard is stopped' % pid)
        return []
    return paths


def lsof_paths(pid):
    try:
        result = subprocess.run(['lsof', '-nP', '-p', str(pid), '-Fn'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True)
    except FileNotFoundError:
        fail('neither /proc nor lsof is available to inspect pid %d' % pid)
    if result.returncode not in (0, 1):
        fail('lsof failed for pid %d (rc %d)' % (pid, result.returncode))
    return [line[1:] for line in result.stdout.splitlines() if line.startswith('n/')]


def cmdline(pid):
    try:
        return (PROC/str(pid)/'cmdline').read_bytes().replace(b'\0', b' ').decode(errors='replace').strip()
    except OSError:
        return '?'


def running_from(shard):
    """PIDs that execute from, map, or hold files of this shard (not merely sharing a parent directory)."""
    shard = Path(os.path.realpath(str(shard)))
    hits = set()
    linux = PROC.is_dir() and (PROC/'self').exists()
    for pid in pids_matching_servuo():
        paths = proc_paths(pid, strict=True) if linux else lsof_paths(pid)
        if any(within(p, shard) for p in paths):
            hits.add(pid)
    if linux:
        for entry in os.listdir(str(PROC)):
            if not entry.isdigit() or int(entry) in (os.getpid(), os.getppid()):
                continue
            pid = int(entry)
            paths = proc_paths(pid, strict=False)
            if not paths:
                continue
            cwd, rest = paths[0], paths[1:]
            command = cmdline(pid)
            opened = [p for p in rest if within(p, shard)]
            if opened or (within(cwd, shard) and ('mono' in command or 'ServUO' in command)):
                hits.add(pid)
    return sorted(hits)


def unit_state(unit):
    try:
        result = subprocess.run(['systemctl', 'is-active', unit], stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True)
    except FileNotFoundError:
        fail('systemctl not found but --unit %s was given' % unit)
    return result.stdout.strip()


def require_stopped(shard, unit=None):
    if unit:
        state = unit_state(unit)
        if state in RUNNING_UNIT_STATES:
            fail('unit %s is %s; stop it first' % (unit, state))
        if state not in STOPPED_UNIT_STATES:
            fail('cannot determine state of unit %s (%r)' % (unit, state))
    pids = running_from(shard)
    if pids:
        fail('shard %s is in use by pids %s (%s); stop it first' % (shard, pids, '; '.join(cmdline(p) for p in pids)))


# ------------------------------------------------------------------ guards

def guard(shard, approval):
    raw = Path(shard)
    if raw.is_symlink():
        fail('shard directory must not be a symlink: %s' % raw)
    shard = raw.resolve()
    if not (shard/'ServUO.exe').exists():
        fail('not a shard directory (no ServUO.exe): %s' % shard)
    live = [r for r in live_roots() if within(shard, r)]
    if live and not approval:
        fail('refusing live install path %s without --live-approval <reviewed approval URL>' % shard)
    for name in SET:
        meta(shard/name, with_hash=False)  # refuses symlinked or non-regular set members
    return shard


def load_manifest(path):
    data = json.loads(Path(path).read_text())
    hashes = data.get('assemblies', data)
    if set(hashes) != set(SET) or not all(isinstance(v, str) and len(v) == 64 for v in hashes.values()):
        fail('manifest %s must give 64-hex sha256 for exactly %s' % (path, ', '.join(SET)))
    return hashes


def load_snapshot(backup, shard, expected_assemblies, verify_state=False):
    backup = Path(backup)
    if backup.is_symlink():
        fail('backup directory must not be a symlink')
    snapshot = json.loads((backup/'snapshot.json').read_text())
    if snapshot.get('schema') != 2:
        fail('unsupported snapshot schema %r' % snapshot.get('schema'))
    if snapshot.get('shard') != str(shard):
        fail('snapshot belongs to %s, not %s' % (snapshot.get('shard'), shard))
    if snapshot.get('assemblies') != expected_assemblies:
        fail('snapshot assemblies differ from the expected manifest')
    for name in SET:
        if not same_meta(meta(backup/name), snapshot['set_meta'][name]):
            fail('snapshot copy of %s is corrupted or changed' % name)
    state = snapshot.get('state') or {}
    if not any(k.startswith('Saves/') and v['type'] == 'file' for k, v in state.items()):
        fail('snapshot holds no Saves files; take a snapshot after a fresh world save')
    if verify_state:
        for item in STATE:
            expected = {k: v for k, v in state.items() if k == item or k.startswith(item+'/')}
            if expected:
                verify_tree(backup, item, expected, 'snapshot '+item)
    return snapshot


# ------------------------------------------------------------- set install

def install_set(source, shard, expected, metas):
    """Install all three assemblies or none: stage verified temporaries, move finals aside, rename;
    on any failure move the asides back and exit with the restored state."""
    source = Path(source)
    for name in SET:
        meta(source/name, with_hash=False)
    if set_hashes(source) != expected:
        fail('source assemblies in %s do not match the expected manifest' % source)
    check_space(shard, sum((source/n).stat().st_size for n in SET), 'staged assemblies')
    previous = set_hashes(shard)
    token = unique()
    staged, asides = {}, {}
    try:
        for name in SET:
            tmp = shard/('.%s.rollout-%s' % (name, token))
            staged[name] = tmp
            copy_file(source/name, tmp, metas[name])
            if sha(tmp) != expected[name]:
                raise RuntimeError('staged copy corrupted: '+name)
        for name in SET:
            aside = shard/('.%s.rollout-aside-%s' % (name, token))
            os.replace(str(shard/name), str(aside))
            asides[name] = aside
            os.replace(str(staged[name]), str(shard/name))
        fsync_dir(shard)
        for name in SET:
            if not same_meta(meta(shard/name), dict(metas[name], type='file', sha256=expected[name])):
                raise RuntimeError('post-install hash/owner/mode mismatch: '+name)
    except BaseException as exc:
        problems = []
        for name, aside in asides.items():
            try:
                os.replace(str(aside), str(shard/name))
            except OSError as undo:
                problems.append('%s: %s' % (name, undo))
        for tmp in staged.values():
            if os.path.lexists(str(tmp)):
                os.unlink(str(tmp))
        fsync_dir(shard)
        if problems or set_hashes(shard) != previous:
            fail('set install failed (%s) AND restoring the previous set failed (%s); the shard may hold a MIXED SET - '
                 'restore all three assemblies from the snapshot before starting' % (exc, problems or 'hash mismatch'))
        fail('set install failed (%s); previous set restored unchanged: %s' % (exc, json.dumps(previous)))
    for aside in asides.values():
        os.unlink(str(aside))
    fsync_dir(shard)
    return set_hashes(shard)


def restore_state(backup, shard, snapshot):
    """Stage every state item under a temporary name, verify, then swap in; keep the previous items aside."""
    token = unique()
    items = [i for i in STATE if i in snapshot['state']]
    manifests = {i: {k: v for k, v in snapshot['state'].items() if k == i or k.startswith(i+'/')} for i in items}
    for item in items:
        if os.path.lexists(str(shard/item)) and Path(shard/item).is_symlink():
            fail('live %s is a symlink; refusing to replace it' % item)
    temps, done = {}, []
    try:
        for item in items:
            tmp = shard/('.%s.restore-%s' % (item, token))
            temps[item] = tmp
            copy_tree(backup, item, tmp, manifests[item])
            verify_tree(shard, tmp.name, rebase(manifests[item], item, tmp.name), 'staged '+item)
        for item in items:
            final, aside = shard/item, shard/('%s.rollback-aside-%s' % (item, token))
            had = os.path.lexists(str(final))
            if had:
                os.replace(str(final), str(aside))
            done.append((item, had, aside))
            os.replace(str(temps[item]), str(final))
        fsync_dir(shard)
        for item in items:
            verify_tree(shard, item, manifests[item], 'restored '+item)
    except BaseException as exc:
        for item, had, aside in reversed(done):
            final = shard/item
            if os.path.lexists(str(final)):
                os.replace(str(final), str(shard/('.%s.restore-failed-%s' % (item, token))))
            if had:
                os.replace(str(aside), str(final))
        for tmp in temps.values():
            if tmp.is_dir() and not tmp.is_symlink():
                shutil.rmtree(str(tmp))
            elif os.path.lexists(str(tmp)):
                os.unlink(str(tmp))
        fsync_dir(shard)
        fail('state restore failed (%s); previous state put back (assemblies are already the snapshot set)' % exc)
    return [str(aside) for _, had, aside in done if had]


# ---------------------------------------------------------------- commands

def save_check(shard, args):
    times = []
    for core in SAVE_CORE:
        root = shard/core
        files = [p for p in root.rglob('*') if p.is_file()] if root.is_dir() else []
        if not files:
            fail('no world save found under %s' % root)
        times += [p.stat().st_mtime for p in files]
    oldest = min(times)
    if args.saved_after is not None:
        threshold = args.saved_after
    else:
        threshold = time.time()-args.max_save_age
    if oldest < threshold:
        fail('world save is stale: oldest core save file %s is older than %s; save the world first' % (
            time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(oldest)), time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(threshold))))
    return {'oldest_core_save_mtime': oldest, 'threshold': threshold}


def parse_time(text):
    try:
        return float(text)
    except ValueError:
        import calendar
        return calendar.timegm(time.strptime(text.rstrip('Z'), '%Y-%m-%dT%H:%M:%S'))


def cmd_manifest(args):
    if args.from_dir:
        hashes = set_hashes(Path(args.from_dir))
    else:
        data = json.loads(Path(args.from_receipt).read_text())
        hashes = data[args.key]
    out = Path(args.output)
    if os.path.lexists(str(out)):
        fail('manifest output must not exist: %s' % out)
    out.write_text(json.dumps({'assemblies': hashes}, indent=2)+'\n')
    load_manifest(out)
    print(json.dumps({'manifest': str(out), 'assemblies': hashes}, indent=2))
    return 0


def cmd_verify(args):
    shard = Path(args.shard_dir).resolve()
    expected = load_manifest(args.manifest)
    actual = {n: meta(shard/n) for n in SET}
    result = {'shard': str(shard), 'actual': {n: m['sha256'] for n, m in actual.items()}, 'expected': expected,
              'owner_mode': {n: [m['uid'], m['gid'], oct(m['mode'])] for n, m in actual.items()}}
    result['match'] = result['actual'] == expected
    if args.owner:
        uid, gid = (int(x) for x in args.owner.split(':'))
        result['owner_match'] = all((m['uid'], m['gid']) == (uid, gid) for m in actual.values())
    print(json.dumps(result, indent=2))
    return 0 if result['match'] and result.get('owner_match', True) else 1


def cmd_snapshot(args):
    shard = guard(args.shard_dir, args.live_approval)
    require_stopped(shard, args.unit)
    backup = Path(args.backup_dir)
    if os.path.lexists(str(backup)):
        fail('backup directory must not exist: %s' % backup)
    checked = save_check(shard, args)
    state = {}
    for item in STATE:
        if os.path.lexists(str(shard/item)):
            state.update(tree_manifest(shard, item))
    set_meta = {n: meta(shard/n) for n in SET}
    check_space(backup.parent, tree_bytes(state)+sum(m['bytes'] for m in set_meta.values()), 'snapshot')
    backup.mkdir(parents=False, mode=0o700)
    for name in SET:
        copy_file(shard/name, backup/name, set_meta[name])
    for item in STATE:
        if item in state:
            copy_tree(shard, item, backup/item, {k: v for k, v in state.items() if k == item or k.startswith(item+'/')})
            verify_tree(backup, item, {k: v for k, v in state.items() if k == item or k.startswith(item+'/')}, 'snapshot '+item)
    if {n: meta(shard/n) for n in SET} != set_meta or tree_manifest(shard, 'Saves') != {k: v for k, v in state.items() if k == 'Saves' or k.startswith('Saves/')}:
        fail('shard changed while the snapshot was taken')
    snapshot = {'schema': 2, 'created': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'shard': str(shard),
                'assemblies': {n: m['sha256'] for n, m in set_meta.items()}, 'set_meta': set_meta, 'state': state,
                'save_check': checked, 'live_approval': args.live_approval, 'not_snapshotted': ['Export (append-only replays; daily backup)']}
    fd = os.open(str(backup/'snapshot.json'), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    with os.fdopen(fd, 'w') as out:
        out.write(json.dumps(snapshot, indent=2)+'\n')
        out.flush()
        os.fsync(out.fileno())
    fsync_dir(backup)
    print(json.dumps({'backup': str(backup), 'assemblies': snapshot['assemblies'], 'state_entries': len(state), 'save_check': checked}, indent=2))
    return 0


def cmd_swap(args):
    shard = guard(args.shard_dir, args.live_approval)
    require_stopped(shard, args.unit)
    current = load_manifest(args.expect_current)
    candidate = load_manifest(args.expect_candidate)
    load_snapshot(args.backup_dir, shard, current, verify_state=True)
    if set_hashes(shard) != current:
        fail('shard does not hold the expected current set; refusing to swap an unknown set')
    metas = {n: meta(shard/n) for n in SET}
    actual = install_set(args.candidate_dir, shard, candidate, metas)
    print(json.dumps({'swapped': str(shard), 'from': current, 'to': actual,
                      'owner_mode': {n: [metas[n]['uid'], metas[n]['gid'], oct(metas[n]['mode'])] for n in SET}}, indent=2))
    return 0


def cmd_rollback(args):
    shard = guard(args.shard_dir, args.live_approval)
    require_stopped(shard, args.unit)
    expected = load_manifest(args.expect_restore)
    backup = Path(args.backup_dir)
    snapshot = load_snapshot(backup, shard, expected, verify_state=args.restore_state)
    if args.restore_state:
        check_space(shard, tree_bytes(snapshot['state']), 'staged state restore')
    restored = install_set(backup, shard, expected, snapshot['set_meta'])
    report = {'rolled_back': str(shard), 'assemblies': restored, 'state_restored': False}
    if args.restore_state:
        report['kept_aside'] = restore_state(backup, shard, snapshot)
        report['state_restored'] = True
    print(json.dumps(report, indent=2))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command')
    sub.required = True
    p = sub.add_parser('manifest', help='write {"assemblies": ...} from a reviewed receipt key or a directory')
    src = p.add_mutually_exclusive_group(required=True)
    src.add_argument('--from-receipt')
    src.add_argument('--from-dir')
    p.add_argument('--key', choices=('baseline', 'candidate'))
    p.add_argument('--output', required=True)
    p.set_defaults(func=cmd_manifest)
    p = sub.add_parser('verify', help='compare the installed set with a manifest (read-only)')
    p.add_argument('--shard-dir', required=True)
    p.add_argument('--manifest', required=True)
    p.add_argument('--owner', help='uid:gid every assembly must have')
    p.set_defaults(func=cmd_verify)
    for name, func, text in (('snapshot', cmd_snapshot, 'copy set, ServUO.exe.config, Config and Saves of a stopped shard'),
                             ('swap', cmd_swap, 'install a verified candidate set over the verified current set'),
                             ('rollback', cmd_rollback, 'restore the snapshotted set (and optionally state)')):
        p = sub.add_parser(name, help=text)
        p.add_argument('--shard-dir', required=True)
        p.add_argument('--backup-dir', required=True)
        p.add_argument('--live-approval', help='reviewed approval URL; required for live install roots')
        p.add_argument('--unit', help='systemd unit that must be inactive/failed (e.g. arena-shard.service)')
        p.set_defaults(func=func)
        if name == 'snapshot':
            fresh = p.add_mutually_exclusive_group(required=True)
            fresh.add_argument('--saved-after', type=parse_time, help='UTC time (ISO 8601 or epoch) of the operator world save')
            fresh.add_argument('--max-save-age', type=float, help='seconds; every core save file must be newer')
        if name == 'swap':
            p.add_argument('--candidate-dir', required=True)
            p.add_argument('--expect-current', required=True)
            p.add_argument('--expect-candidate', required=True)
        if name == 'rollback':
            p.add_argument('--expect-restore', required=True, help='manifest the snapshot must hold (current.json)')
            p.add_argument('--restore-state', action='store_true',
                           help='also restore ServUO.exe.config, Config and Saves; previous state is kept aside')
    args = parser.parse_args(argv)
    if args.command == 'manifest' and args.from_receipt and not args.key:
        parser.error('--from-receipt requires --key')
    return args.func(args)


if __name__ == '__main__':
    sys.exit(main())
