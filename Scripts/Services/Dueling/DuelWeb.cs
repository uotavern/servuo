using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using Server.Misc;
using Server.Network;

namespace Server.Engines.Dueling
{
    /// <summary>
    /// Read-only JSON feed of the duel service for the web (the Tavern's /duels page): live matches, standings and
    /// recent history at http://*:{Duel.WebPort}/duel/. The snapshot is rebuilt on the game thread every few seconds;
    /// the listener thread only ever hands out the last finished buffer, so it never touches world state.
    /// </summary>
    public static class DuelWeb
    {
        public static readonly int Port = Config.Get("Duel.WebPort", 0); // 0 disables the feed
        public static readonly string Host = Config.Get("Duel.WebHost", "*"); // 127.0.0.1 behind a reverse proxy
        public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(3.0);

        /// <summary>Extra top-level sections from other services, each a complete <c>"key":value</c> JSON fragment.</summary>
        public static readonly List<Func<string>> Sections = new List<Func<string>>();

        private static HttpListener m_Listener;
        private static byte[] m_Snapshot = Encoding.UTF8.GetBytes("{}");
        private static readonly object m_Lock = new object();

        public static void Initialize()
        {
            if (Port <= 0)
                return;

            if (!HttpListener.IsSupported)
            {
                Console.WriteLine("[Duel] Web feed disabled: HttpListener is not supported here.");
                return;
            }

            Timer.DelayCall(TimeSpan.Zero, Refresh, Rebuild);

            try
            {
                m_Listener = new HttpListener();
                m_Listener.Prefixes.Add(String.Format("http://{0}:{1}/duel/", Host, Port));
                m_Listener.Start();
                m_Listener.BeginGetContext(OnRequest, null);
                Console.WriteLine("[Duel] Web feed listening on {0}:{1} (/duel/).", Host, Port);
            }
            catch (Exception e)
            {
                Console.WriteLine("[Duel] Web feed failed to start on port {0}: {1}", Port, e.Message);
                m_Listener = null;
            }
        }

        private static void OnRequest(IAsyncResult result)
        {
            HttpListenerContext context = null;

            try
            {
                context = m_Listener.EndGetContext(result);
            }
            catch
            { }

            try
            {
                m_Listener.BeginGetContext(OnRequest, null);
            }
            catch
            { }

            if (context == null)
                return;

            try
            {
                byte[] buffer;

                bool gzip = false;
                string path = context.Request.Url.AbsolutePath;
                if (path == "/duel/" || path == "/duel")
                {
                    lock (m_Lock) buffer = m_Snapshot;
                }
                else if (!DuelReplay.Read(path, out buffer, out gzip))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var response = context.Response;
                response.ContentType = gzip ? "application/x-ndjson; charset=utf-8" : "application/json; charset=utf-8";
                if (gzip) response.AddHeader("Content-Encoding", "gzip");
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Cache-Control", "public, max-age=3");

                if (context.Request.HttpMethod != "GET" && context.Request.HttpMethod != "HEAD")
                {
                    response.StatusCode = 405;
                    response.Close();
                    return;
                }

                response.ContentLength64 = buffer.Length;

                if (context.Request.HttpMethod == "GET")
                    response.OutputStream.Write(buffer, 0, buffer.Length);

                response.Close();
            }
            catch
            {
                try { context.Response.Abort(); } catch { }
            }
        }

        private static void Rebuild()
        {
            byte[] bytes;

            try
            {
                bytes = Encoding.UTF8.GetBytes(Snapshot());
            }
            catch (Exception e)
            {
                Console.WriteLine("[Duel] Web feed snapshot failed: {0}", e.Message);
                return;
            }

            lock (m_Lock)
                m_Snapshot = bytes;
        }

