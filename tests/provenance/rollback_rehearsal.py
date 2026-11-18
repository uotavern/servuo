#!/usr/bin/env python3
"""Rehearse the reviewed rollout/rollback procedure on ONE disposable loopback shard.

baseline (deployed-equivalent set) -> health -> snapshot -> swap candidate ->
health -> rollback (assemblies only, candidate-era saves kept) -> health ->
rollback --restore-state -> health.

Every phase reuses one persistent world directory under --output, so the
health checks also prove that saves written by one set load under the other.
Only processes started here are touched; all are stopped in `finally`.
Never contacts a remote host and never runs tools/shard_rollout.py against a
live install path.
"""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import select
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
from local_lifecycle import (PINNED as CANDIDATE, configure, ensure_free, finalized, install_sigterm,  # noqa: E402
                             match_tick, receipt, sha, wait_finalized, wait_listening)
from combat_policy import Combat  # noqa: E402

BASELINE = {'ServUO.exe': '3a44e60b9edda4c9fb8bba313cb9950c394d2a9d24c6ab315b008549127b3165',
            'Scripts.dll': 'b30c89378266c7b0a7a2f55a162d979afecc5d7084ebc1782f338c91074012f9',
            'Ultima.dll': '2adeee941bce23f3b4cb8fb465d7d5d796061e2e84852a1b61b14268dcd88a17'}
SET = tuple(BASELINE)
TOOL = ROOT/'tools'/'shard_rollout.py'
PHASE_SECONDS = 150


def tool(*argv):
    result = subprocess.run([sys.executable, str(TOOL), *map(str, argv)], capture_output=True, text=True, timeout=120)
    entry = {'argv': ['shard_rollout.py', *map(str, argv[:1])], 'exit': result.returncode,
             'stdout': json.loads(result.stdout) if result.stdout.strip().startswith('{') else result.stdout,
             'stderr': result.stderr.strip()}
    return entry


def provision(shard, baseline, source, data, port, feed_port):
    shard.mkdir(parents=True)
    for name in SET:
        shutil.copy2(baseline/name, shard/name)
    shutil.copy2(source/'ServUO.exe.config', shard/'ServUO.exe.config')
    for name in ('Config', 'Data'):
        shutil.copytree(source/name, shard/name, symlinks=False)
    settings = {
        'Server.cfg': {'Listen': 'Listen=127.0.0.1', 'Address': 'Address=127.0.0.1', 'Port': 'Port='+str(port)},
        'Duel.cfg': {'WebHost': 'WebHost=127.0.0.1', 'WebPort': 'WebPort='+str(feed_port),
                     'ShowdownAfterSeconds': 'ShowdownAfterSeconds=10', 'RoundTimeLimitSeconds': 'RoundTimeLimitSeconds=20'},
        'Arena.cfg': {'Enabled': 'Enabled=true', 'DisableRisingTide': 'DisableRisingTide=true', 'SelfPlay': 'SelfPlay=false', 'BotAccounts': 'BotAccounts='},
        # No timed saves: each phase performs one explicit console save at shutdown.
        'AutoSave.cfg': {'Enabled': 'Enabled=false'}, 'AutoRestart.cfg': {'Enabled': 'Enabled=false'},
        'Accounts.cfg': {'AccountsPerIp': 'AccountsPerIp=2', 'AutoCreateAccounts': 'AutoCreateAccounts=true'},
        'DataPath.cfg': {'CustomPath': 'CustomPath='+str(data)}, 'Compiler.cfg': {'Dynamic': 'Dynamic=false'},
    }
    for name, values in settings.items():
        configure(shard/'Config'/name, values)
    return settings


def saves_digest(shard):
    root = shard/'Saves'
    if not root.is_dir():
        return {'files': 0, 'sha256': None}
    h = hashlib.sha256()
    files = sorted(p for p in root.rglob('*') if p.is_file())
    for p in files:
        h.update(str(p.relative_to(root)).encode()+b'\0'+sha(p).encode()+b'\n')
    return {'files': len(files), 'sha256': h.hexdigest()}


def backpack(probe):
    """Sorted [serial, graphic, amount] of both seats' backpack contents."""
    return [sorted([i['serial'], i['graphic'], i.get('amount', 1)] for i in probe.inventory(seat)) for seat in range(2)]


