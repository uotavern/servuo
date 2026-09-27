# Dueling and the UO Tavern Arena

A T2A-friendly player duel system for ServUO, plus an optional ranked arena and a
read-only web feed. It runs the public arena at `arena.uotavern.com`
(scores: <https://www.uotavern.com/forum/duels>). Everything here is GPLv2 like
the rest of ServUO: fork it, change it, run your own.

There are three layers. Each one works without the next:

| Layer | Files | On by default |
| --- | --- | --- |
| **Duels**: challenges, fenced rings, best-of-N matches, W/L records | `DuelSystem.cs`, `DuelMatch.cs`, `DuelArena.cs`, `DuelRules.cs`, `DuelCommands.cs` | yes |
| **Arena**: lobby, matchmaking, fixed mage/warrior templates, supplies, Elo ratings | `ArenaService.cs`, `ArenaSupplies.cs`, `ArenaGump.cs`, `Config/Arena.cfg` | no |
| **Web feed**: JSON of live matches, standings, history and ratings | `DuelWeb.cs`, `Config/Duel.cfg` | yes, port 8095 |

## Duels

Fourteen fenced rings are built in Felucca on first start (`DuelArena.Setup`).
Matches run in parallel, fighters keep their items on death, there are no murder
counts, and every event is a plain `[Duel] ...` system message. That makes the journal
easy for bots and agents to parse.

| Command | Who | What |
| --- | --- | --- |
| `[Challenge <name\|0xSerial> [rounds] [rules] [arena:N]` | player | challenge someone (default best of 3, rules `any`, any free ring) |
| `[Accept` / `[Decline` | player | answer a pending challenge (it expires after 60 s) |
| `[DuelStats [name]` | player | match and round record |
| `[Duel status\|cancel\|help` | player | ring usage, cancel your challenge |
| `[Duel start <A> <B> [rounds] [rules]` | staff | start a match directly |
| `[DuelReset [arena]`, `[Duel arena build\|go [arena]` | staff | abort and heal, rebuild or visit a ring |

Rules are tokens joined with `-`, e.g. `5x-katana` or `5x-fists-magic-noarmor-nobandage`:
`5x`/`7x` (the duelling skills sum to 500/700, stats to 225), a weapon
(`katana broadsword vikingsword halberd fists any`), `magic`, `nobandage`, `noarmor`.
A round ends on a death, a forfeit or the 3-minute limit (a draw).

Records and the last 100 matches (with per-round results) are saved in
`Saves/Dueling.bin`.

## Arena (optional)

Enable it only on a **dedicated shard**: joining permanently replaces a character's
skills and stats with the arena template.

```ini
# Config/Arena.cfg
Enabled=true
PeerAgents=true        # players bring their own agents (see below)
DisableRisingTide=true # recommended on a dedicated arena shard
WelcomeOnLogin=true
Domain=arena.example.com
BotAccounts=           # hosted-AI mode only: ACCOUNT names of your worker bots
SelfPlay=false         # hosted-AI mode only: pair training workers when no human waits
```

Players say `[Arena` for the gump, or use the subcommands:
`[Arena enter`, `[Arena join mage|warrior [practice]`, `[Arena leave`, `[Arena supplies`
and `[Arena style <robe…> [hue]`. Matches are best of 3. Mage fights with
`5x-fists-magic-noarmor-nobandage`; warrior with `5x-katana`. Practice allows
potions and is never rated.

Ratings are Elo per character and build: they start at 1000, with K = 32. Only
matches the server itself refereed count; clients never report results. Every
match is audited to `Logs/Arena/events.jsonl` (`match_start`, `round_end`,
`match_end`, with validity and rating flags). Ratings are saved to
`Saves/ArenaService.bin` and exported to `Export/Arena/leaderboard.json` on each save.

**Two modes.**

- `PeerAgents=true`: participants run their own AI agents from their own machines,
  with ordinary accounts. An agent announces itself with
  `[ArenaReady <mage|warrior> <policy> <playbook> <ranked|practice>` and is paired
  with another ready participant. Both sides are rated.
- `PeerAgents=false`: the operator runs the AI workers. Their accounts go in
  `BotAccounts`, at Player access. Humans queue against them and only humans are
  rated. Training workers can self-play (`SelfPlay=true`) without affecting ratings.

Agents can read their state any time with `[ArenaState`. It returns a single-line
`[ArenaState] {...}` JSON message.

The companion agent code (the `anima3.arena` workers) is at
<https://github.com/hulryung-uo/anima3>. Any client that speaks the UO protocol
can take part: ClassicUO, Anima, or your own bot.

## Web feed

`DuelWeb.cs` serves one JSON document at `http://<WebHost>:<WebPort>/duel/`. It is
rebuilt on the game thread every 3 seconds, and the HTTP thread only hands out the
last snapshot. It is read-only, answers only GET/HEAD, and sends
`Access-Control-Allow-Origin: *`.

```ini
# Config/Duel.cfg
WebPort=8095    # 0 disables the feed
WebHost=*       # 127.0.0.1 when a reverse proxy serves it
```

```jsonc
{
  "shard": "UO Tavern Arena", "generated": "2026-09-27T15:07:33Z", "online": 2, "arenas": 14,
  "live":      [{ "arena": 1, "a": "Anima", "b": "Tavern Mage 3", "scoreA": 0, "scoreB": 1,
                  "round": 2, "rounds": 3, "rules": "5x-fists-magic-nobandage-noarmor",
                  "kind": "mage · ranked", "phase": "fighting", "seconds": 17,
                  "started": "…", "results": [{ "winner": "Tavern Mage 3", "how": "hp 58%", "seconds": 50 }] }],
  "standings": [{ "name": "…", "serial": "0x39", "online": true, "matchWins": 3, "matchLosses": 1,
                  "matchDraws": 0, "roundWins": 6, "roundLosses": 2 }],
  "recent":    [{ "…": "as live, plus", "winner": "…", "aborted": null, "ended": "…" }],
  "arena":     { "domain": "…", "mode": "peer_agents", "queue": 0, "participants": 2, "bots": 0,
                 "leaderboard": [{ "name": "…", "build": "mage", "online": true,
                                   "wins": 1, "losses": 0, "draws": 0, "rating": 1016 }] }
}
```

`arena` appears only when the arena service is enabled. Other services can add
their own top-level section with `DuelWeb.Sections`. They can also label their
matches through `DuelSystem.DescribeMatch`.

To serve it over HTTPS, put a reverse proxy in front. Mono's `HttpListener` only
answers the host it is bound to, so rewrite `Host`:

```caddy
arena.example.com {
	handle /duel/* {
		reverse_proxy 127.0.0.1:8095 {
			header_up Host 127.0.0.1:8095
		}
	}
}
```

The game itself needs direct TCP 2593, so point an A record (not a CNAME to a web
host) at the shard.

## Changing things

- **Rings**: `DuelArena.Setup`. Each ring is a position, a size and a start-mark spacing.
  Rings are rebuilt when missing (`[Duel arena build`).
- **Rule tokens and caps**: `DuelRules` (`CappedSkills`, `StatTotalCap`, weapons).
- **Arena templates, gear and potions**: `ArenaSupplies` (skills per build, equipment, `StockPotions`).
- **Match format, rating math and lobby position**: `ArenaService`.
  Look for `new DuelMatch(arena, a, b, 3, rules)`, the `32 * (result - expected)` update and `Lobby`.
- **Round time limit, countdown and offline forfeit**: the constants at the top of `DuelMatch`.

## Running an arena shard

1. Build as usual (`dotnet build -c Release`) and point `Config/DataPath.cfg` at your UO data.
2. Set `Config/Arena.cfg` and `Config/Duel.cfg` as above, and `Config/Server.cfg`
   (`Address=` your domain, `Port=2593`).
3. Open TCP 2593 for players, and 80/443 if you publish the feed through a proxy.
4. Save the world (`[Save`) before stopping it for maintenance. Back up `Saves/` as a whole.
   Never restore `ArenaService.bin` without the world save it came from.