        private static string Snapshot()
        {
            var j = new Json();
            j.Open('{');
            j.Key("shard").Str(ServerList.ServerName);
            j.Key("generated").Time(DateTime.UtcNow);
            j.Key("online").Num(NetState.Instances.Count(ns => ns.Mobile != null));
            j.Key("arenas").Num(DuelArena.All.Count);
            j.Key("replayEnabled").Bool(DuelReplay.Enabled);
            j.Key("replaySampleMs").Num(DuelReplay.SampleMs);
            j.Key("showdownAfterSeconds").Num(DuelMatch.ShowdownAfterSeconds);
            j.Key("roundLimitSeconds").Num((int)DuelMatch.RoundTimeLimit.TotalSeconds);

            j.Key("live").Open('[');
            foreach (DuelMatch m in DuelSystem.Matches.Where(m => m.Phase != DuelPhase.Finished).OrderBy(m => m.Arena.Id))
            {
                j.Open('{');
                j.Key("id").Str(m.Id);
                j.Key("training").Bool(m.Rules.Training);
                j.Key("ranked").Bool(m.Ranked);
                j.Key("arenaName").Str(m.Arena.Name);
                j.Key("arena").Num(m.Arena.Id);
                j.Key("a").Str(m.A.Name);
                j.Key("b").Str(m.B.Name);
                j.Key("scoreA").Num(m.ScoreA);
                j.Key("scoreB").Num(m.ScoreB);
                j.Key("round").Num(m.Round);
                j.Key("rounds").Num(m.Rounds);
                j.Key("rules").Str(m.Rules.ToString());
                j.Key("kind").Str(DuelSystem.Describe(m));
                j.Key("phase").Str(m.Phase.ToString().ToLowerInvariant());
                j.Key("seconds").Num(m.ElapsedSeconds());
                j.Key("showdown").Bool(m.Showdown);
                j.Key("showdownRemaining").Num(m.ShowdownRemaining);
                j.Key("roundLimitSeconds").Num((int)DuelMatch.RoundTimeLimit.TotalSeconds);
                j.Key("started").Time(m.Started);
                j.Key("results");
                Results(j, m.Results);
                j.Close('}');
            }
            j.Close(']');

            j.Key("standings").Open('[');
            var standings = DuelSystem.Records
                .OrderByDescending(kv => kv.Value.MatchWins)
                .ThenBy(kv => kv.Value.MatchLosses)
                .ThenByDescending(kv => kv.Value.RoundWins - kv.Value.RoundLosses)
                .ThenBy(kv => kv.Key.Name, StringComparer.OrdinalIgnoreCase)
                .Take(200);
            foreach (var kv in standings)
            {
                DuelRecord r = kv.Value;
                j.Open('{');
                j.Key("name").Str(kv.Key.Name);
                j.Key("serial").Str(String.Format("0x{0:X}", kv.Key.Serial.Value));
                j.Key("online").Bool(kv.Key.NetState != null);
                j.Key("matchWins").Num(r.MatchWins);
                j.Key("matchLosses").Num(r.MatchLosses);
                j.Key("matchDraws").Num(r.MatchDraws);
                j.Key("roundWins").Num(r.RoundWins);
                j.Key("roundLosses").Num(r.RoundLosses);
                j.Close('}');
            }
            j.Close(']');

            j.Key("recent").Open('[');
            for (int i = DuelSystem.History.Count - 1; i >= 0; i--)
            {
                DuelHistoryEntry h = DuelSystem.History[i];
                j.Open('{');
                j.Key("arena").Num(h.Arena);
                j.Key("a").Str(h.A);
                j.Key("b").Str(h.B);
                j.Key("scoreA").Num(h.ScoreA);
                j.Key("scoreB").Num(h.ScoreB);
                j.Key("rounds").Num(h.Rounds);
                j.Key("rules").Str(h.Rules);
                j.Key("kind").Str(h.Kind);
                j.Key("winner").Str(h.Winner);
                j.Key("aborted").Str(h.Aborted);
                j.Key("started").Time(h.Started);
                j.Key("ended").Time(h.Ended);
                j.Key("results");
                Results(j, h.Results);
                j.Close('}');
            }
            j.Close(']');

            foreach (Func<string> section in Sections)
                j.Raw(section());

            j.Close('}');
            return j.ToString();
        }

        private static void Results(Json j, IEnumerable<DuelRoundResult> results)
        {
            j.Open('[');
            foreach (DuelRoundResult r in results)
            {
                j.Open('{');
                j.Key("winner").Str(r.Winner);
                j.Key("how").Str(r.How);
                j.Key("seconds").Num(r.Seconds);
                j.Close('}');
            }
            j.Close(']');
        }

        /// <summary>Just enough of a JSON writer for the snapshot: commas are placed automatically.</summary>
        private class Json
        {
            private readonly StringBuilder m_Sb = new StringBuilder();
            private bool m_NeedComma;

            public override string ToString() { return m_Sb.ToString(); }

            private void Value(string raw)
            {
                if (m_NeedComma)
                    m_Sb.Append(',');
                m_Sb.Append(raw);
                m_NeedComma = true;
            }

            public Json Open(char c)
            {
                if (m_NeedComma)
                    m_Sb.Append(',');
                m_Sb.Append(c);
                m_NeedComma = false;
                return this;
            }

            public Json Close(char c)
            {
                m_Sb.Append(c);
                m_NeedComma = true;
                return this;
            }

            public Json Key(string key)
            {
                Value(Quote(key));
                m_Sb.Append(':');
                m_NeedComma = false;
                return this;
            }

            public Json Raw(string fragment) { Value(fragment); return this; }
            public Json Str(string s) { Value(s == null ? "null" : Quote(s)); return this; }
            public Json Num(int n) { Value(n.ToString(CultureInfo.InvariantCulture)); return this; }
            public Json Bool(bool b) { Value(b ? "true" : "false"); return this; }

            /// <summary>Every time the duel service keeps is UTC; a save round-trip drops the Kind, so stamp it back.</summary>
            public Json Time(DateTime t)
            {
                return t == DateTime.MinValue ? Str(null) : Str(DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }

            private static string Quote(string s)
            {
                var sb = new StringBuilder(s.Length + 2);
                sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20 || c == '<' || c == '>' || c == '&')
                                sb.AppendFormat("\\u{0:x4}", (int)c);
                            else
                                sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
                return sb.ToString();
            }
        }
    }
}
