#!/usr/bin/env python3
"""Rebuild the historical public-shard inputs and replay the preserved patch chain.

Never starts, installs or deploys a shard. Every output directory must be new.

The deployed public pair (see the workspace deployment map, 2026-11-17) is

  ServUO.exe  3a44e60b...  = PatchSendHandoff(ServUO.exe 49fd952b...)
  Scripts.dll b30c8937...  = PatchSupplyBottles(PatchTrainingReset(Scripts.dll b03a253b...))
  Ultima.dll  2adeee94...  (unpatched)

This tool reconstructs the unpatched inputs from a committed source export
using the *legacy* recipe (plain SDK build against the NuGet
Microsoft.NETFramework.ReferenceAssemblies.net48 package, i.e. what the
repository makefile's `dotnet build -c release` does), in two independent
exports, and then applies the three committed patch programs.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_provenance import export  # noqa: E402  (rejects symlinks / unsafe paths)

ARTIFACTS = ('ServUO.exe', 'Scripts.dll', 'Ultima.dll')
LEGACY_REVISION = 'baf481bfbb0c98fc95d82395a895ce194e03498f'
# Unpatched inputs preserved in local worktree build outputs (checked by
# tests/provenance/check_artifacts.py) plus the deployed unpatched Ultima.dll.
LEGACY_INPUTS = {
    'ServUO.exe': '49fd952b952e11939c194c3cc8fdec03309f14a19e0d7a8a0eece750dbc10898',
    'Scripts.dll': 'b03a253bb85b1ead9eff732dab7e8749e9e3a38476351ccfaa149dfa678225b0',
    'Ultima.dll': '2adeee941bce23f3b4cb8fb465d7d5d796061e2e84852a1b61b14268dcd88a17',
}
DEPLOYED = {
    'ServUO.exe': '3a44e60b9edda4c9fb8bba313cb9950c394d2a9d24c6ab315b008549127b3165',
    'Scripts.dll': 'b30c89378266c7b0a7a2f55a162d979afecc5d7084ebc1782f338c91074012f9',
    'Ultima.dll': '2adeee941bce23f3b4cb8fb465d7d5d796061e2e84852a1b61b14268dcd88a17',
}
PATCHES = (
    ('resetpatch', 'tools/PatchTrainingReset.cs', 'Scripts.before.dll', 'Scripts.reset.dll',
     'e05037e49859138f14fae1b091dbb7f1b753501278ef0385d087fc6b3c6cfb02'),
    ('bottlespatch', 'tools/PatchSupplyBottles.cs', 'Scripts.reset.dll', 'Scripts.dll', DEPLOYED['Scripts.dll']),
    ('sendpatch', 'tests/network/PatchSendHandoff.cs', 'ServUO.exe', 'ServUO.patched.exe', DEPLOYED['ServUO.exe']),
)
LEGACY_BUILD = ['dotnet', 'build', 'ServUO.sln', '-c', 'Release', '-p:Platform=x64', '-p:NuGetAudit=false']


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(argv, cwd=None, timeout=900):
    return subprocess.check_output([str(x) for x in argv], cwd=cwd, text=True,
                                   stderr=subprocess.STDOUT, timeout=timeout)


def ref_assemblies():
    """Hash the auto-referenced NuGet reference assemblies actually used."""
    root = Path(os.environ.get('NUGET_PACKAGES', Path.home()/'.nuget/packages'))
    pkg = root/'microsoft.netframework.referenceassemblies.net48'/'1.0.3'
    files = sorted(p for p in pkg.rglob('*') if p.is_file() and p.name in ('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll'))
    return {'package': 'Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3',
            'nupkg_sha512': next((p.read_text().strip() for p in pkg.glob('*.nupkg.sha512')), None),
            'sample_sha256': {str(p.relative_to(pkg)): digest(p) for p in files}}


def build(repo, commit, directory):
    directory.mkdir(parents=True)
    export(repo, commit, directory)
    log = run(LEGACY_BUILD, cwd=directory)
    (directory.parent/(directory.name+'.log')).write_text(log)
    if '0 Error(s)' not in log:
        raise RuntimeError('legacy build failed in '+str(directory))
    return {name: digest(directory/name) for name in ARTIFACTS}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--revision', default=LEGACY_REVISION)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--cecil', type=Path, required=True, help='Mono.Cecil.dll used by the patch programs')
    parser.add_argument('--no-patch', action='store_true', help='only rebuild and compare (control revisions)')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    commit = run(['git', 'rev-parse', args.revision+'^{commit}'], repo).strip()
    if args.output.exists() or args.output.is_symlink():
        raise SystemExit('output must not exist: '+str(args.output))
    args.output.mkdir(parents=True)
    out = args.output.resolve()
    report = {
        'schema': 1, 'scope': 'offline rebuild and patch replay; no shard started, no deployment',
        'revision': commit, 'tree': run(['git', 'rev-parse', commit+'^{tree}'], repo).strip(),
        'recipe_sha256': digest(__file__), 'build_command': LEGACY_BUILD,
        'host': {'platform': platform.platform(), 'machine': platform.machine()},
        'dotnet_sdk': run(['dotnet', '--version']).strip(),
        'dotnet_sdks': run(['dotnet', '--list-sdks']).strip().splitlines(),
        'mono': run(['mono', '--version']).splitlines()[0],
        'mcs': run(['mcs', '--version']).strip(),
        'reference_assemblies': ref_assemblies(),
        'cecil_sha256': digest(args.cecil),
        'builds': {},
    }
    for name in ('export1', 'export2'):
        report['builds'][name] = build(repo, commit, out/name)
    first, second = report['builds']['export1'], report['builds']['export2']
    report['exports_identical'] = first == second
    report['matches_legacy_inputs'] = {k: first[k] == v for k, v in LEGACY_INPUTS.items()}
    if not args.no_patch:
        if not report['exports_identical'] or not all(report['matches_legacy_inputs'].values()):
            report['passed'] = False
            (out/'lineage.json').write_text(json.dumps(report, indent=2)+'\n')
            raise SystemExit('rebuilt inputs differ from preserved legacy inputs; see '+str(out/'lineage.json'))
        stage = out/'deployed-equivalent'
        stage.mkdir()
        shutil.copy2(out/'export1'/'Scripts.dll', stage/'Scripts.before.dll')
        shutil.copy2(out/'export1'/'ServUO.exe', stage/'ServUO.exe')  # resolver needs the real name
        shutil.copy2(out/'export1'/'Ultima.dll', stage/'Ultima.dll')
        report['patches'] = []
        tools = out/'patch-tools'
        tools.mkdir()
        for name, source, before, after, expected in PATCHES:
            exe = tools/(name+'.exe')
            run(['mcs', '-r:'+str(args.cecil.resolve()), '-out:'+str(exe), repo/source])
            log = run(['mono', exe, stage/before, stage/after], cwd=stage)
            actual = digest(stage/after)
            report['patches'].append({'source': source, 'source_sha256': digest(repo/source), 'input_sha256': digest(stage/before),
                                      'output_sha256': actual, 'expected_sha256': expected, 'match': actual == expected, 'log': log})
        for leftover in ('Scripts.before.dll', 'Scripts.reset.dll'):
            (stage/leftover).rename(tools/leftover)
        (stage/'ServUO.exe').rename(tools/'ServUO.before.exe')
        (stage/'ServUO.patched.exe').rename(stage/'ServUO.exe')
        report['deployed_equivalent'] = {n: digest(stage/n) for n in ARTIFACTS}
        report['matches_deployed'] = {n: report['deployed_equivalent'][n] == DEPLOYED[n] for n in ARTIFACTS}
        report['passed'] = all(report['matches_deployed'].values()) and all(p['match'] for p in report['patches'])
    else:
        report['passed'] = report['exports_identical']
    (out/'lineage.json').write_text(json.dumps(report, indent=2)+'\n')
    print(out/'lineage.json')
    if not report['passed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