def health(shard, label, bridge, data, port, feed_port, expect_set, expect_serials, expect_backpack):
    """Protocol login, supply/bottle, round-reset and replay checks, then a saving shutdown."""
    report = {'phase': label, 'passed': False, 'installed': {n: sha(shard/n) for n in SET}, 'expected_set': expect_set}
    if report['installed'] != expect_set:
        raise ValueError(label+': installed set is not the expected set')
    ensure_free(port)
    ensure_free(feed_port)
    deadline = time.monotonic()+PHASE_SECONDS
    processes, handles, buffers = [], [], {}

    def remaining(limit=10):
        seconds = min(limit, deadline-time.monotonic())
        if seconds <= 0:
            raise TimeoutError(label+': phase deadline')
        return seconds

    def start(command, name, stdin=False, stdout=False):
        log = (shard/('%s-%s.log' % (label, name))).open('w');handles.append(log)
        p = subprocess.Popen([str(x) for x in command], cwd=shard, stdin=subprocess.PIPE if stdin else subprocess.DEVNULL,
                             stdout=subprocess.PIPE if stdout else log, stderr=log, bufsize=0)
        processes.append(p)
        return p

    def receive(p):
        buf = buffers.get(p.pid, b'')
        while b'\n' not in buf:
            if not select.select([p.stdout], [], [], remaining())[0]:
                raise TimeoutError('bridge response')
            part = os.read(p.stdout.fileno(), 65536)
            if not part:
                raise RuntimeError('bridge closed before response')
            buf += part
        line, buffers[p.pid] = buf.split(b'\n', 1)
        return json.loads(line)

    def rpc(p, obj):
        p.stdin.write((json.dumps(obj)+'\n').encode())
        result = receive(p)
        if result.get('ok') is not True:
            raise ValueError('bridge command failed: '+str(result))
        return result

    def feed():
        with urllib.request.urlopen('http://127.0.0.1:%d/duel/' % feed_port, timeout=remaining(3)) as response:
            return json.load(response)

    started = time.monotonic()
    server = None
    try:
        server = start(['mono', shard/'ServUO.exe', '-noconsole', '-service'], 'server', stdin=True)
        while True:
            remaining()
            if server.poll() is not None:
                raise RuntimeError('shard exited during startup')
            try:
                initial = feed();break
            except (OSError, urllib.error.URLError):
                time.sleep(.2)
        wait_listening(port, remaining, lambda: server.poll() is None)
        report['startup_seconds'] = round(time.monotonic()-started, 1)
        # Training matches are not recorded in Dueling.bin history, so world
        # continuity is proven by persisted characters and backpack items.
        if initial.get('live') or initial.get('recent'):
            raise ValueError(label+': fixture world must have no live or recorded matches')
        clients, serials = [], []
        for username in ('fixture_alpha', 'fixture_beta'):
            p = start([bridge, '127.0.0.1', str(port), username, 'local-fixture-only', data], username, stdin=True, stdout=True)
            ready = receive(p)
            if ready.get('event') != 'ready' or ready.get('schema_version') != 33:
                raise ValueError('bridge handshake failed')
            serials.append(ready['player']['serial']);clients.append(p)
        report['serials'] = serials
        if expect_serials is not None and serials != expect_serials:
            raise ValueError('%s: characters not preserved across restart: %s != %s' % (label, serials, expect_serials))
        match_id = None
        probe = Combat(clients, serials, rpc, remaining, {}, lambda: False)
        report['backpack_at_login'] = backpack(probe)
        if expect_backpack is not None and report['backpack_at_login'] != expect_backpack:
            raise ValueError(label+': backpack items not preserved across restart')
        combat = Combat(clients, serials, rpc, remaining, report, lambda: finalized(shard, match_id))
        combat.prepare()  # quick preparation, strength potion (stat mod) and bottle refill
        combat.say(0, '[Challenge 0x%X 1 training any arena:1' % serials[1])
        for p in clients:
            rpc(p, {'cmd': 'pump', 'ms': 500})
        combat.say(1, '[Accept')
        phases = []
        while True:
            remaining()
            if server.poll() is not None:
                raise RuntimeError('shard exited during match')
            match_tick(clients, rpc, lambda: finalized(shard, match_id))
            snapshot = feed()
            for match in snapshot.get('live', []):
                if match_id and match['id'] != match_id:
                    raise ValueError('more than one match observed')
                match_id = match['id']
                if match['phase'] not in phases:
                    phases.append(match['phase'])
                    if match['phase'] == 'fighting':
                        combat.wait(.5)
                        report['round_reset_strength'] = combat.observe(0)['player']['strength']
            if finalized(shard, match_id) and not snapshot.get('live'):
                break
            time.sleep(.5)
        report['phases'] = phases
        # 'roundover' lasts about one second before Finished and may be sampled.
        if phases[:2] != ['countdown', 'fighting'] or phases[2:] not in ([], ['roundover']):
            raise ValueError('did not observe complete round phases: %s' % phases)
        if report.get('round_reset_strength') != 100:
            raise ValueError('round start did not clear the strength modifier: %s' % report.get('round_reset_strength'))
        archive, manifest = wait_finalized(shard, match_id, remaining)
        with gzip.open(archive, 'rt') as stream:
            rows = [json.loads(line) for line in stream if line.strip()]
        header, end = receipt(rows, serials)
        metadata = json.loads(manifest.read_text())
        raw = hashlib.sha256(gzip.decompress(archive.read_bytes())).hexdigest()
        if header['id'] != match_id or metadata.get('id') != match_id or metadata.get('sha256') != raw or metadata.get('complete') is not True:
            raise ValueError('replay identity/checksum/completion mismatch')
        report.update(match_id=match_id, replay_rows=len(rows), raw_sha256=raw, replay_end=end)
        probe.wait(1.0)
        report['backpack_at_shutdown'] = backpack(probe)
        for p in clients:
            p.stdin.close()
        # Operator-equivalent saving stop: console `shutdown` saves then exits.
        report['save_started'] = int(time.time())
        server.stdin.write(b'shutdown\n')
        server.wait(timeout=remaining(60))
        report['server_exit'] = server.returncode
        report['saves'] = saves_digest(shard)
        if not report['saves']['files']:
            raise ValueError('shutdown did not write a world save')
        report['passed'] = True
        return report
    except Exception as exc:
        report['error'] = type(exc).__name__+': '+str(exc)
        exc.phase_report = report
        raise
    finally:
        for p in reversed(processes):
            if p.poll() is None:
                p.terminate()
            try:
                p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                p.kill();p.wait(timeout=10)
        report['processes_stopped'] = all(p.poll() is not None for p in processes)
        report['duration_seconds'] = round(time.monotonic()-started, 1)
        for handle in handles:
            handle.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--baseline', required=True, type=Path, help='deployed-equivalent set (tools/historical_lineage.py output)')
    parser.add_argument('--candidate', required=True, type=Path, help='candidate set (tools/build_provenance.py build1)')
    parser.add_argument('--source', required=True, type=Path, help='committed export supplying Config/Data/ServUO.exe.config')
    parser.add_argument('--bridge', required=True, type=Path)
    parser.add_argument('--data', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--port', type=int, default=12698)
    parser.add_argument('--feed-port', type=int, default=18100)
    args = parser.parse_args()
    install_sigterm()
    baseline, candidate = args.baseline.resolve(), args.candidate.resolve()
    if {n: sha(baseline/n) for n in SET} != BASELINE or {n: sha(candidate/n) for n in SET} != CANDIDATE:
        raise ValueError('baseline/candidate sets are not the pinned deployed-equivalent/candidate hashes')
    args.output.mkdir(parents=True, exist_ok=False)
    out = args.output.resolve()
    shard, backup = out/'shard', out/'rollout-backup'
    (out/'baseline.json').write_text(json.dumps({'assemblies': BASELINE}))
    (out/'candidate.json').write_text(json.dumps({'assemblies': CANDIDATE}))
    bridge, data = args.bridge.resolve(), args.data.resolve()
    report = {'schema': 1, 'passed': False, 'scope': 'one disposable loopback shard; no remote host contacted',
              'harness_sha256': sha(Path(__file__)), 'tool_sha256': sha(TOOL), 'bridge_sha256': sha(bridge),
              'baseline': BASELINE, 'candidate': CANDIDATE, 'port': args.port, 'feed_port': args.feed_port, 'steps': []}
    common = (bridge, data, args.port, args.feed_port)
    steps = report['steps']

    def step(kind, value):
        steps.append({'step': kind, **value} if isinstance(value, dict) else {'step': kind, 'result': value})
        (out/'rehearsal.json').write_text(json.dumps(report, indent=2)+'\n')
        return value

    def must(entry):
        if entry['exit'] != 0:
            raise RuntimeError('rollout tool failed: '+json.dumps(entry))
        return entry
    try:
        report['config_overrides'] = provision(shard, baseline, args.source.resolve(), data, args.port, args.feed_port)
        st = os.stat(str(shard/'ServUO.exe'))  # BSD /tmp gives files the directory's group, not the user's
        report['provisioned_owner'] = '%d:%d' % (st.st_uid, st.st_gid)
        h0 = step('health', health(shard, 'h0-baseline', *common, BASELINE, None, None))
        serials = h0['serials']
        step('verify-baseline', must(tool('verify', '--shard-dir', shard, '--manifest', out/'baseline.json')))
        step('snapshot', must(tool('snapshot', '--shard-dir', shard, '--backup-dir', backup, '--saved-after', h0['save_started'])))
        snapshot_saves = saves_digest(backup)
        # Negative gate: the tool must refuse a swap whose "current" manifest is wrong.
        refused = tool('swap', '--shard-dir', shard, '--backup-dir', backup, '--candidate-dir', candidate,
                       '--expect-current', out/'candidate.json', '--expect-candidate', out/'candidate.json')
        step('swap-refused-on-wrong-current', refused)
        if refused['exit'] == 0 or {n: sha(shard/n) for n in SET} != BASELINE:
            raise RuntimeError('swap guard did not refuse an unexpected current set')
        step('swap', must(tool('swap', '--shard-dir', shard, '--backup-dir', backup, '--candidate-dir', candidate,
                               '--expect-current', out/'baseline.json', '--expect-candidate', out/'candidate.json')))
        h1 = step('health', health(shard, 'h1-candidate', *common, CANDIDATE, serials, h0['backpack_at_shutdown']))
        # Rehearsed trigger: the operator decides to roll back. Keep candidate-era saves first.
        step('rollback-assemblies', must(tool('rollback', '--shard-dir', shard, '--backup-dir', backup, '--expect-restore', out/'baseline.json')))
        h2 = step('health', health(shard, 'h2-rolled-back-candidate-saves', *common, BASELINE, serials, h1['backpack_at_shutdown']))
        # Full fallback: restore pre-rollout Config/Saves too; the world returns to h0's save.
        restore = step('rollback-full-state', must(tool('rollback', '--shard-dir', shard, '--backup-dir', backup, '--expect-restore', out/'baseline.json', '--restore-state')))
        restored = saves_digest(shard)
        step('restored-saves-identical-to-snapshot', {'snapshot': snapshot_saves, 'restored': restored, 'match': restored == snapshot_saves})
        if restored != snapshot_saves:
            raise RuntimeError('restored saves differ from snapshot')
        h3 = step('health', health(shard, 'h3-rolled-back-snapshot-state', *common, BASELINE, serials, h0['backpack_at_shutdown']))
        step('final-verify', must(tool('verify', '--shard-dir', shard, '--manifest', out/'baseline.json', '--owner', report['provisioned_owner'])))
        report['kept_aside'] = restore['stdout'].get('kept_aside')
        report['passed'] = all(s.get('passed', True) for s in steps if s['step'] == 'health') and h3['passed']
    except Exception as exc:
        report['error'] = type(exc).__name__+': '+str(exc)
        if getattr(exc, 'phase_report', None):
            steps.append({'step': 'health', **exc.phase_report})
        raise
    finally:
        leftovers = subprocess.run(['pgrep', '-f', str(shard/'ServUO.exe')], capture_output=True, text=True).stdout.split()
        report['leftover_processes'] = leftovers
        (out/'rehearsal.json').write_text(json.dumps(report, indent=2)+'\n')
    print(out/'rehearsal.json')


if __name__ == '__main__':
    main()
