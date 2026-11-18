#!/usr/bin/env python3
"""Build a pinned committed source export twice; never starts or deploys a shard."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile

ARTIFACTS = ('ServUO.exe', 'Scripts.dll', 'Ultima.dll')


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(argv, cwd=None):
    return subprocess.check_output(argv, cwd=cwd, text=True, stderr=subprocess.STDOUT)


def export(repo, revision, destination):
    data = subprocess.check_output(['git', 'archive', '--format=tar', revision], cwd=repo)
    with tarfile.open(fileobj=io.BytesIO(data)) as archive:
        for member in archive:
            target = destination / member.name
            if member.name.startswith('/') or '..' in Path(member.name).parts:
                raise ValueError('unsafe archive path')
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.extractfile(member).read())
                target.chmod(member.mode & 0o777)
            else:
                raise ValueError('unsupported non-regular archive entry: '+member.name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--framework-root', required=True)
    parser.add_argument('--framework-api', required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    commit = run(['git', 'rev-parse', args.revision+'^{commit}'], repo).strip()
    tree = run(['git', 'rev-parse', commit+'^{tree}'], repo).strip()
    # Refuse reused directories, including symlinks, before any builds.
    args.output.mkdir(parents=True, exist_ok=False)
    output = args.output.resolve()
    report = {'schema_version': 1, 'source_commit': commit, 'source_tree': tree,
              'export': 'git archive: committed files only; no working-tree edits, ignored binaries, saves, or .git',
              'recipe_sha256': digest(Path(__file__)), 'dotnet_info': run(['dotnet', '--info']),
              'mono_version': run(['mono', '--version']), 'builds': [],
              'scope': 'source artifact reproducibility only; not deployed binary identity or full shard acceptance'}
    try:
        for number in (1, 2):
            directory = output / ('build'+str(number))
            directory.mkdir()
            export(repo, commit, directory)
            argv = ['dotnet', 'build', 'ServUO.sln', '-c', 'Release', '-p:Platform=x64',
                    '-p:TargetFrameworkRootPath='+str(Path(args.framework_root).resolve()),
                    '-p:FrameworkPathOverride='+str(Path(args.framework_api).resolve()),
                    '-p:Deterministic=true', '-p:PathMap='+str(directory)+'=/_/servuo',
                    '-p:NuGetAudit=false', '--ignore-failed-sources']
            env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT='1')
            with (output / ('build'+str(number)+'.log')).open('w') as log:
                subprocess.run(argv, cwd=directory, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
            report['builds'].append({'command': argv, 'artifacts': {name: digest(directory/name) for name in ARTIFACTS},
                'resolved_packages': {str(p.relative_to(directory)): json.loads(p.read_text()).get('libraries', {})
                                      for p in directory.glob('*/obj/project.assets.json')}})
        report['byte_identical'] = report['builds'][0]['artifacts'] == report['builds'][1]['artifacts']
        if not report['byte_identical']:
            raise RuntimeError('clean builds differ; inspect manifest before using artifacts')
    finally:
        (output/'manifest.json').write_text(json.dumps(report, indent=2)+'\n')
    print(output/'manifest.json')


if __name__ == '__main__':
    main()
