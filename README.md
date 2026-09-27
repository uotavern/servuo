# [ServUO]

[![Build Status](https://travis-ci.com/ServUO/ServUO.svg?branch=master)](https://travis-ci.com/ServUO/ServUO)
[![GitHub issues](https://img.shields.io/github/issues/servuo/servuo.svg)](https://github.com/ServUO/ServUO/issues)
[![GitHub release](https://img.shields.io/github/release/servuo/servuo.svg)](https://github.com/ServUO/ServUO/releases)
[![GitHub repo size](https://img.shields.io/github/repo-size/servuo/servuo.svg)](https://github.com/ServUO/ServUO/)
[![Discord](https://img.shields.io/discord/110970849628000256.svg)](https://discord.gg/0cQjvnFUN26nRt7y)
[![GitHub contributors](https://img.shields.io/github/contributors/servuo/servuo.svg)](https://github.com/ServUO/ServUO/graphs/contributors)
[![GitHub](https://img.shields.io/github/license/servuo/servuo.svg?color=a)](https://github.com/ServUO/ServUO/blob/master/LICENSE)


ServUO is a community driven Ultima Online Server Emulator written in C#.


### Website

[ServUO]


#### Windows

Run `_windebug.bat` for development, attaching a debugger and/or extended output.

Run `_winrelease.bat` for production environment.


#### Other Platforms

Run `make debug` for development, attaching a debugger and/or extended output.

Run `make` or `make release` for production environment. Writing release is optinal by default


### Linux Dependencies

#### Ubuntu / Debian
```
sudo add-apt-repository ppa:dotnet/backports
sudo apt-get update
sudo apt-get -y install zlib1g mono-complete dotnet-sdk-10.0 dotnet-runtime-10.0
```

#### Arch-based
```
sudo pacman -S make mono dotnet-sdk dotnet-runtime
```

### Summary

1. Starting with the `/Config` directory, make sure to read the readme first, then find and edit `Server.cfg` to set up the essentials.
2. Go through the remaining `*.cfg` files to ensure they suit your needs.
3. For Windows, run `_winrelease.bat` to produce `ServUO.exe`, OSX/Linux users may run `make`.
4. Run `ServUO`
5. ???
6. Profit!


    [ServUO]: <https://www.servuo.dev>


### UO Tavern Arena

The optional `Config/Arena.cfg` service adds a safe lobby, `[Arena` player gump,
AI-worker matchmaking, mage/warrior templates, supplies, cosmetics and persistent
per-character/build ratings. It is **disabled by default** and intended for a
dedicated arena shard: joining explicitly applies a permanent 5x template.
The gump and combat use standard UO packets for ClassicUO and Anima clients.

Set the operator-owned bot **account** allowlist, keep bots at Player access,
and run the `anima3.arena` workers from the companion anima3 repository. Training
workers self-play separately; human ratings are never accepted from clients.
Results go to `Logs/Arena/events.jsonl`, rankings to `Saves/ArenaService.bin` and
`Export/Arena/leaderboard.json`. Save the world before maintenance.

Commands, rules, both arena modes, the JSON web feed (`Config/Duel.cfg`) and
where to customise it all: [`Scripts/Services/Dueling/README.md`](Scripts/Services/Dueling/README.md).
Live scores: <https://www.uotavern.com/forum/duels>.
