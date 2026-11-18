# One disposable local shard lifecycle fixture

`tests/provenance/local_lifecycle.py` exercises the pinned clean source binaries from BUILD_PROVENANCE.md through real TCP clients. It creates two ordinary accounts and characters in a new empty world, issues a player-level Challenge and Accept, observes countdown and fighting, then verifies a single 20-second time-limit draw and a finalized semantic replay. This is lifecycle acceptance, not combat-balance or production deployment acceptance.

```sh
python3 tests/provenance/local_lifecycle.py \
  --build /tmp/servuo-source-build-new/build1 \
  --bridge /path/to/anima-bridge \
  --data /path/to/licensed-uo-client-assets \
  --output /tmp/servuo-lifecycle-new
python3 -m unittest discover -s tests/provenance -v
```

Prerequisites: the exact three binary hashes in BUILD_PROVENANCE.md; Mono; Python 3.9+; an anima-client schema-33 bridge; legally available local UO client data. Tested bridge is the transport-observation integration debug binary; its SHA is recorded in the result. The output directory must not exist. The candidate binaries and committed Config/Data are copied there, without Saves or accounts. Client assets are read in place by shard and bridge; the harness does not copy or write them. No symlink to production world data is created.

The harness accepts no hostname: game and feed bind and connect only to 127.0.0.1, default ports 12694 and 18096. Override only to other available unprivileged ports. Dynamic compilation, autosave, autorestart and self-play are disabled in copied settings. It starts exactly one shard and two bridge processes; a 120-second deadline bounds readiness and match operations. Finally it terminates and waits for each owned process, escalating to kill after five seconds. Original binaries, configuration, assets and existing processes are not modified. Logs, replay archive/metadata and `lifecycle.json` stay in the disposable directory for review.

Accounts `fixture_alpha` and `fixture_beta` use the public throwaway password `local-fixture-only`. They are automatically created through ordinary protocol login; Account constructor sets Player access. Service mode skips the owner-account console prompt. These credentials are only for this empty loopback fixture and must never be reused for deployed accounts.

Replay validation binds the ordered character serials and observed match ID to the header, demands one round_end with time_limit and no winner, one final complete end row with draw score, no abort/dropped rows, contiguous sequence numbers and a matching decompressed SHA-256 in metadata. Training matches are intentionally absent from ranked recent history, so completion uses the exact finalized replay, not a non-empty ranked-history list. Both countdown and fighting must have been observed before success.

The committed `tests/provenance/lifecycle-20261118.json` and compressed raw `tests/provenance/fixtures/local-training-draw.jsonl.gz` preserve the acceptance evidence. The result receipt records the measured binary/bridge/harness hashes and final replay metadata. An exploratory UI session ran on separate loopback ports12693/18095 and is not the automated fixture. No production address, account, replay, process or binary was used.

Remaining limits: this fixture produces an idle draw, not spell/melee damage or a winning combat round; it does not prove every replay consumer, live migration, live configuration, legacy compiler origins, or load performance. Existing compiled reset/send fixtures separately cover training reset and queue handoff. A production rollout still requires reviewed operational acceptance and rollback planning.

## Bounded combat mode

Pass `--combat` to execute a single combat fixture instead of the idle draw. It uses the same pinned source binaries, fresh world and ordinary accounts, with a 180-second total deadline and one best-of-one round (100-second showdown, 120-second round limit). No server commands with administrative access are used. The phase-5 investigation is limited to two local fixture executions, including unsuccessful attempts.

The policy uses the ordinary quick-preparation gump to establish 100/25/100 stats and seven mage skills. Before the match it consumes a strength potion, verifies a temporary strength increase and an empty bottle, requests lobby supplies, and checks that the bottle leaves the backpack and the strength potions refill. The bank destination is implemented by the pinned source but is not directly observed over this protocol. On round start, strength must return to100, demonstrating removal of the temporary stat modifier.

Combat explicitly includes the `magic` rule: `training any` alone, used by the idle-draw fixture, correctly forbids spellcasting. The attacker closes to within four tiles using single manual `Walk` steps (no bridge `WalkTo` route; fails closed if not within ten tiles) and casts Energy Bolt; the defender uses a healing potion and Greater Heal before becoming passive. The policy makes at most13 Energy Bolt attempts and one Greater Heal; bounded meditation waits allow mana recovery. A missed target cursor or unexpected result fails closed and retains the error/observations.

