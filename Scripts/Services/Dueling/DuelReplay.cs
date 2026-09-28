using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Server.Engines.Dueling
{
    /// <summary>Server observations, not video or an executable simulation. World reads stay on the game thread;
    /// bounded disk work runs on one background writer. Only finalized archives are published.</summary>
    public static class DuelReplay
    {
        public static readonly bool Enabled = Config.Get("Duel.ReplayEnabled", true);
        public static readonly string DirectoryPath = Path.Combine("Export", "DuelReplays");
        public static readonly int SampleMs = Math.Max(100, Config.Get("Duel.ReplaySampleMs", 200));
        private static readonly int Keep = Math.Max(1, Config.Get("Duel.ReplayKeep", 200));
        private static readonly int Days = Math.Max(1, Config.Get("Duel.ReplayDays", 30));
        private const int MaxLines = 150000;
        private const long MaxBytes = 32 * 1024 * 1024;
        private const long ArchiveBudget = 512 * 1024 * 1024;
        public static int Failures, DroppedRows;
        private static readonly Regex SafeId = new Regex("^[0-9a-f]{32}$");
        private static readonly Dictionary<DuelMatch, Recording> Active = new Dictionary<DuelMatch, Recording>();
        private static readonly BlockingCollection<Action> Work = new BlockingCollection<Action>(8192);
        private static readonly object ArchiveLock = new object();
        private static byte[] Index = Encoding.UTF8.GetBytes("{\"schema\":1,\"replays\":[]}");
        private static readonly HashSet<string> Published = new HashSet<string>();

        private sealed class Recording
        {
            public DuelMatch Match;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public StreamWriter Writer;
            public int Sequence, Dropped;
            public long Bytes;
            public bool Failed;
            public string Partial, LastLoadout, LastWorld;
            public readonly Dictionary<string, long> VisualSeen = new Dictionary<string, long>();
        }

        private static string Q(string s) { return s == null ? "null" : ArenaService.Json(s); }
        private static string B(bool b) { return b ? "true" : "false"; }
        private static string Pos(IPoint3D p) { return "[" + p.X + "," + p.Y + "," + p.Z + "]"; }
        public static void Initialize()
        {
            if (!Enabled) return;
            var thread = new Thread(() =>
            {
                try { Directory.CreateDirectory(DirectoryPath); RefreshIndex(); }
                catch (Exception e) { Interlocked.Increment(ref Failures); Console.WriteLine("[Replay] init: " + e.Message); }
                foreach (var work in Work.GetConsumingEnumerable())
                    try { work(); } catch (Exception e) { Interlocked.Increment(ref Failures); Console.WriteLine("[Replay] writer: " + e.Message); }
            });
            thread.IsBackground = true;
            thread.Name = "Duel replay writer";
            thread.Start();
            Network.Packet.VisualCompiled += VisualPacket;
            Mobile.PublicSpeechBroadcast += Speech;
            Timer.DelayCall(TimeSpan.FromMilliseconds(SampleMs), TimeSpan.FromMilliseconds(SampleMs), SampleAll);
        }

        public static void Start(DuelMatch m)
        {
            if (!Enabled) return;
            var r = new Recording { Match = m, Partial = Path.Combine(DirectoryPath, m.Id + ".partial") };
            Active[m] = r;
            string header = "\"schema\":1,\"visualVersion\":1,\"id\":" + Q(m.Id) + ",\"started\":" + Q(m.Started.ToString("o")) +
                ",\"rules\":" + Q(m.Rules.ToString()) + ",\"ranked\":" + B(m.Ranked) + ",\"training\":" + B(m.Rules.Training) +
                ",\"sampleMs\":" + SampleMs + ",\"showdownAfterSeconds\":" + DuelMatch.ShowdownAfterSeconds +
                ",\"roundLimitSeconds\":" + (int)DuelMatch.RoundTimeLimit.TotalSeconds + ",\"rounds\":" + m.Rounds +
                ",\"arena\":{\"id\":" + m.Arena.Id + ",\"map\":\"Felucca\",\"shape\":" + Q(m.Arena.Shape) + ",\"name\":" + Q(m.Arena.Name) +
                ",\"floor\":[" + m.Arena.Floor.X + "," + m.Arena.Floor.Y + "," + m.Arena.Floor.Width + "," + m.Arena.Floor.Height +
                "],\"z\":" + m.Arena.Z + "},\"players\":[" + Identity(m.A) + "," + Identity(m.B) + "]";
            Write(r, "header", header);
            Frame(r);
        }

        private static string Identity(Mobile p)
        {
            if (p == null) return "null";
            var gear = p.Items.Where(i => i.Layer != Layer.Backpack && i.Layer != Layer.Bank && i.Parent == p)
                .Select(i => "{\"serial\":" + i.Serial.Value + ",\"graphic\":" + i.ItemID + ",\"hue\":" + i.Hue + ",\"layer\":" + (int)i.Layer + "}").ToList();
            if (p.HairItemID != 0) gear.Add("{\"serial\":0,\"graphic\":" + p.HairItemID + ",\"hue\":" + p.HairHue + ",\"layer\":11}");
            if (p.FacialHairItemID != 0) gear.Add("{\"serial\":0,\"graphic\":" + p.FacialHairItemID + ",\"hue\":" + p.FacialHairHue + ",\"layer\":16}");
            return "{\"profile\":"+ArenaExperience.ProfileJson(p)+",\"stats\":[" + p.RawStr + "," + p.RawDex + "," + p.RawInt + "],\"serial\":" + p.Serial.Value + ",\"name\":" + Q(p.Name) + ",\"body\":" + p.Body.BodyID +
                ",\"hue\":" + p.Hue + ",\"equipment\":[" + String.Join(",", gear) + "]}";
        }

        // Only public fighter speech: no commands, party/guild/private messages or whispers.
        private static void Speech(Mobile speaker, Network.MessageType type, int hue, string text)
        {
            if (type != Network.MessageType.Regular && type != Network.MessageType.Emote &&
                type != Network.MessageType.Yell && type != Network.MessageType.Spell) return;
            if (String.IsNullOrWhiteSpace(text)) return;
            var match = DuelSystem.FindMatchOf(speaker);
            if (match == null || (speaker != match.A && speaker != match.B)) return;
            Event(match, "speech", "\"actor\":" + speaker.Serial.Value + ",\"messageType\":" + (int)type +
                ",\"hue\":" + hue + ",\"text\":" + Q(text.Length > 512 ? text.Substring(0, 512) : text));
        }

        private static string Fighter(Mobile p)
        {
            return "{\"serial\":" + p.Serial.Value + ",\"pos\":" + Pos(p) + ",\"direction\":" + (int)p.Direction +
                ",\"hits\":" + p.Hits + ",\"hitsMax\":" + p.HitsMax + ",\"mana\":" + p.Mana + ",\"manaMax\":" + p.ManaMax +
                ",\"stam\":" + p.Stam + ",\"stamMax\":" + p.StamMax + ",\"alive\":" + B(p.Alive) +
                ",\"poisoned\":" + B(p.Poisoned) + ",\"paralyzed\":" + B(p.Paralyzed) + ",\"hidden\":" + B(p.Hidden) +
                ",\"spell\":" + Q(p.Spell == null ? null : p.Spell.GetType().Name) + "}";
        }

        private static void SampleAll()
        {
            foreach (var r in Active.Values.ToArray()) Frame(r);
        }

        public static void Position(Mobile p)
        {
            var m = DuelSystem.FindMatchOf(p);
            if (m != null) Event(m, "position", "\"round\":" + m.Round + ",\"phase\":" + Q(m.Phase.ToString()) + ",\"player\":" + Fighter(p));
        }

        // Wire cues are captured once before compression, scoped to the two fighters
        // or their arena floor. These contain no text, accounts or backpack contents.
        private static int U16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
        private static int U32(byte[] b, int o) { return (b[o] << 24) | (b[o+1] << 16) | (b[o+2] << 8) | b[o+3]; }
        private static void VisualPacket(int id, byte[] b, int length)
        {
            if (Active.Count == 0) return;
            foreach (var r in Active.Values)
            {
                var m = r.Match;
                bool relevant;
                if (id == 0x54) relevant = m.Arena.Floor.Contains(new Point2D(U16(b, 6), U16(b, 8)));
                else if (id == 0x70 || id == 0xC0 || id == 0xC7)
                    relevant = U32(b, 2) == m.A.Serial.Value || U32(b, 2) == m.B.Serial.Value ||
                        U32(b, 6) == m.A.Serial.Value || U32(b, 6) == m.B.Serial.Value ||
                        m.Arena.Floor.Contains(new Point2D(U16(b, 12), U16(b, 14))) ||
                        m.Arena.Floor.Contains(new Point2D(U16(b, 17), U16(b, 19)));
                else
                {
                    int serial = U32(b, id == 0x2F ? 2 : 1);
                    relevant = serial == m.A.Serial.Value || serial == m.B.Serial.Value;
                }
                if (!relevant) continue;
                string data = Convert.ToBase64String(b, 0, length);
                long now = r.Clock.ElapsedMilliseconds, previous;
                // Effects may be compiled separately for clients with different protocol support.
                if (r.VisualSeen.TryGetValue(data, out previous) && now - previous < 10) continue;
                if (r.VisualSeen.Count > 512) r.VisualSeen.Clear();
                r.VisualSeen[data] = now;
                Write(r, "visual", "\"packet\":" + Q(data));
            }
        }

        private static void Frame(Recording r)
        {
            var m = r.Match;
            if (m.A == null || m.B == null || m.A.Deleted || m.B.Deleted) return;
            // Dynamic arena floor/fences and spell fields do not live in the MUL map.
            var world = new List<string>();
            var objects = m.A.Map.GetItemsInBounds(new Rectangle2D(m.Arena.Floor.X - 1, m.Arena.Floor.Y - 1, m.Arena.Floor.Width + 2, m.Arena.Floor.Height + 2));
            foreach (Item i in objects)
                if (!i.Deleted && i.Visible && i.Parent == null)
                    world.Add("{\"serial\":" + i.Serial.Value + ",\"g\":" + i.ItemID + ",\"hue\":" + i.Hue + ",\"pos\":" + Pos(i) + "}");
            objects.Free();
            world.Sort(StringComparer.Ordinal);
            string ground = "\"items\":[" + String.Join(",", world) + "]";
            if (ground != r.LastWorld) { Write(r, "world", ground); r.LastWorld = ground; }
            string loadout = "\"players\":[" + Identity(m.A) + "," + Identity(m.B) + "]";
            if (loadout != r.LastLoadout) { Write(r, "loadout", loadout); r.LastLoadout = loadout; }
            Write(r, "frame", "\"round\":" + m.Round + ",\"phase\":" + Q(m.Phase.ToString()) +
                ",\"showdown\":" + B(m.Showdown) + ",\"score\":[" + m.ScoreA + "," + m.ScoreB +
                "],\"players\":[" + Fighter(m.A) + "," + Fighter(m.B) + "]");
        }

        private static void Write(Recording r, string type, string fields)
        {
            if (r.Failed) return;
            int seq = r.Sequence++;
            if (seq >= MaxLines) { Interlocked.Increment(ref r.Dropped); Interlocked.Increment(ref DroppedRows); return; }
            string line = "{\"seq\":" + seq + ",\"t\":" + r.Clock.ElapsedMilliseconds + ",\"type\":" + Q(type) +
                (String.IsNullOrEmpty(fields) ? "" : "," + fields) + "}";
            if (!Work.TryAdd(() =>
            {
                if (r.Failed) return;
                try
                {
                    int size = Encoding.UTF8.GetByteCount(line) + 1;
                    if (r.Bytes + size > MaxBytes) { Interlocked.Increment(ref r.Dropped); Interlocked.Increment(ref DroppedRows); return; }
                    if (r.Writer == null) r.Writer = new StreamWriter(r.Partial, false, new UTF8Encoding(false));
                    r.Writer.WriteLine(line);
                    r.Bytes += size;
                    if (seq % 25 == 0) r.Writer.Flush();
                }
                catch (Exception e)
                {
                    r.Failed = true; Interlocked.Increment(ref Failures);
                    if (r.Writer != null) { r.Writer.Dispose(); r.Writer = null; }
                    Console.WriteLine("[Replay] " + r.Match.Id + ": " + e.Message);
                }
            }))
            {
                Interlocked.Increment(ref r.Dropped); Interlocked.Increment(ref DroppedRows);
                if (type == "header") r.Failed = true;
            }
        }

        public static void Event(DuelMatch m, string type, string fields)
        {
            Recording r;
            if (m != null && Active.TryGetValue(m, out r)) Write(r, type, fields);
        }
        public static void Action(Mobile actor, Mobile target, string type, string name, int amount = 0)
        {
            var m = DuelSystem.FindMatchOf(actor) ?? DuelSystem.FindMatchOf(target);
            if (m == null) return;
            Event(m, type, "\"actor\":" + (actor == null ? 0 : actor.Serial.Value) + ",\"target\":" + (target == null ? 0 : target.Serial.Value) +
                ",\"name\":" + Q(name) + ",\"amount\":" + amount);
        }
        public static void Projectile(Mobile actor, string type, IPoint3D destination)
        {
            if (actor == null || destination == null) return;
            Event(DuelSystem.FindMatchOf(actor), type, "\"actor\":" + actor.Serial.Value + ",\"from\":" + Pos(actor) + ",\"to\":" + Pos(destination));
        }
        public static void Finish(DuelMatch m, Mobile winner, string aborted)
        {
            Recording r;
            if (!Active.TryGetValue(m, out r)) return;
            Frame(r);
            Active.Remove(m);
            long duration = r.Clock.ElapsedMilliseconds;
            string result = "\"id\":" + Q(m.Id) + ",\"ranked\":" + B(m.Ranked) + ",\"training\":" + B(m.Rules.Training) +
                ",\"winner\":" + (winner == null ? "null" : winner.Serial.Value.ToString()) +
                ",\"score\":[" + m.ScoreA + "," + m.ScoreB + "],\"aborted\":" + Q(aborted) + ",\"ratingResult\":" + Q(m.LadderResult);
            string meta = "\"schema\":1,\"visualVersion\":1," + result + ",\"started\":" + Q(m.Started.ToString("o")) +
                ",\"ended\":" + Q(DateTime.UtcNow.ToString("o")) + ",\"durationMs\":" + duration +
                ",\"rules\":" + Q(m.Rules.ToString()) + ",\"arena\":" + m.Arena.Id +
                ",\"players\":[" + Identity(m.A) + "," + Identity(m.B) + "],\"url\":" + Q("/duel/replays/" + m.Id + ".jsonl");
            int seq = r.Sequence++;
            Action close = () =>
            {
                try
                {
                    if (r.Failed || r.Writer == null) return;
                    string quality = ",\"complete\":" + B(r.Dropped == 0) + ",\"dropped\":" + r.Dropped;
                    r.Writer.WriteLine("{\"seq\":" + seq + ",\"t\":" + duration + ",\"type\":\"end\"," + result + quality + "}");
                    r.Writer.Dispose(); r.Writer = null;
                    string hash;
                    using (var sha = SHA256.Create()) using (var input = File.OpenRead(r.Partial))
                        hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                    string archive = Path.Combine(DirectoryPath, m.Id + ".jsonl.gz");
                    using (var input = File.OpenRead(r.Partial)) using (var output = File.Create(archive + ".tmp"))
                    using (var zip = new GZipStream(output, CompressionMode.Compress)) input.CopyTo(zip);
                    File.Move(archive + ".tmp", archive);
                    string manifest = Path.Combine(DirectoryPath, m.Id + ".meta.json");
                    File.WriteAllText(manifest + ".tmp", "{" + meta + quality + ",\"sha256\":" + Q(hash) + "}", new UTF8Encoding(false));
                    File.Move(manifest + ".tmp", manifest);
                    File.Delete(r.Partial);
                    RefreshIndex();
                }
                catch (Exception e) { Interlocked.Increment(ref Failures); Console.WriteLine("[Replay] finalize " + m.Id + ": " + e.Message); }
                finally { if (r.Writer != null) { r.Writer.Dispose(); r.Writer = null; } }
            };
            // Never wait for disk or a full queue on the simulation thread. Earlier enqueued rows stay ordered.
            if (!Work.TryAdd(close)) ThreadPool.QueueUserWorkItem(_ => Work.Add(close));
        }

        private static void RefreshIndex()
        {
            lock (ArchiveLock)
            {
                Published.Clear();
                var rows = new List<string>();
                long total = 0;
                foreach (var file in new DirectoryInfo(DirectoryPath).GetFiles("*.meta.json").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    string id = file.Name.Substring(0, file.Name.Length - ".meta.json".Length);
                    if (!SafeId.IsMatch(id)) continue;
                    string archive = Path.Combine(DirectoryPath, id + ".jsonl.gz");
                    if (!File.Exists(archive)) continue;
                    long size = new FileInfo(archive).Length;
                    if (rows.Count >= Keep || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-Days) || total + size > ArchiveBudget)
                    { File.Delete(archive); file.Delete(); continue; }
                    if (file.Length > 65536 || size > MaxBytes) continue;
                    total += size;
                    rows.Add(File.ReadAllText(file.FullName)); Published.Add(id);
                }
                foreach (var file in new DirectoryInfo(DirectoryPath).GetFiles("*.partial"))
                    if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-Days)) file.Delete();
                Index = Encoding.UTF8.GetBytes("{\"schema\":1,\"replays\":[" + String.Join(",", rows) + "]}");
            }
        }

        public static bool Read(string path, out byte[] bytes, out bool gzip)
        {
            bytes = null; gzip = false;
            lock (ArchiveLock)
            {
                if (path == "/duel/replays/" || path == "/duel/replays") { bytes = Index; return true; }
                const string prefix = "/duel/replays/";
                if (!path.StartsWith(prefix) || !path.EndsWith(".jsonl")) return false;
                string id = path.Substring(prefix.Length, path.Length - prefix.Length - 6);
                if (!SafeId.IsMatch(id) || !Published.Contains(id)) return false;
                try { bytes = File.ReadAllBytes(Path.Combine(DirectoryPath, id + ".jsonl.gz")); gzip = true; return true; }
                catch (IOException) { return false; }
            }
        }
    }
}
