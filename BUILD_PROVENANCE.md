# Isolated ServUO build provenance (2026-11-18)

This recipe does not start, install, or restart a shard. It addresses issue uotavern/uo#10 with two distinct results: a reproducible clean source build, and byte-identical reconstruction of the preserved production patch chain. These are **different binaries**. Neither result authorizes a production replacement.

## Source identity and prerequisites

Tested source commit `a15af9a0d6b2a106fcad5eb8b9ff16b861e3d184`, tree `bc3fb4e6441f0642d130fd49aa820b51e791ae85`, preserved remotely as `archive/training-reset-source-20261118` (original `fix/training-round-reset`), includes Arena service/reset/supply/send-handoff prerequisites. Comparison to primary `pub57` found the combat fix patch-equivalent, and the feed introduction represented by evolved Arena source: DuelWeb.cs and Config/Duel.cfg identical; DuelMatch/DuelSystem retain the feed with Arena lifecycle hooks. Blindly cherry-picking the old feed commit would overwrite later behavior.

`git archive` exports exactly committed files, excluding `.git`, untracked saves/data, ignored binaries and working-tree configuration edits. Committed Config and Data remain included. Non-regular archive entries (including symlinks) are rejected. The build mutates only the new disposable output directories. Never run the default Makefile target: it starts the shard.

Validated host: macOS arm64, .NET SDK 11.0.100, Mono/mcs 6.14.1, Mono .NET 4.8 API/reference frameworks, Mono.Cecil 0.11.1.0. NuGet restored cached Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3 and System.Data.DataSetExtensions 4.5.0; complete resolved libraries and SDK details are in `tests/provenance/source-build-20261118.json`. `--ignore-failed-sources` permits cache reuse, not a guarantee of offline availability. This does not pin a downloadable OS/container image.

```sh
python3 tools/build_provenance.py \
  --revision a15af9a0d6b2a106fcad5eb8b9ff16b861e3d184 \
  --output /tmp/servuo-source-build-new \
  --framework-root /opt/homebrew/lib/mono/xbuild-frameworks \
  --framework-api /opt/homebrew/lib/mono/4.8-api
```

Fetch the preserved source first: `git fetch origin archive/training-reset-source-20261118`. The recipe pins the immutable commit, not the movable branch tip. Python 3.9+, git, dotnet, mono and mcs must be on PATH. Paths are host-specific explicit inputs. The output must not exist. Two independent clean exports build Release/x64/net48 with deterministic compilation and a common `PathMap`. An independent reviewer reran the recipe in another output directory and obtained the same six hashes. Both builds passed with zero warnings/errors and identical SHA-256 for all three binaries. This establishes same-toolchain repeatability, not cross-toolchain reproducibility. New source-built hashes:

| Artifact | SHA-256 |
| --- | --- |
| ServUO.exe | cce39e129079ab0edc0adb6b527ed97f9c9b5e908d47bd5a4d212bf4852941df |
| Scripts.dll | 4257bf06bbf5dc04272b6009746b034eabc62f76cca0e0f1c2a0e9f1fcfa3a0a |
| Ultima.dll | 8282a2a2c2777649daf86d3628331cb065b1736fbff088eb6fc5e89544714e87 |

## Artifact checks and preserved patch reconstruction

```sh
python3 tests/provenance/check_artifacts.py \
  --artifacts /tmp/servuo-source-build-new/build1 \
  --output /tmp/servuo-checks-new \
  --legacy-scripts ../servuo-cosmetics/Scripts.dll \
  --legacy-server ../servuo/ServUO.exe \
  --cecil /opt/homebrew/lib/mono/gac/Mono.Cecil/0.11.1.0__0738eb9f132ed756/Mono.Cecil.dll
python3 -m unittest discover -s tests/provenance -v
```

The three legacy flags are optional as a group. Supply preserved local artifacts, never live output paths. Inputs are pinned by checksum before patching; originals are copied and not modified. Existing patch programs compare all existing method bodies to constrain changes. Locally reconstructed reset intermediate `e05037e...`, final Scripts.dll `b30c89378266c7b0a7a2f55a162d979afecc5d7084ebc1782f338c91074012f9` and ServUO.exe `3a44e60b9edda4c9fb8bba313cb9950c394d2a9d24c6ab315b008549127b3165` exactly match the deployment-map preserved chain. This reconstructs patches from preserved binary inputs; the original compiler provenance of those inputs remains unknown.

The compiled reset fixture creates isolated in-memory maps/world/mobile, reduces HP/stamina/mana, applies a strength curse and stun/disarm flags, invokes the actual built PrepareFighter, and asserts stat-mod removal, readiness reset and full resources. No saves, production configuration or game listener are loaded. The compiled send fixture uses an ephemeral loopback socket to test queued output under sender contention. The separately run candidate-source `tests/network/check_send_handoff.py` regression (in the preserved source worktree, not this tooling-only branch) reproduces the old stalled queue and confirms fixed behavior (two expected harness compiler warnings). Two Python tests cover committed-only exports and rejection of symlink entries.

These checks do not cover a complete client login, every protocol packet, a full scheduled round lifecycle, bottle inventory behavior, replay generation or all Arena rules. Such isolated shard acceptance with disposable client data remains a prerequisite before any deployment; this bounded unit does not close that gate.

## Rollback and review boundary

No live files/processes changed, so no operational rollback is needed. Remove only explicitly chosen disposable output directories if desired. A future reviewed deployment must preserve the current ServUO.exe/Scripts.dll pair, their hashes and configuration; stop the candidate, restore that pair together, and revalidate login/round/replay behavior before restoring traffic. Do not replace only one assembly based on these tests. Historical deployed identity is documented in the workspace deployment map, not inferred from repository names.
