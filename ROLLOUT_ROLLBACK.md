# Public shard rollout and rollback procedure (ready for review, 2026-11-19)

Status: **not executed on the public shard.** Issue uotavern/uo#10 requires this
evidence to be reviewed before any live replacement. The procedure was
rehearsed end to end on one disposable loopback shard only
(`tests/provenance/rollback_rehearsal.py`, receipt
`tests/provenance/rollback-rehearsal-20261119.json`).

## 1. Artifact identities

The three assemblies are one unit. Never replace only one of them.

| Set | ServUO.exe | Scripts.dll | Ultima.dll |
| --- | --- | --- | --- |
| **Current public** (deployment map 2026-11-17) | `3a44e60b9edda4c9fb8bba313cb9950c394d2a9d24c6ab315b008549127b3165` | `b30c89378266c7b0a7a2f55a162d979afecc5d7084ebc1782f338c91074012f9` | `2adeee941bce23f3b4cb8fb465d7d5d796061e2e84852a1b61b14268dcd88a17` |
| **Candidate A** (accepted fixtures, `a15af9a`, `tools/build_provenance.py`) | `cce39e129079ab0edc0adb6b527ed97f9c9b5e908d47bd5a4d212bf4852941df` | `4257bf06bbf5dc04272b6009746b034eabc62f76cca0e0f1c2a0e9f1fcfa3a0a` | `8282a2a2c2777649daf86d3628331cb065b1736fbff088eb6fc5e89544714e87` |
| Candidate B (same `a15af9a`, legacy recipe; *no live-fixture evidence yet*) | `13c0a1884498b72f495277092431a0f8e7fa5228d8acb0fe1f46b4756900b5db` | `18107578345235bacf46dea053cd134a046b44f6583fa5f06db8c148682ca0c1` | `2adeee941bce23f3b4cb8fb465d7d5d796061e2e84852a1b61b14268dcd88a17` |

### Lineage of the current public set (reproduced byte for byte)

`tools/historical_lineage.py` (receipt `tests/provenance/historical-lineage-20261119.json`):

1. `git archive baf481bfbb0c98fc95d82395a895ce194e03498f` (branch tips
   `feat/arena-service` / `feat/arena-cosmetics`, committed 2026-09-29 00:45 +0200).
2. Legacy recipe = the makefile's build step without running the shard:
   `dotnet build ServUO.sln -c Release -p:Platform=x64` with the
   auto-referenced NuGet `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3.
   Roslyn `Deterministic` is on by default (PE debug directory holds only a
   Reproducible entry, no PDB path; linker version 48.0 = Roslyn).
3. Two independent exports both yield ServUO.exe `49fd952b…`, Scripts.dll
   `b03a253b…`, Ultima.dll `2adeee94…` — the exact preserved unpatched inputs.
4. Committed binary patches: `tools/PatchTrainingReset.cs` → `e05037e4…`,
   `tools/PatchSupplyBottles.cs` → `b30c8937…` (public Scripts.dll),
   `tests/network/PatchSendHandoff.cs` → `3a44e60b…` (public ServUO.exe).
   Ultima.dll is deployed unpatched.

Original-toolchain evidence: the preserved worktree build outputs'
`obj/project.assets.json` name `.NET SDK 10.0.401` (NuGet 7.0.0); the
reproduction used SDK 11.0.100 and still matched, so this source is
insensitive to that compiler change. Control: commit `e9dae69` rebuilt to its
own preserved Scripts.dll `aa160edf…`.

Why candidate A differs from the deployed bytes even where source is equal
(Ultima/ is identical in every commit): `tools/build_provenance.py` compiles
against Mono's `4.8-api` reference assemblies (`FrameworkPathOverride`) rather
than the NuGet reference package. Building `baf481b` with exactly those flags
gives Ultima.dll `8282a2a2…`; adding only `PathMap` changes nothing. This is
an explained, deterministic difference — not build nondeterminism.

Candidate B removes that reference-assembly difference (Ultima.dll unchanged
from production) but has not run the live combat/lifecycle fixtures. Reviewers
choose A (more evidence) or require B to pass `local_lifecycle.py` first.

## 2. Preconditions (all required, record each in the rollout log)

1. Review approval URL for this procedure and the chosen candidate set.
2. Fresh read-only artifact map on the host:
   `sha256sum /opt/uoarena/shard/{ServUO.exe,Scripts.dll,Ultima.dll}` equals
   the "current public" row. Anything else: **stop** (`shard_rollout.py swap`
   also refuses).
3. Candidate staged on the host in a new directory (e.g.
   `/opt/uoarena/rollout-<UTC>/candidate/`), hashes verified there.
4. Daily backup present and < 36 h old (`/var/backups/uoarena`); it covers
   `Config`, `Saves`, `Export` but **not** the assemblies.
5. Provider-level disk snapshot of the host taken (outside this tool).
6. Feed `GET /duel/` shows `live: []` (no running match); announce the window.
7. `/usr/bin/python3` and `pgrep` present (tool is stdlib-only, Python ≥ 3.8).

## 3. Rollout

```sh
R=/opt/uoarena/rollout-<UTC>; APPROVAL=<review URL>
# a. Save the world while running (staff [Save in game; the systemd service has no console).
#    Then stop: SIGTERM -> ProcessExit -> HandleClosed does NOT save by itself.
systemctl stop arena-shard.service
# b. Snapshot assemblies + ServUO.exe.config + Config + Saves (refuses if running)
python3 tools/shard_rollout.py snapshot --shard-dir /opt/uoarena/shard \
  --backup-dir $R/backup --live-approval $APPROVAL
