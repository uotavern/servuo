#!/usr/bin/env python3
"""Run offline compiled reset/send checks; optionally reconstruct preserved patches."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def run(argv, cwd=None, env=None):
    return subprocess.check_output([str(x) for x in argv], cwd=cwd, env=env, text=True, stderr=subprocess.STDOUT, timeout=60)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--artifacts', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--legacy-scripts', type=Path)
    p.add_argument('--legacy-server', type=Path)
    p.add_argument('--cecil', type=Path)
    args = p.parse_args()
    if any((args.legacy_scripts, args.legacy_server, args.cecil)) and not all((args.legacy_scripts, args.legacy_server, args.cecil)):
        p.error('legacy reconstruction requires both inputs and Cecil')
    args.output.mkdir(parents=True, exist_ok=False)
    out = args.output.resolve()
    artifacts = args.artifacts.resolve()
    report = {'scope': 'in-memory training fixture and loopback send interleaving; no full shard login', 'checks': {}, 'artifact_sha256': {name: sha(artifacts/name) for name in ('ServUO.exe', 'Scripts.dll', 'Ultima.dll')}, 'harness_sha256': {name: sha(ROOT/name) for name in ('tests/provenance/CheckRoundReset.cs', 'tests/network/CheckPatchedSend.cs', 'tests/provenance/check_artifacts.py')}}
    env = dict(os.environ, MONO_PATH=str(artifacts))
    for name, source, refs in [
        ('reset', ROOT/'tests/provenance/CheckRoundReset.cs', ['ServUO.exe', 'Scripts.dll']),
        ('send', ROOT/'tests/network/CheckPatchedSend.cs', []),
    ]:
        run(['mcs', '-out:'+str(out/(name+'.exe'))]+['-r:'+str(artifacts/x) for x in refs]+[source])
        command = ['mono', out/(name+'.exe')]
        if name == 'send':
            command.append(artifacts/'ServUO.exe')
        report['checks'][name] = run(command, cwd=out, env=env)
    if args.cecil:
        expected_inputs = [(args.legacy_scripts, 'b03a253bb85b1ead9eff732dab7e8749e9e3a38476351ccfaa149dfa678225b0'),
                           (args.legacy_server, '49fd952b952e11939c194c3cc8fdec03309f14a19e0d7a8a0eece750dbc10898')]
        for path, expected in expected_inputs:
            if sha(path) != expected:
                raise ValueError('legacy input checksum mismatch: '+str(path))
        shutil.copy2(args.legacy_scripts, out/'Scripts.before.dll')
        shutil.copy2(args.legacy_server, out/'ServUO.exe')
        shutil.copy2(artifacts/'Ultima.dll', out/'Ultima.dll')
        patches = [
            ('resetpatch', 'tools/PatchTrainingReset.cs', 'Scripts.before.dll', 'Scripts.reset.dll', 'e05037e49859138f14fae1b091dbb7f1b753501278ef0385d087fc6b3c6cfb02'),
            ('bottlespatch', 'tools/PatchSupplyBottles.cs', 'Scripts.reset.dll', 'Scripts.dll', 'b30c89378266c7b0a7a2f55a162d979afecc5d7084ebc1782f338c91074012f9'),
            ('sendpatch', 'tests/network/PatchSendHandoff.cs', 'ServUO.exe', 'ServUO.patched.exe', '3a44e60b9edda4c9fb8bba313cb9950c394d2a9d24c6ab315b008549127b3165'),
        ]
        report['cecil_sha256'] = sha(args.cecil)
        report['patches'] = []
        for name, source, before, after, expected in patches:
            run(['mcs', '-r:'+str(args.cecil.resolve()), '-out:'+str(out/(name+'.exe')), ROOT/source])
            log = run(['mono', out/(name+'.exe'), out/before, out/after], cwd=out)
            actual = sha(out/after)
            if actual != expected:
                raise ValueError('patch byte identity mismatch: '+after)
            report['patches'].append({'source': source, 'source_sha256': sha(ROOT/source), 'output_sha256': actual, 'log': log})
    (out/'checks.json').write_text(json.dumps(report, indent=2)+'\n')
    print(out/'checks.json')


if __name__ == '__main__':
    main()