Success additionally requires an attacker win1-0 with exactly one `defeat` round (never timeout or disconnect), server-recorded positive damage/healing, Energy Bolt cast evidence and a self-targeted Greater Heal. Client before/after observations bind the potion heal to an actual HP increase. These are controlled scripted actions, not a benchmark of agent intelligence, duel balance or load capacity.

### Phase-5 measured result (two fixtures, no extra game)

The second fixture produced a decisive win1-0 in a60-second fighting round: seven damage events totaling202 HP, eight successful cast/target events (seven Energy Bolts and one Greater Heal), and two heal events totaling78 HP. The healing potion increased HP34→62 (28); Greater Heal supplied50. Strength potion100→120 was cleared to100 at round start. One consumed empty bottle disappeared from the backpack after supplies, and strength potions returned to5. The raw archive has444 rows, complete=true, no abort and no dropped rows. Independent replay validation confirmed its ordered participants and checksum.

**Both executed wrappers reported failure; actual combat success is separately established from the finalized replay.** Attempt1 used the draw fixture's non-magic rules, so spellcasting was correctly denied. Attempt2 completed and archived the winning match, but then attempted another cast after the shard had already resurrected/released the loser. The policy's `dead` check therefore missed completion. All owned processes were stopped in both cases.

The corrected policy now stops issuing actions when the exact observed match archive appears, including while waiting for a target cursor; the common strict receipt validator still must pass. Offline regression tests cover this post-resurrection case and independently validate the recorded actual combat archive. The corrected wrapper was **not rerun live**, respecting the two-fixture investigation budget. Its next live acceptance remains outstanding; do not report the wrapper itself as runtime-passed.

Evidence is preserved under `tests/provenance/combat-evidence/`: both executed wrapper reports (attempt2 omits unrelated world inventory/terrain/UI and records the full report hash), the successful combat's raw gzip and metadata, and `offline-audit.json`. The raw match is `f7340263d0a7441eacd3e142d13ccf43`; decompressed SHA-256 is `7232ebd58555233bb9652d5a4a505f77cec310de48f3342d8e944383d746e69b`. Thirteen offline tests pass. No third fixture, production process, deployment or account was used.

### Live rerun of the corrected wrapper (2026-11-18, three fixtures, budget exhausted)

The PR #3 wrapper was rerun live on loopback ports 12697/18099 with the same pinned binaries, a fresh world each time and the throwaway accounts. Bridge binary SHA-256 `34cb820fc34fdf83184fa07dff642dba619fdcd02c8653bc6b25d4fe23ed91ab` (the earlier `dcdba1f3…` binary had been rebuilt in its shared target directory and is no longer available). **No execution reported wrapper success; the corrected wrapper has still not passed live.**

1. Run 1 (merged PR #3 policy, 100 s, exit 1): actual combat succeeded again: match `ab4d18bcd24947e8bd472ad00a3cfea5`, defeat 1-0 for the attacker, seven damage events totaling 170 HP, two heals totaling 52 HP, nine casts (eight with targets), 486 complete rows; the policy saw the loser `dead` and stopped casting, so the PR #3 post-resurrection fix worked. The wrapper then failed with `TimeoutError: bridge response` on a post-match pump. Replay positions show the attacker never reached its `WalkTo` goal (6087,3721), unlike PR #3 attempt 2, so the bridge route stayed active; after release outside the fence the bridge kept re-pathing toward that interior goal.
2. Run 2 (bounded WalkTo wait plus cancel-by-WalkTo-to-current-tile, 41 s, exit 1): in eight seconds the attacker moved one tile; the bridge still reported the start tile at cancel time while the replay records the step 0.8 s before the first cast, so the cancel most likely created a new one-tile route, and the attacker's bridge (`fixture_alpha`) did not answer a pump within 10 s during the first cast. The match was aborted by harness shutdown, so it was never finalized (unfinalized partial preserved).
3. Run 3 (manual-step approach, 2 s, exit 1): bridge login got `Connection refused`: the Duel web feed starts about one second before the game listener, and the harness only waited for the feed. No match was started.

Fixes now in code, with offline regression tests but **not yet live-verified**: `combat_policy.approach` never uses `WalkTo` (single `Walk` steps, which also cancel any route, and a fail-closed spell-range check), and `wait_listening` waits for the game TCP listener before starting bridges. Bridge timeouts now name the client. The pump-stall mechanism is inferred from replay positions and bridge source, not from a bridge profile. All owned processes were stopped after each run. Evidence is under `tests/provenance/combat-evidence/rerun-20261118/`. A further live fixture execution is required for wrapper acceptance.
