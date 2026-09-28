using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Server.Accounting;
using Server.Commands;
using Server.Mobiles;
using Server.Regions;
using Server.Spells;

namespace Server.Engines.Dueling
{
    // The shard owns pairing and results. In peer mode agents run on participant machines;
    // readiness/policy labels are declarations, never authority to submit an outcome.
    public static class ArenaService
    {
        public static readonly bool Enabled = Config.Get("Arena.Enabled", false);
        public static readonly bool WelcomeOnLogin = Config.Get("Arena.WelcomeOnLogin", false);
        public static readonly bool PeerAgents = Config.Get("Arena.PeerAgents", true);
        public static readonly bool SelfPlay = Config.Get("Arena.SelfPlay", false);
        public static readonly string Domain = Config.Get("Arena.Domain", "arena.uotavern.com");
        public static readonly Rectangle2D LobbyBounds = new Rectangle2D(5198, 309, 50, 50);
        public static readonly Point3D Lobby = new Point3D(5223, 334, 15);
        private static readonly string SavePath = Path.Combine("Saves", "ArenaService.bin");
        private static readonly string LogPath = Path.Combine("Logs", "Arena", "events.jsonl");
        private static readonly HashSet<string> BotAccounts = new HashSet<string>(
            Config.Get("Arena.BotAccounts", "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        private static readonly List<Entry> Queue = new List<Entry>();
        private static readonly Dictionary<PlayerMobile, Bot> Bots = new Dictionary<PlayerMobile, Bot>();
        private static readonly Dictionary<DuelMatch, Session> Sessions = new Dictionary<DuelMatch, Session>();
        private static readonly Dictionary<string, ArenaRecord> Records = new Dictionary<string, ArenaRecord>();
        private static readonly Dictionary<PlayerMobile, string> LastResult = new Dictionary<PlayerMobile, string>();
        private static readonly Dictionary<PlayerMobile, string> LastResultData = new Dictionary<PlayerMobile, string>();
        private static readonly Dictionary<PlayerMobile, DateTime> LastJoin = new Dictionary<PlayerMobile, DateTime>();
        private static bool StorageHealthy = true;
        private static int Side;

        private class Entry { public PlayerMobile Player; public string Build; public bool Practice; public DateTime Joined; }
        private class Bot { public string Build, Policy, Playbook; public bool Training; public DateTime Ready; }
        private class Session
        {
            public string Id = Guid.NewGuid().ToString("N"), Build;
            public bool Training, Practice, Invalid, BotFailure;
            public string PolicyA, PolicyB, PlaybookA, PlaybookB;
            public DateTime Started = DateTime.UtcNow;
        }
        public class ArenaRecord
        {
            public Mobile Player;
            public string Build;
            public bool Peer;
            public int Wins, Losses, Draws, Rating = 1000;
        }

        public static void Configure()
        {
            if (!Enabled) return;
            EventSink.WorldSave += e => Save();
            EventSink.WorldLoad += Load;
            EventSink.Login += e => Timer.DelayCall(TimeSpan.FromSeconds(2), () =>
            {
                var pm = e.Mobile as PlayerMobile;
                if (pm == null || pm.NetState == null) return;
                if (DuelSystem.FindMatchOf(pm) == null && DuelArena.Find(pm) != null)
                {
                    pm.Frozen = false;
                    if (!pm.Alive) pm.Resurrect();
                    DuelMatch.FullHeal(pm);
                    pm.Combatant = null; pm.Warmode = false;
                    pm.MoveToWorld(Lobby, DuelArena.ArenaMap);
                    Audit("restart_recovery", "\"player\":" + pm.Serial.Value);
                }
                if (IsBot(pm)) return;
                pm.SendMessage(0x35, "[Arena] Welcome! Bring your own agent. Say [Arena for arenas, supplies and rankings.");
                if (WelcomeOnLogin && DuelSystem.FindMatchOf(pm) == null)
                {
                    if (InLobby(pm)) Open(pm); else Enter(pm);
                }
            });
        }

        public static void Initialize()
        {
            if (!Enabled) return;
            CommandSystem.Register("Arena", AccessLevel.Player, Command);
            CommandSystem.Register("ArenaReady", AccessLevel.Player, ReadyCommand);
            CommandSystem.Register("ArenaState", AccessLevel.Player, e => SendState(e.Mobile as PlayerMobile));
            new ArenaLobbyRegion().Register();
            Timer.DelayCall(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), Tick);
            DuelSystem.DescribeMatch = Describe;
            DuelWeb.Sections.Add(WebSection);
            Audit("service_start", "\"domain\":" + Json(Domain));
        }

        /// <summary>"mage · ranked", "warrior · training", ...; null for a plain player challenge.</summary>
        private static string Describe(DuelMatch m)
        {
            Session s;
            if (m == null || !Sessions.TryGetValue(m, out s)) return null;
            return s.Build + " · " + (s.Training ? "training" : s.Practice ? "practice" : "ranked");
        }

        /// <summary>The arena's part of the web feed: every rated player per build, and who is waiting.</summary>
        private static string WebSection()
        {
            string rows = String.Join(",", Records.Values
                .Where(r => r.Peer == PeerAgents && r.Player != null && !r.Player.Deleted && r.Wins + r.Losses + r.Draws > 0)
                .OrderBy(r => r.Build).ThenByDescending(r => r.Rating).ThenByDescending(r => r.Wins).ThenBy(r => r.Player.Serial.Value)
                .Select(r => "{\"name\":" + Json(r.Player.Name) + ",\"build\":" + Json(r.Build) + ",\"online\":" + (r.Player.NetState != null ? "true" : "false") +
                    ",\"wins\":" + r.Wins + ",\"losses\":" + r.Losses + ",\"draws\":" + r.Draws + ",\"rating\":" + r.Rating + "}"));
            return "\"arena\":{\"domain\":" + Json(Domain) + ",\"queue\":" + (Queue.Count + ArenaMatchmaking.Count) + ",\"mode\":" + Json(PeerAgents ? "peer_agents" : "hosted_ai") + ",\"participants\":" + Bots.Count + ",\"bots\":" + (PeerAgents ? 0 : Bots.Count) + ",\"leaderboard\":[" + rows + "]}";
        }

        public static bool IsServiceMatch(DuelMatch m) { return m != null && Sessions.ContainsKey(m); }

        public static bool AllowsPotions(DuelMatch m)
        {
            Session s;
            return m != null && Sessions.TryGetValue(m, out s) && !m.Rules.NoPotions;
        }

        public static bool IsBot(Mobile m)
        {
            if (PeerAgents) return false;
            var account = m == null ? null : m.Account as Account;
            return account != null && BotAccounts.Contains(account.Username);
        }
        public static bool InLobby(Mobile m) { return m != null && m.Map == DuelArena.ArenaMap && LobbyBounds.Contains(m.Location); }
        private static bool Idle(PlayerMobile p) { return p != null && !p.Deleted && p.NetState != null && DuelSystem.FindMatchOf(p) == null; }
        private static string Build(string value) { return value == "mage" || value == "warrior" ? value : null; }
        private static bool Token(string value) { return value.Length > 0 && value.Length <= 48 && value.All(c => Char.IsLetterOrDigit(c) || c == '-' || c == '_'); }
        public static string Json(string value)
        {
            var b = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
        private static bool Audit(string kind, string fields)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, "{\"schema\":1,\"event\":" + Json(kind) + ",\"utc\":" + Json(DateTime.UtcNow.ToString("o")) + "," + fields + "}\n", Encoding.UTF8);
                return true;
            }
            catch (Exception e)
            {
                StorageHealthy = false;
                Console.WriteLine("[Arena] Storage failure; matchmaking stopped: " + e.Message);
                return false;
            }
        }
        public static void Open(PlayerMobile p)
        {
            if (p == null) return;
            ArenaDuelSetupGump.Open(p);
        }
        public static void OpenPreparation(PlayerMobile p)
        {
            if(p == null || DuelSystem.FindMatchOf(p) != null)return;
            ArenaMatchmaking.ClosePanels(p);
            p.SendGump(new ArenaGump(p));
        }
        public static void Enter(PlayerMobile p)
        {
            if (!Idle(p) || p.AccessLevel != AccessLevel.Player) return;
            if (p.Combatant != null || p.Aggressed.Count > 0 || p.Aggressors.Count > 0 || p.Criminal)
            { p.SendMessage(0x35, "[Arena] Leave combat before travelling to the arena."); return; }
            if (!p.Alive) p.Resurrect();
            p.MoveToWorld(Lobby, DuelArena.ArenaMap);
            DuelMatch.FullHeal(p);
            Open(p);
        }
        private static void Command(CommandEventArgs e)
        {
            var p = e.Mobile as PlayerMobile;
            if (p == null) return;
            string verb = e.Length == 0 ? "menu" : e.GetString(0).ToLowerInvariant();
            switch (verb)
            {
                case "enter": Enter(p); break;
                case "join": Join(p, e.Length > 1 ? e.GetString(1).ToLowerInvariant() : "mage", e.Length > 2 && e.GetString(2).ToLowerInvariant() == "practice"); break;
                case "leave": Leave(p); break;
                case "skills": ArenaTraining.GiveBall(p, e.Length > 1 ? e.GetInt32(1) : 7); break;
                case "duel": Open(p); break;
                case "stats": ArenaTraining.GiveStatBall(p); break;
                case "supplies": ArenaSupplies.Refill(p); break;
                case "style": ArenaSupplies.Style(p, e.Length > 1 ? e.GetString(1) : "robe", e.Length > 2 ? e.GetInt32(2) : 0); break;
                default: Open(p); break;
            }
        }
        public static bool IsLegacyQueued(PlayerMobile p) { return Queue.Any(q => q.Player == p) || Bots.ContainsKey(p); }
        public static bool IsQueued(PlayerMobile p) { return IsLegacyQueued(p) || ArenaMatchmaking.Contains(p); }
        public static void Leave(PlayerMobile p)
        {
            ArenaMatchmaking.Remove(p);
            Queue.RemoveAll(q => q.Player == p);
            if (PeerAgents) Bots.Remove(p);
            p.SendMessage(0x35, "[Arena] You left the queue. An active match continues; leaving its ring forfeits a round.");
        }
        public static void Join(PlayerMobile p, string build, bool practice = false)
        {
            if (!Idle(p) || p.AccessLevel != AccessLevel.Player || IsBot(p)) return;
            if (DuelSystem.HasPending(p)) { p.SendMessage("[Arena] Resolve your duel invitation first."); return; }
            if (ArenaMatchmaking.Contains(p)) { p.SendMessage("[Arena] Leave the duel waiting list first."); return; }
            if (!AccountAvailable(p)) { p.SendMessage(0x35, "[Arena] This account already has a queued or active participant."); return; }
            if (!InLobby(p)) { p.SendMessage(0x35, "[Arena] Say [Arena enter to visit the lobby first."); return; }
            if (Build(build) == null) { p.SendMessage(0x35, "[Arena] Choose mage or warrior."); return; }
            if (practice)
            {
                DuelRules practiceRules; string reason;
                DuelRules.TryParse("7x", out practiceRules, out reason);
                if (!practiceRules.CheckSkills(p, out reason)) { p.SendMessage(0x35, "[Arena] " + reason + " Use [Arena skills and [Arena stats first."); return; }
            }
            DateTime last;
            if (LastJoin.TryGetValue(p, out last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(5)) return;
            LastJoin[p] = DateTime.UtcNow;
            Queue.RemoveAll(q => q.Player == p);
            Queue.Add(new Entry { Player = p, Build = build, Practice = practice, Joined = DateTime.UtcNow });
            p.SendMessage(0x35, "[Arena] Queued for " + build + (practice ? " practice (potions allowed; no rating)" : " ranked") + (practice ? ". Your current skills and stats are preserved (7x cap)." : ". Your skills and stats will use this arena template.") + " Say [Arena leave to cancel.");
            Tick();
        }
        internal static bool AccountAvailable(PlayerMobile p)
        {
            return !PeerAgents || (!Queue.Any(q => q.Player != p && q.Player.Account == p.Account)
                && !DuelSystem.Matches.Any(m => m.Phase != DuelPhase.Finished
                    && (m.A.Account == p.Account || m.B.Account == p.Account)));
        }
        private static void ReadyCommand(CommandEventArgs e)
        {
            var p = e.Mobile as PlayerMobile;
            if (p == null || (!PeerAgents && !IsBot(p)) || p.AccessLevel != AccessLevel.Player) return;
            if (PeerAgents)
            {
                if (e.Length != 4 || Build(e.GetString(0)) == null || !Token(e.GetString(1)) || !Token(e.GetString(2))
                    || (e.GetString(3) != "ranked" && e.GetString(3) != "practice")) return;
                if (Idle(p) && AccountAvailable(p))
                {
                    if (!InLobby(p)) Enter(p);
                    if (!InLobby(p)) return;
                    bool practice = e.GetString(3) == "practice";
                    Bots[p] = new Bot { Build = e.GetString(0), Policy = e.GetString(1), Playbook = e.GetString(2),
                        Training = practice, Ready = DateTime.UtcNow };
                    var queued = Queue.FirstOrDefault(q => q.Player == p);
                    if (queued == null || queued.Build != e.GetString(0) || queued.Practice != practice)
                        Join(p, e.GetString(0), practice);
                    else queued.Joined = DateTime.UtcNow;
                }
                SendState(p);
                return;
            }
            if (e.Length != 4 || Build(e.GetString(0)) == null || !Token(e.GetString(1)) || !Token(e.GetString(2)) || (e.GetString(3) != "training" && e.GetString(3) != "public")) return;
            if (Idle(p))
            {
                p.Frozen = false;
                p.Name = "Tavern " + (e.GetString(0) == "mage" ? "Mage " : "Warrior ") + p.Serial.Value;
                Bots[p] = new Bot { Build = e.GetString(0), Policy = e.GetString(1), Playbook = e.GetString(2), Training = e.GetString(3) == "training", Ready = DateTime.UtcNow };
                if (!InLobby(p)) p.MoveToWorld(Lobby, DuelArena.ArenaMap);
            }
            SendState(p);
        }
        private static bool Ready(PlayerMobile p, Bot bot)
        {
            return Idle(p) && DateTime.UtcNow - bot.Ready < TimeSpan.FromSeconds(12);
        }
        private static void Tick()
        {
            Queue.RemoveAll(q => !Idle(q.Player) || !InLobby(q.Player) || DateTime.UtcNow - q.Joined > TimeSpan.FromMinutes(10));
            foreach (var p in Bots.Keys.Where(p => p.Deleted || p.NetState == null).ToList()) Bots.Remove(p);
            foreach (var p in LastJoin.Keys.Where(p => p.Deleted || p.NetState == null).ToList()) LastJoin.Remove(p);
            foreach (var p in LastResult.Keys.Where(p => p.Deleted).ToList()) { LastResult.Remove(p); LastResultData.Remove(p); }
            foreach (var kv in Sessions)
            {
                if (kv.Key.A.NetState == null || kv.Key.B.NetState == null) kv.Value.Invalid = true;
                if ((IsBot(kv.Key.A) && kv.Key.A.NetState == null) || (IsBot(kv.Key.B) && kv.Key.B.NetState == null)) kv.Value.BotFailure = true;
            }
            if (!StorageHealthy) return;
            if (PeerAgents)
            {
                Queue.RemoveAll(q => Bots.ContainsKey(q.Player) && !Ready(q.Player, Bots[q.Player]));
                foreach (var entry in Queue.ToList())
                {
                    if (!Queue.Contains(entry) || DuelArena.FindFree() == null) continue;
                    var other = Queue.FirstOrDefault(q => q != entry && q.Build == entry.Build && q.Practice == entry.Practice
                        && q.Player.Account != entry.Player.Account);
                    if (other != null && Start(entry.Player, other.Player, entry.Build, false, entry.Practice))
                    { Queue.Remove(entry); Queue.Remove(other); }
                }
                return;
            }
            foreach (var entry in Queue.ToList())
            {
                var bot = Bots.FirstOrDefault(kv => kv.Value.Build == entry.Build && !kv.Value.Training && Ready(kv.Key, kv.Value));
                if (bot.Key == null || DuelArena.FindFree() == null) continue;
                if (Start(entry.Player, bot.Key, entry.Build, false, entry.Practice)) Queue.Remove(entry);
            }
            if (!SelfPlay || Queue.Count > 0) return;
            foreach (string build in new[] { "mage", "warrior" })
            {
                var available = Bots.Where(kv => kv.Value.Build == build && kv.Value.Training && Ready(kv.Key, kv.Value)).OrderBy(kv => kv.Value.Ready).ToList();
                for (int i = 0; i < available.Count; i++)
                {
                    var first = available[i];
                    var second = available.Skip(i + 1).FirstOrDefault(kv => kv.Value.Policy != first.Value.Policy
                        && !(kv.Value.Policy.StartsWith("explore-") && first.Value.Policy.StartsWith("explore-")));
                    if (second.Key == null) continue;
                    Start(first.Key, second.Key, build, true);
                    break;
                }
            }
        }
        private static bool Start(PlayerMobile a, PlayerMobile b, string build, bool training, bool practice = false)
        {
            var arena = DuelArena.FindFree();
            if (arena == null || !Idle(a) || !Idle(b)) return false;
            if (practice)
            {
                DuelRules check; string reason;
                DuelRules.TryParse("7x", out check, out reason);
                foreach (var p in new[] { a, b })
                {
                    if (!check.CheckSkills(p, out reason))
                    { Leave(p); p.SendMessage(0x35, "[Arena] " + reason); return false; }
                }
            }
            // Alternate sides so learning/evaluation does not confound policy and spawn position.
            if ((Side++ & 1) == 1) { var swap = a; a = b; b = swap; }
            ArenaSupplies.Prepare(a, build, practice);
            ArenaSupplies.Prepare(b, build, practice);
            ArenaSupplies.StockPotions(a); ArenaSupplies.StockPotions(b);
            DuelRules rules; string error;
            DuelRules.TryParse((practice ? "7x" : "5x") + (build == "mage" ? "-fists-magic-noarmor-nobandage" : "-katana"), out rules, out error);
            var match = new DuelMatch(arena, a, b, 3, rules);
            Bot ba, bb;
            Bots.TryGetValue(a, out ba); Bots.TryGetValue(b, out bb);
            var session = new Session { Build = build, Training = training, Practice = practice, PolicyA = ba == null ? "human" : ba.Policy, PolicyB = bb == null ? "human" : bb.Policy,
                PlaybookA = ba == null ? "human" : ba.Playbook, PlaybookB = bb == null ? "human" : bb.Playbook };
            if (!Audit("match_start", Fields(match, session))) return false;
            Sessions[match] = session;
            arena.Match = match;
            DuelSystem.Matches.Add(match);
            match.Start();
            SendState(a); SendState(b);
            return true;
        }
        private static string Fields(DuelMatch m, Session s)
        {
            return "\"mode\":" + Json(PeerAgents ? "peer_agents" : "hosted_ai") + ",\"arena\":" + m.Arena.Id + ",\"id\":" + Json(s.Id) + ",\"build\":" + Json(s.Build) + ",\"training\":" + (s.Training ? "true" : "false") + ",\"practice\":" + (s.Practice ? "true" : "false") +
                ",\"a\":" + m.A.Serial.Value + ",\"b\":" + m.B.Serial.Value + ",\"name_a\":" + Json(m.A.Name) + ",\"name_b\":" + Json(m.B.Name) +
                ",\"policy_a\":" + Json(s.PolicyA) + ",\"policy_b\":" + Json(s.PolicyB) + ",\"playbook_a\":" + Json(s.PlaybookA) + ",\"playbook_b\":" + Json(s.PlaybookB);
        }
        public static void NoteRound(DuelMatch m, string reason, int seconds, int attacksA, int attacksB)
        {
            Session s;
            if (!Sessions.TryGetValue(m, out s)) return;
            if ((reason != null && reason.StartsWith("forfeit")) || (seconds > 30 && (attacksA == 0 || attacksB == 0))) s.Invalid = true;
            Audit("round_end", Fields(m, s) + ",\"round\":" + m.Round + ",\"score_a\":" + m.ScoreA + ",\"score_b\":" + m.ScoreB + ",\"reason\":" + Json(reason ?? "defeat") + ",\"seconds\":" + seconds + ",\"attacks_a\":" + attacksA + ",\"attacks_b\":" + attacksB);
        }
        public static void Finished(DuelMatch m, Mobile winner, bool aborted)
        {
            Session s;
            if (!Sessions.TryGetValue(m, out s)) return;
            Sessions.Remove(m); // idempotent; never score an abort
            string fields = Fields(m, s) + ",\"winner\":" + (winner == null ? "null" : winner.Serial.Value.ToString()) +
                ",\"score_a\":" + m.ScoreA + ",\"score_b\":" + m.ScoreB + ",\"valid\":" + (!aborted && !s.Invalid ? "true" : "false") +
                ",\"rated\":" + (!aborted && !s.Training && !s.Practice && !s.BotFailure ? "true" : "false") + ",\"aborted\":" + (aborted ? "true" : "false") + ",\"seconds\":" + ((int)(DateTime.UtcNow - s.Started).TotalSeconds);
            bool logged = Audit("match_end", fields);
            int ratingA = Record(m.A, s.Build).Rating, ratingB = Record(m.B, s.Build).Rating;
            foreach (var p in new[] { m.A, m.B })
            {
                if (p == null || p.Deleted) continue;
                LastResult[p] = s.Id;
                LastResultData[p] = "{" + fields + "}";
                Bot bot;
                if (Bots.TryGetValue(p, out bot)) bot.Ready = DateTime.MinValue; // require fresh readiness after every match
                if (!aborted && logged && !s.Training && !s.Practice && !s.BotFailure && !IsBot(p))
                {
                    var record = Record(p, s.Build);
                    double result = winner == null ? 0.5 : winner == p ? 1.0 : 0.0;
                    if (result == 1) record.Wins++; else if (result == 0) record.Losses++; else record.Draws++;
                    double expected = 1.0 / (1.0 + Math.Pow(10, ((PeerAgents ? (p == m.A ? ratingB : ratingA) : 1000) - record.Rating) / 400.0));
                    record.Rating = Math.Max(0, record.Rating + (int)Math.Round(32 * (result - expected), MidpointRounding.AwayFromZero));
                    p.SendMessage(0x35, String.Format("[Arena] {0}: {1}W {2}L {3}D | {4} rating ({5})", p.Name, record.Wins, record.Losses, record.Draws, record.Rating, s.Build));
                }
                p.RemoveAggressed(m.Opponent(p)); p.RemoveAggressor(m.Opponent(p));
                p.MoveToWorld(Lobby, DuelArena.ArenaMap);
                SendState(p);
            }
            Save();
        }
        public static ArenaRecord Record(Mobile p, string build)
        {
            string key = p.Serial.Value + ":" + build + ":" + PeerAgents;
            ArenaRecord r;
            if (!Records.TryGetValue(key, out r)) Records[key] = r = new ArenaRecord { Player = p, Build = build, Peer = PeerAgents };
            return r;
        }
        public static IEnumerable<ArenaRecord> Leaderboard(string build)
        {
            return Records.Values.Where(r => r.Peer == PeerAgents && r.Build == build && r.Player != null && !r.Player.Deleted && r.Wins + r.Losses + r.Draws > 0)
                .OrderByDescending(r => r.Rating).ThenByDescending(r => r.Wins).ThenBy(r => r.Player.Serial.Value).Take(10);
        }
        public static string QueueStatus(PlayerMobile p)
        {
            int at = Queue.FindIndex(q => q.Player == p);
            int available = Bots.Count(kv => Ready(kv.Key, kv.Value) && !kv.Value.Training);
            return at < 0 ? (PeerAgents ? "Participant agents | " + DuelArena.All.Count + " arenas | " : available + " AI ready | ") + Queue.Count + " waiting" : "Queue position " + (at + 1) + " | " + Queue[at].Build;
        }
        private static void SendState(PlayerMobile p)
        {
            if (p == null || p.AccessLevel != AccessLevel.Player || (!PeerAgents && !IsBot(p))) return;
            var m = DuelSystem.FindMatchOf(p);
            Session s;
            string last;
            LastResult.TryGetValue(p, out last);
            if (m != null && Sessions.TryGetValue(m, out s))
                p.SendMessage(0x35, "[ArenaState] {\"replayId\":" + Json(m.Id) + ",\"id\":" + Json(s.Id) + ",\"phase\":" + Json(m.Phase.ToString()) + ",\"opponent\":" + m.Opponent(p).Serial.Value + ",\"round\":" + m.Round + ",\"build\":" + Json(s.Build) + ",\"playbook\":" + Json(p == m.A ? s.PlaybookA : s.PlaybookB) + ",\"policy\":" + Json(p == m.A ? s.PolicyA : s.PolicyB) + ",\"showdown\":" + (m.Showdown ? "true" : "false") + ",\"showdownRemaining\":" + m.ShowdownRemaining + "}");
            else
            {
                string result; LastResultData.TryGetValue(p, out result);
                p.SendMessage(0x35, "[ArenaState] {\"phase\":\"Idle\",\"last\":" + Json(last) + ",\"result\":" + (result ?? "null") + "}");
            }
        }
        private static void Save()
        {
            try
            {
                Persistence.Serialize(SavePath + ".tmp", w =>
                {
                    w.Write(1);
                    var all = Records.Values.Where(r => r.Player != null && !r.Player.Deleted).ToList();
                    w.Write(all.Count);
                    foreach (var r in all) { w.Write(r.Player); w.Write(r.Build); w.Write(r.Wins); w.Write(r.Losses); w.Write(r.Draws); w.Write(r.Rating); w.Write(r.Peer); }
                });
                if (File.Exists(SavePath)) File.Replace(SavePath + ".tmp", SavePath, null); else File.Move(SavePath + ".tmp", SavePath);
                Directory.CreateDirectory(Path.Combine("Export", "Arena"));
                string rows = String.Join(",", Records.Values.Where(r => r.Peer == PeerAgents && r.Player != null && !r.Player.Deleted && r.Wins + r.Losses + r.Draws > 0).Select(r =>
                    "{\"serial\":" + r.Player.Serial.Value + ",\"name\":" + Json(r.Player.Name) + ",\"build\":" + Json(r.Build) + ",\"wins\":" + r.Wins + ",\"losses\":" + r.Losses + ",\"draws\":" + r.Draws + ",\"rating\":" + r.Rating + "}"));
                string path = Path.Combine("Export", "Arena", "leaderboard.json");
                File.WriteAllText(path + ".tmp", "{\"domain\":" + Json(Domain) + ",\"updated\":" + Json(DateTime.UtcNow.ToString("o")) + ",\"players\":[" + rows + "]}", Encoding.UTF8);
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            catch (Exception e) { StorageHealthy = false; Console.WriteLine("[Arena] Save failed: " + e.Message); }
        }
        private static void Load()
        {
            Persistence.Deserialize(SavePath, r =>
            {
                int version = r.ReadInt(); int count = r.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    var record = new ArenaRecord { Player = r.ReadMobile(), Build = r.ReadString(), Wins = r.ReadInt(), Losses = r.ReadInt(), Draws = r.ReadInt(), Rating = r.ReadInt() };
                    record.Peer = version >= 1 && r.ReadBool();
                    if (record.Player != null) Records[record.Player.Serial.Value + ":" + record.Build + ":" + record.Peer] = record;
                }
            });
        }
    }
    public class ArenaLobbyRegion : BaseRegion
    {
        public ArenaLobbyRegion() : base("UO Tavern Arena Lobby", DuelArena.ArenaMap, 61, ArenaService.LobbyBounds) { }
        public override bool AllowHarmful(Mobile from, IDamageable target) { return false; }
        public override bool OnBeginSpellCast(Mobile m, ISpell s) { return false; }
        public override bool AllowHousing(Mobile m, Point3D p) { return false; }
    }
}
