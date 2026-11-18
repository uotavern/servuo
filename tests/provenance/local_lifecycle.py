#!/usr/bin/env python3
"""One isolated loopback-only login/invite/round/replay fixture; never deploys."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import select
import shutil
import socket
import subprocess
import time
import urllib.request

PINNED = {'ServUO.exe': 'cce39e129079ab0edc0adb6b527ed97f9c9b5e908d47bd5a4d212bf4852941df',
          'Scripts.dll': '4257bf06bbf5dc04272b6009746b034eabc62f76cca0e0f1c2a0e9f1fcfa3a0a',
          'Ultima.dll': '8282a2a2c2777649daf86d3628331cb065b1736fbff088eb6fc5e89544714e87'}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def configure(path, replacements):
    remaining = dict(replacements)
    lines = path.read_text().splitlines()
    lines = [remaining.pop(line.split('=', 1)[0], line) if '=' in line else line for line in lines]
    path.write_text('\n'.join(lines+list(remaining.values()))+'\n')


def ensure_free(port):
    if not 1024 <= port <= 65535:
        raise ValueError('only unprivileged ports allowed')
    with socket.socket() as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(('127.0.0.1', port))


def receipt(rows, serials):
    if not rows or rows[0].get('type') != 'header':
        raise ValueError('missing replay header')
    header = rows[0]
    if [p['serial'] for p in header['players']] != serials or len(set(serials)) != 2:
        raise ValueError('replay participants differ from logged-in clients')
    ends = [r for r in rows if r.get('type') == 'end']
    if len(ends) != 1:
        raise ValueError('replay must have exactly one terminal row')
    end = ends[0]
    if rows[-1] is not end or end.get('id') != header.get('id'):
        raise ValueError('terminal identity/order mismatch')
    if end.get('complete') is not True or end.get('aborted') is not None or end.get('dropped') != 0:
        raise ValueError('incomplete/aborted/dropped replay')
    if header.get('rounds') != 1 or header.get('training') is not True or header.get('ranked') is not False:
        raise ValueError('unexpected match mode')
    if end.get('winner') is not None or end.get('score') != [0, 0]:
        raise ValueError('expected bounded timeout draw')
    if [r.get('seq') for r in rows] != list(range(len(rows))):
        raise ValueError('replay sequence gap')
    rounds = [r for r in rows if r.get('type') == 'round_end']
    if len(rounds) != 1 or rounds[0].get('reason') != 'time_limit' or rounds[0].get('round') != 1 or rounds[0].get('winner') is not None:
        raise ValueError('expected exactly one time-limit round')
    return header, end


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', required=True, type=Path)
    parser.add_argument('--bridge', required=True, type=Path)
    parser.add_argument('--data', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--port', type=int, default=12694)
    parser.add_argument('--feed-port', type=int, default=18096)
    args = parser.parse_args()
    build, bridge, data = args.build.resolve(), args.bridge.resolve(), args.data.resolve()
    for name, expected in PINNED.items():
        if sha(build/name) != expected:
            raise ValueError('unexpected candidate binary: '+name)
    if not bridge.is_file() or not data.is_dir():
        raise ValueError('bridge binary and licensed data directory required')
    if args.port == args.feed_port:
        raise ValueError('game/feed ports must differ')
    ensure_free(args.port)
    ensure_free(args.feed_port)
    args.output.mkdir(parents=True, exist_ok=False)
    out = args.output.resolve()
    for name in (*PINNED, 'ServUO.exe.config'):
        shutil.copy2(build/name, out/name)
    for name in ('Config', 'Data'):
        shutil.copytree(build/name, out/name, symlinks=False)
    config = out/'Config'
    settings = {
        'Server.cfg': {'Listen':'Listen=127.0.0.1', 'Address':'Address=127.0.0.1', 'Port':'Port='+str(args.port)},
        'Duel.cfg': {'WebHost':'WebHost=127.0.0.1', 'WebPort':'WebPort='+str(args.feed_port), 'ShowdownAfterSeconds':'ShowdownAfterSeconds=10', 'RoundTimeLimitSeconds':'RoundTimeLimitSeconds=20'},
        'Arena.cfg': {'Enabled':'Enabled=true', 'DisableRisingTide':'DisableRisingTide=true', 'SelfPlay':'SelfPlay=false', 'BotAccounts':'BotAccounts='},
        'AutoSave.cfg': {'Enabled':'Enabled=false'}, 'AutoRestart.cfg': {'Enabled':'Enabled=false'},
        'Accounts.cfg': {'AccountsPerIp':'AccountsPerIp=2', 'AutoCreateAccounts':'AutoCreateAccounts=true'},
        'DataPath.cfg': {'CustomPath':'CustomPath='+str(data)}, 'Compiler.cfg': {'Dynamic':'Dynamic=false'},
    }
    for name, values in settings.items():
        configure(config/name, values)
    processes, handles = [], []
    deadline = time.monotonic()+120
    report = {'passed': False, 'source_commit':'a15af9a0d6b2a106fcad5eb8b9ff16b861e3d184', 'binaries': PINNED,
              'bridge_sha256':sha(bridge), 'host':'127.0.0.1', 'port':args.port, 'feed_port':args.feed_port,
              'harness_sha256':sha(Path(__file__)), 'max_matches':1, 'deadline_seconds':120, 'config_overrides':settings, 'phases':[]}
    def remaining(limit=10):
        seconds = min(limit, deadline-time.monotonic())
        if seconds <= 0:
            raise TimeoutError('fixture deadline')
        return seconds
    def start(command, name, pipes=False):
        log = (out/(name+'.log')).open('w');handles.append(log)
        p = subprocess.Popen([str(x) for x in command], cwd=out, stdin=subprocess.PIPE if pipes else subprocess.DEVNULL,
                             stdout=subprocess.PIPE if pipes else log, stderr=log, bufsize=0)
        processes.append(p)
        return p
    buffers = {}
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
    def say(p, text):
        return rpc(p, {'cmd':'act', 'action':{'type':'Say', 'text':text}})
    def feed():
        with urllib.request.urlopen('http://127.0.0.1:'+str(args.feed_port)+'/duel/', timeout=remaining(3)) as response:
            return json.load(response)
    try:
        server = start(['mono', out/'ServUO.exe', '-noconsole', '-service'], 'server')
        while True:
            remaining()
            if server.poll() is not None:
                raise RuntimeError('shard exited during startup')
            try:
                initial = feed()
                if initial.get('recent') or initial.get('live'):
                    raise ValueError('fixture world must start empty')
                break
            except (OSError, urllib.error.URLError):
                time.sleep(.2)
        clients, serials = [], []
        for username in ('fixture_alpha', 'fixture_beta'):
            p = start([bridge, '127.0.0.1', str(args.port), username, 'local-fixture-only', data], username, True)
            ready = receive(p)
            if ready.get('event') != 'ready' or ready.get('schema_version') != 33:
                raise ValueError('bridge handshake failed')
            serials.append(ready['player']['serial']);clients.append(p)
        if len(set(serials)) != 2:
            raise ValueError('distinct character serials required')
        report['serials'] = serials
        say(clients[0], '[Challenge 0x%X 1 training any arena:1' % serials[1])
        for p in clients:
            rpc(p, {'cmd':'pump', 'ms':500})
        say(clients[1], '[Accept')
        match_id = None
        while True:
            remaining()
            if server.poll() is not None:
                raise RuntimeError('shard exited during match')
            for p in clients:
                rpc(p, {'cmd':'pump', 'ms':100})
            snapshot = feed()
            for match in snapshot.get('live', []):
                if match_id and match['id'] != match_id:
                    raise ValueError('more than one match observed')
                match_id = match['id']
                if match['phase'] not in report['phases']:
                    report['phases'].append(match['phase'])
            if match_id and (out/'Export'/'DuelReplays'/(match_id+'.jsonl.gz')).exists() and not snapshot.get('live'):
                report['final_feed'] = snapshot
                break
            time.sleep(.5)
        if report['phases'] != ['countdown', 'fighting']:
            raise ValueError('did not observe complete round phases')
        report['match_id'] = match_id
        archive = out/'Export'/'DuelReplays'/(match_id+'.jsonl.gz')
        while not archive.exists():
            remaining();time.sleep(.2)
        with gzip.open(archive, 'rt') as stream:
            rows = [json.loads(line) for line in stream if line.strip()]
        header, end = receipt(rows, serials)
        if header['id'] != match_id:
            raise ValueError('replay does not belong to observed match')
        metadata = json.loads(archive.with_name(match_id+'.meta.json').read_text())
        raw_sha = hashlib.sha256(gzip.decompress(archive.read_bytes())).hexdigest()
        if metadata.get('id') != match_id or metadata.get('sha256') != raw_sha or metadata.get('complete') is not True:
            raise ValueError('metadata identity/checksum/completion mismatch')
        report['raw_sha256'] = raw_sha
        report['replay_metadata'] = metadata
        report.update(replay_sha256=sha(archive), replay_header=header, replay_end=end, replay_rows=len(rows), passed=True)
    except Exception as exc:
        report['error'] = type(exc).__name__+': '+str(exc)
        raise
    finally:
        for p in reversed(processes):
            if p.poll() is None:
                p.terminate()
            try:
                p.wait(timeout=5)
            except subprocess.TimeoutExpired:
                p.kill();p.wait(timeout=5)
        report['processes_stopped'] = all(p.poll() is not None for p in processes)
        for handle in handles:
            handle.close()
        (out/'lifecycle.json').write_text(json.dumps(report, indent=2)+'\n')
    print(out/'lifecycle.json')


if __name__ == '__main__':
    main()
