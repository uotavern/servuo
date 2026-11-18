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
