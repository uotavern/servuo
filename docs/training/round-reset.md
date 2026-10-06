# Training round reset — 2026-10-06

Direct training challenges were not in ArenaService.Sessions, so PrepareFighter
skipped stat-mod cleanup. Live match `ab9c559443a7406da981e8fedd289a0f` began with
one player's maximum HP at 89 despite base STR=100. The learner rejected it.

Extend the existing cleanup to `Rules.Training`. Reset StunReady/DisarmReady to
align each round's public client state, then remove previous stat mods before
FullHeal. Non-training direct duels retain their existing behavior.

Deployment uses tools/PatchTrainingReset.cs against the pinned deployed artifact
rather than replacing other changes in that assembly. Mono.Cecil compares all
103,614 method bodies; only DuelMatch.PrepareFighter may differ. Source patch is
included for future normal builds.

- Before: `b03a253bb85b1ead9eff732dab7e8749e9e3a38476351ccfaa149dfa678225b0`
- After: `e05037e49859138f14fae1b091dbb7f1b753501278ef0385d087fc6b3c6cfb02`
- Backups: `/opt/uoarena/training-reset-release-20261006/`
- Deployment requires zero online players, no live matches, and unchanged hashes.
- Live acceptance: next training replay starts both players with maxima 100/100/25.
