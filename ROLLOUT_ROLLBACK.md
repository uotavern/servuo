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
2. Legacy recipe, exactly as `tools/historical_lineage.py` runs it in each
   export: `dotnet build ServUO.sln -c Release -p:Platform=x64 -p:NuGetAudit=false`
   (NuGet restore uses the auto-referenced
   `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3; no framework
   overrides, no PathMap). The makefile at `baf481b` runs `dotnet build -c release`
   (no solution argument, no platform, no NuGetAudit switch); that literal form
   was also run on a fresh `baf481b` export and gave the same three hashes
   (2026-11-19). `NuGetAudit=false` only suppresses the vulnerability-audit
   network query. Roslyn `Deterministic` is on by default (PE debug directory
   holds only a Reproducible entry, no PDB path; linker version 48.0 = Roslyn).
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

## 2. Host facts this procedure assumes (checked read-only by the coordinator, 2026-11-19)

- `arena-shard.service` (public) runs `/usr/bin/mono /opt/uoarena/shard/ServUO.exe -service`
  as `User=uoarena`. `anima3-lab-shard.service` runs a *different* shard from
  `/opt/uoarena/lab-shard`; never stop, snapshot or swap it as part of this
  procedure. The lab shard's cwd is `/opt/uoarena/lab-shard`, and both shards
  read client data from `DataPath.cfg` `CustomPath=/opt/uoarena/data`, which is
  outside `/opt/uoarena/shard`. The tool's running-shard check resolves paths
  and only counts files inside the target shard, so the lab shard (and its
  mapped client data) does not block it, and the public shard cannot hide
  behind a relative or symlinked path. A cwd or argument inside the shard
  counts only for mono/ServUO processes and never for the tool's own ancestor
  chain, so an operator shell parked in the directory or `sudo … --shard-dir`
  cannot block an emergency rollback; any process with a shard file open or
  mapped still does.
- The operator connects as `ssh root@host` (no sudo).
- `/opt/uoarena/shard/Saves` is a real directory, `uoarena:uoarena` 700 with
  600/700 entries; `Config` and `Export` are owned by `uoarena`; the
  assemblies are `uoarena` 644. The operator runs the tool as root, so it
  copies files with `O_EXCL|O_NOFOLLOW` and sets owner and mode from the
  file it replaces (swap) or from the snapshot (rollback), then verifies them.
- About 132 GB were free; the tool still checks free space (+10% +64 MiB) for
  the snapshot, staged assemblies and staged state.

## 3. Preconditions (all required; record each in the rollout log)

1. Review approval URL for this procedure, the chosen candidate set and the
   reviewed commit `<SHA>` of this repository.
2. The reviewed checkout is pinned on the host at
   `/opt/uoarena/rollout-tools/servuo-<SHA>` (owned by root, not writable by
   `uoarena`). Check it before every use:
   ```sh
   T=/opt/uoarena/rollout-tools/servuo-<SHA>
   git -C $T rev-parse HEAD                                 # == <SHA>
   git -C $T status --porcelain                             # empty
   sha256sum $T/tools/shard_rollout.py                      # == tool_sha256 in
   python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["tool_sha256"])' \
     $T/tests/provenance/rollback-rehearsal-20261119.json
   ```
3. Manifests are generated from the reviewed receipt, never typed:
   ```sh
   R=/opt/uoarena/rollout-$(date -u +%Y%m%dT%H%M%SZ); mkdir -m 700 $R
   python3 $T/tools/shard_rollout.py manifest --from-receipt \
     $T/tests/provenance/rollback-rehearsal-20261119.json --key baseline  --output $R/current.json
   python3 $T/tools/shard_rollout.py manifest --from-receipt \
     $T/tests/provenance/rollback-rehearsal-20261119.json --key candidate --output $R/candidate.json
   ```
   (For candidate B, generate `candidate.json` from its reviewed receipt instead.)
4. Fresh read-only check that the host still holds the current set and owners:
   `python3 $T/tools/shard_rollout.py verify --shard-dir /opt/uoarena/shard --manifest $R/current.json --owner $(id -u uoarena):$(id -g uoarena)`
   must exit 0. Anything else: **stop**.
5. Candidate copied to `$R/candidate/` and checked:
   `python3 $T/tools/shard_rollout.py verify --shard-dir $R/candidate --manifest $R/candidate.json` exits 0.
6. Free space: `df -h /opt/uoarena` shows at least twice `du -sh /opt/uoarena/shard/{Saves,Config}`.
7. Daily backup present and < 36 h old in `/var/backups/uoarena` (it covers
   `Config`, `Saves`, `Export`, not the assemblies) and a provider-level disk
   snapshot taken.
8. `Config/AutoRestart.cfg` has `Enabled=False` (if not, note the next restart
   time and stay clear of it). The public `Config/AutoSave.cfg` is
   `Enabled=True`, `Frequency=00:00:05:00`, `WarningTime=00:00:00:10`: a timed
   save starts every 5 minutes, 10 s after a broadcast warning. §4 b turns
   timed saves off in the running process (`[SetSaves false`) before the
   operator save so none can start between `[Save` and the stop.
   `SetSaves` is not persisted; the next start runs timed saves again.
9. `/usr/bin/python3` ≥ 3.8, `pgrep`, `systemctl` present. Run as root
   (`ssh root@host`); under `sudo` the tool ignores its own ancestor chain
   (sudo, the login shell) when checking that the shard is stopped.
10. Two operator test accounts exist, e.g. `ops-health-a` and `ops-health-b`
    (names are a review decision; never player accounts). Each holds a
    **key character** with character AccessLevel Counselor (lets the account
    log in under a Counselor Lockdown: `Account.HasAccess` admits an account if
    any of its characters is at or above the lockdown level) and a **fighter
    character** at AccessLevel Player in the slot the health client selects
    (Arena preparation, supplies and challenges require a Player-level
    character). Plus the Administrator account `<ADMIN>` that applies Lockdown
    and runs `[Save`/`[SetSaves`.

## 4. Rollout

ServUO under systemd has no console, and the in-game `[Shutdown` saves and then
kills itself, which systemd may treat as a failure and restart. So the save is
done in game, verified on disk, and only then is the unit stopped:

```sh
# a. Quiesce. As <ADMIN> in game: [Admin -> Administer -> Access -> Lockdown ->
#    Counselor (player accounts can no longer log in); kick or wait out any
#    connected player; confirm the client list shows only staff and
#    GET /duel/ shows "live": [].
# b. Save. In game: [SetSaves false   (no timed save can start from now on; if the
#    10 s "The world will save in" warning was just broadcast, wait for that
#    save to finish first). Record T_SAVE (UTC) just *before* issuing [Save, then
#    [Save. Require both journal lines and fresh core save files:
journalctl -u arena-shard.service --since "<T_SAVE>" | grep -E "World: Saving|Save finished"
find /opt/uoarena/shard/Saves/Mobiles /opt/uoarena/shard/Saves/Items -type f -printf '%TY-%Tm-%TdT%TH:%TM:%TS %p\n'
# c. Stop immediately after. Then prove no save ran after the recorded one: the
#    last "World: Saving" in the journal must be the one belonging to T_SAVE.
systemctl stop arena-shard.service
journalctl -u arena-shard.service --since "<T_SAVE>" | grep -E "World: Saving|Save finished"   # exactly one pair
# d. Snapshot: refuses unless the unit is inactive/failed, no process uses the
#    shard, every Saves/Mobiles and Saves/Items file is newer than T_SAVE,
#    nothing is symlinked and there is space.
python3 $T/tools/shard_rollout.py snapshot --shard-dir /opt/uoarena/shard \
  --backup-dir $R/backup --saved-after <T_SAVE> --unit arena-shard.service --live-approval <APPROVAL>
# e. Swap the set atomically-or-not-at-all (on any failure the previous set is
#    put back and the tool exits non-zero; owner/mode copied from the old files).
python3 $T/tools/shard_rollout.py swap --shard-dir /opt/uoarena/shard --backup-dir $R/backup \
  --candidate-dir $R/candidate --expect-current $R/current.json --expect-candidate $R/candidate.json \
  --unit arena-shard.service --live-approval <APPROVAL>
# f. Ownership/permission check, then start.
python3 $T/tools/shard_rollout.py verify --shard-dir /opt/uoarena/shard --manifest $R/candidate.json \
  --owner $(id -u uoarena):$(id -g uoarena)
stat -c '%U:%G %a %n' /opt/uoarena/shard/{ServUO.exe,Scripts.dll,Ultima.dll} /opt/uoarena/shard/Saves
systemctl start arena-shard.service
# g. Lockdown is a static field (AccountHandler.LockdownLevel), not saved: every
#    start comes up open to players. As soon as the journal shows the listener,
#    log in as <ADMIN> and re-apply Lockdown -> Counselor BEFORE §5. Players
#    can connect during those seconds; if that is unacceptable, gate the game
#    port at the host firewall for the window instead (review decision).
# h. Run §5 with ops-health-a/b (key character admits the account, fighter
#    character does the checks), then lift Lockdown to Player.
```

## 5. Health checks (after rollout and after any rollback)

1. `systemctl is-active arena-shard.service` and the journal shows world load
   with no exception; no new file under `Crashes/`.
2. Hashes and owners: `shard_rollout.py verify --manifest <expected>.json --owner <uoarena uid:gid>` exits 0.
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

On the disposable fixture, steps 3–5 are run by `rollback_rehearsal.py` with
freshly created player accounts and no Lockdown. On the public shard they are
run under Counselor Lockdown with the §3.10 operator accounts, logging in to
their Player-level fighter characters (anima-bridge from the reviewed
anima-client build, or the desktop client by hand if the bridge cannot select
that character slot). They create one training replay that is visible on the
public feed unless reviewers decide otherwise (§8).

## 6. Rollback triggers

Roll back immediately when any of these occurs within the observation window
(proposed: 30 minutes after lifting Lockdown):

- service not active within 120 s, or a crash/exception during world load;
- any health check in §5 fails, or the hash/owner verify fails;
- login failures, disconnect storms or a match that does not finalize its replay;
- a player-visible regression confirmed by the operator.

## 7. Rollback

```sh
# 1. Quiesce and save, as its own step (skip only if the shard cannot run):
#    Lockdown -> Counselor, [SetSaves false, record T_SAVE, [Save, confirm
#    "Save finished" and fresh Saves/Mobiles + Saves/Items mtimes (§4 a-b).
#    After the stop, check no later "World: Saving" (§4 c).
#    After each start below: re-apply Lockdown before §5 (§4 g-h).
# 2. Stop.
systemctl stop arena-shard.service
# 3. Default: restore the assemblies only, keep candidate-era Config/Saves.
#    The snapshot must belong to this shard and hold exactly current.json.
python3 $T/tools/shard_rollout.py rollback --shard-dir /opt/uoarena/shard --backup-dir $R/backup \
  --expect-restore $R/current.json --unit arena-shard.service --live-approval <APPROVAL>
python3 $T/tools/shard_rollout.py verify --shard-dir /opt/uoarena/shard --manifest $R/current.json \
  --owner $(id -u uoarena):$(id -g uoarena)
systemctl start arena-shard.service          # then repeat §5 against current.json
# 4. Fallback only if the current set cannot load the candidate-era saves:
systemctl stop arena-shard.service
python3 $T/tools/shard_rollout.py rollback --shard-dir /opt/uoarena/shard --backup-dir $R/backup \
  --expect-restore $R/current.json --restore-state --unit arena-shard.service --live-approval <APPROVAL>
systemctl start arena-shard.service          # then repeat §5
```

Assemblies-only rollback is safe for this candidate because `baf481b..a15af9a`
touches only `ArenaSupplies.Refill`, `DuelMatch.PrepareFighter` and
`NetState` send handling — no Serialize/Deserialize or save-version change.

`--restore-state` first re-verifies every snapshotted file (hash, type, owner,
mode) and the free space, then copies each of `ServUO.exe.config`, `Config`,
`Saves` to a hidden temporary name in the shard directory, verifies it, and
only then renames the live item aside (`<item>.rollback-aside-<UTC>-<ns>-<pid>-<rand>`)
and the staged copy into place. A failure at any point puts the previous items
back, so a partial `Saves` is never live. It loses everything since the
snapshot (accounts, items, ladder); the candidate-era state stays in the aside
directories for forensics.

`Export/` (replay archives) is deliberately not snapshotted or restored: it is
append-only public output that both sets write in the same format, it is in the
daily backup, and a rollback must not delete replays recorded meanwhile.

## 8. What a human must review before any live use

- That §1's "current public" hashes are still what is on disk (§3.4) and that
  the running process was started from those files after their mtime.
- Candidate A vs B choice; if B, run `local_lifecycle.py --combat` against it first.
- The quiesce/save step: who holds `<ADMIN>`, and that the Lockdown gump,
  `[SetSaves false` and `[Save` behave as described on the live build.
- The ops-health accounts (§3.10): names, their key/fighter characters, and
  that the health client logs in to the fighter character.
- Whether the seconds between `systemctl start` and re-applied Lockdown are
  acceptable or the game port is firewall-gated for the window.
- Test accounts/characters for §5 on the public shard and whether their
  training replay may be visible on the public feed.
- Observation window length and who decides a player-visible regression.
- Provider snapshot step and its restore drill (outside this repository).
- The pinned checkout `<SHA>` and tool hash (§3.2).

## 9. Disposable rehearsal result (2026-11-19)

One loopback shard (ports 12698/18100), one persistent world directory, pinned
release anima-bridge `f8e3eca9…`. All four health phases passed; no process
was left running. Each phase: startup ≈1 s to feed, login of both fixture
accounts, quick preparation, Strength potion 100→120, bottle refill removed
the empty bottle from the backpack, training round `countdown → fighting`
(a ~1 s `roundover` may also be sampled) with STR back to 100 at fighting,
151–152-row complete replay with matching metadata checksum, then console
`shutdown` (save + self-kill, exit −9). Rerun 2026-11-19 after the second
review round with the final tool: `tool_sha256` `8e6ea2c1…` and harness
`2d26980a…` equal the committed files; the swap also verified that live
`ServUO.exe.config`/`Config`/`Saves` still matched the snapshot.
The snapshot used `--saved-after` the h0 save time, rollbacks used
`--expect-restore`, and the final verify checked owner as well as hashes.

| Phase | Installed set (ServUO/Scripts/Ultima) | Persistence check |
| --- | --- | --- |
| h0 current | `3a44e60b` / `b30c8937` / `2adeee94` | new characters 2, 3 |
| snapshot (fresh-save check) → refused swap with wrong manifest → swap | — | set unchanged after refusal |
| h1 candidate A | `cce39e12` / `4257bf06` / `8282a2a2` | same serials; backpack = h0 shutdown |
| rollback `--expect-restore` (assemblies only) | `3a44e60b` / `b30c8937` / `2adeee94` | loaded candidate-era save; backpack = h1 shutdown |
| rollback `--restore-state` | — | restored Saves digest identical to snapshot |
| h3 current, snapshot state | `3a44e60b` / `b30c8937` / `2adeee94` | backpack = h0 shutdown; final hash + owner verify exit 0 |

Not exercised by the rehearsal (unprivileged macOS user, no systemd or
/proc): root ownership changes, the Linux /proc scan and `--unit`; these are
covered by the fault-injection unit tests. The fixture's "current" set is the byte-identical reconstruction from §1, not
a copy of the host files. The rehearsal does not cover systemd, the host's
real saves/accounts volume, client versions other than anima-bridge, or load.
