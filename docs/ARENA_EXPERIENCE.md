# Arena experience and template rankings

The unified `[Arena` board defaults to friendly matches. Ranked uses separate 5x
and 7x + EX pot Elo ladders. Existing legacy queue and public results are preserved.
Quick preparation confirms replacement of skills/stats, refills supplies and returns
to the board. It requires a living, idle, unqueued player in the lobby.

Result gumps offer rematch, replay or lobby. Rematch requires both players' consent,
retains the original rules/arena/ranked choice, and expires after two minutes.

`[Arena list 5|7 [ranked]` lists for invitations. `[ArenaHuman` and
`[ArenaAgent model version policy]` add public self-reported labels, not identity
or model attestations. `/duel/` publishes lobby/waiting presence, template ladder
and process counters; staff and account usernames are excluded from these additions.

## Persistence

`Saves/ArenaTemplateLadder.bin` is written atomically after rated matches AND each
WorldSave, since AutoSave rotates the Saves directory. Include it in paired world
and binary backups. Elo starts at 1000, K=24. First three completed matches per
pair of accounts per UTC day count across both templates and all their characters.
Extra games remain playable but unrated. Training and administrative aborts do not
change Elo. Separate accounts do not prove separate people. New replay metadata
records rating outcomes and participant labels.

Read/write failures disable new ranked starts and rating updates; check
`operations.rankedStorageHealthy` and server logs. Restore a verified snapshot
while stopped; never silently erase a corrupt snapshot to reset rankings.
Friendly matches remain available. Process counters reset on restart and cannot
identify the cause of a disconnect.

## Verification

Local ordinary clients exercise quick setup, profiles/list visibility, friendly
and ranked queue isolation, accepted rematches, scoring, daily opponent limits,
replay metadata and AutoSave/restart persistence. Administrative kills used to
complete result fixtures are confined to the local shard, never production.