# c. Swap the set (verifies snapshot == current == expected, candidate == expected,
#    stages temporaries, renames, re-hashes)
python3 tools/shard_rollout.py swap --shard-dir /opt/uoarena/shard --backup-dir $R/backup \
  --candidate-dir $R/candidate --expect-current $R/current.json \
  --expect-candidate $R/candidate.json --live-approval $APPROVAL
systemctl start arena-shard.service
```

`current.json` / `candidate.json` are `{"assemblies": {name: sha256}}` with the
rows above.

## 4. Health checks (after rollout and after any rollback)

1. `systemctl is-active arena-shard.service` and the journal shows world load
   with no exception; no new file under `Crashes/`.
2. Hashes: `shard_rollout.py verify --manifest <expected>.json` exits 0.
3. Protocol: game port accepts TCP; `/duel/` returns JSON; a designated test
   account logs in through the normal login → server list → character → game
   sequence (anima-bridge `event=ready`, schema 33) and the character/backpack
   that existed before the swap is present.
4. Round reset: test character uses quick preparation and drinks a Strength
   potion (STR > 100), refill moves the empty bottle out of the backpack, and a
   1-round **training** challenge between two test accounts reaches
   `countdown → fighting` with STR back to 100 at fighting.
5. Replay: that match's `Export/DuelReplays/<id>.meta.json` exists, its
   `sha256` equals the decompressed archive and `complete` is true.

Steps 3–5 create accounts/characters/replays only on the disposable fixture.
On the public shard, which accounts are used and whether the training replay
may appear publicly is a **review decision** (see §7).

## 5. Rollback triggers

Roll back immediately when any of these occurs within the observation window
(proposed: 30 minutes after start):

- service not active within 120 s, or a crash/exception during world load;
- any health check in §4 fails, or the hash verify fails;
- login failures, disconnect storms or a match that does not finalize its replay;
- a player-visible regression confirmed by the operator.

## 6. Rollback

```sh
systemctl stop arena-shard.service          # after a staff [Save if the shard is healthy enough
# Default: restore the assemblies only, keep candidate-era Config/Saves.
python3 tools/shard_rollout.py rollback --shard-dir /opt/uoarena/shard \
  --backup-dir $R/backup --live-approval $APPROVAL
systemctl start arena-shard.service          # then repeat §4 against current.json
# Fallback if the current set cannot load the candidate-era saves:
systemctl stop arena-shard.service
python3 tools/shard_rollout.py rollback --shard-dir /opt/uoarena/shard \
  --backup-dir $R/backup --restore-state --live-approval $APPROVAL
systemctl start arena-shard.service          # then repeat §4
```

Assemblies-only rollback is safe for this candidate because `baf481b..a15af9a`
touches only `ArenaSupplies.Refill`, `DuelMatch.PrepareFighter` and
`NetState` send handling — no Serialize/Deserialize or save-version change.
`--restore-state` loses everything since the snapshot (accounts, items,
ladder); candidate-era state is kept aside as `Saves.rollback-aside-<epoch>`.

## 7. What a human must review before any live use

- That §1's "current public" hashes are still what is on disk (re-hash; the
  last read-only observation is 2026-11-17) and that the running process was
  started from those files after their mtime.
- Candidate A vs B choice; if B, run `local_lifecycle.py --combat` against it first.
- The world-save step: who issues staff `[Save` and confirms "World: Saved"
  before `systemctl stop`; what `uoarena-backup` covers in addition.
- Test accounts/characters for §4 on the public shard and whether their
  training replay may be visible on the public feed.
- Observation window length and who decides a player-visible regression.
- Provider snapshot step and its restore drill (outside this repository).
- This document and the tool are reviewed at a specific commit; run that commit.

## 8. Disposable rehearsal result (2026-11-19)

One loopback shard (ports 12698/18100), one persistent world directory, pinned
release anima-bridge `f8e3eca9…`. All four health phases passed; no process
was left running. Each phase: startup ≈1 s to feed, login of both fixture
accounts, quick preparation, Strength potion 100→120, bottle refill removed
the empty bottle from the backpack, training round `countdown → fighting →
roundover` with STR back to 100 at fighting, 150–151-row complete replay with
matching metadata checksum, then console `shutdown` (save + self-kill, exit −9).

| Phase | Installed set (ServUO/Scripts/Ultima) | Persistence check |
| --- | --- | --- |
| h0 current | `3a44e60b` / `b30c8937` / `2adeee94` | new characters 2, 3 |
| snapshot → refused swap with wrong manifest → swap | — | set unchanged after refusal |
| h1 candidate A | `cce39e12` / `4257bf06` / `8282a2a2` | same serials; backpack = h0 shutdown |
| rollback (assemblies only) | `3a44e60b` / `b30c8937` / `2adeee94` | loaded candidate-era save; backpack = h1 shutdown |
| rollback `--restore-state` | — | restored Saves digest identical to snapshot |
| h3 current, snapshot state | `3a44e60b` / `b30c8937` / `2adeee94` | backpack = h0 shutdown; final verify exit 0 |

The fixture's "current" set is the byte-identical reconstruction from §1, not
a copy of the host files. The rehearsal does not cover systemd, the host's
real saves/accounts volume, client versions other than anima-bridge, or load.
