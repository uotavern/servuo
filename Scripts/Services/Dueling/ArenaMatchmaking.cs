using System;
using System.Collections.Generic;
using System.Linq;
using Server.Mobiles;

namespace Server.Engines.Dueling
{
    // One public lobby for template duels; legacy rated queues remain command-compatible.
    public static class ArenaMatchmaking
    {
        public class Entry
        {
            public PlayerMobile Player;
            public int Template, ArenaId;
            public bool Auto, Ranked;
            public DateTime Joined, Renewed;
        }
        private static readonly List<Entry> Waiting = new List<Entry>();
        public static void Initialize()
        {
            Timer.DelayCall(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), Tick);
        }
        public static string Name(int template) { return template == 0 ? "5x Mage" : "7x + Explosion Potions"; }
        public static DuelRules Rules(int template)
        {
            DuelRules rules; string error;
            DuelRules.TryParse(template == 0 ? "mage5-noexplosion" : "standard7-explosion", out rules, out error);
            return rules;
        }
        public static string RuleName(DuelRules rules)
        {
            for (int i = 0; i < 2; i++) if (rules.ToString() == Rules(i).ToString()) return Name(i);
            return rules.ToString();
        }
        public static int Count { get { return Waiting.Count; } }
        public static bool Contains(PlayerMobile p) { return Waiting.Any(e => e.Player == p); }
        public static void Remove(PlayerMobile p) { Waiting.RemoveAll(e => e.Player == p); }
        private static bool Present(PlayerMobile p)
        {
            return p != null && !p.Deleted && p.NetState != null && p.Alive && p.AccessLevel == AccessLevel.Player
                && ArenaService.InLobby(p) && DuelSystem.FindMatchOf(p) == null && !ArenaService.IsLegacyQueued(p);
        }
        private static string Problem(PlayerMobile p, int template)
        {
            if (!Present(p)) return "Return to the lobby alive and leave any legacy queue first.";
            if (p.Combatant != null || p.Criminal || p.Aggressors.Count > 0 || p.Aggressed.Count > 0)
                return "Leave combat before joining.";
            string reason;
            return Rules(template).CheckSkills(p, out reason) ? null : reason + " Use the skill ball in Preparation.";
        }
        public static void Join(PlayerMobile p, int template, bool auto, int arenaId = 0, bool ranked = false)
        {
            if (!ArenaService.Enabled || template < 0 || template > 1 || (arenaId != 0 && DuelArena.Get(arenaId) == null)) return;
            if(ranked && !ArenaLadder.Healthy){p.SendMessage("[Arena] Ranked matchmaking is unavailable while storage is unhealthy.");return;}
            string problem = Problem(p, template);
            if (problem != null) { p.SendMessage(0x35, "[Duel] " + problem); return; }
            if (DuelSystem.HasPending(p)) { p.SendMessage(0x35, "[Duel] Reply to or cancel your pending invitation first."); return; }
            if (!ArenaService.AccountAvailable(p) || Waiting.Any(e => e.Player != p && e.Player.Account == p.Account))
            { p.SendMessage(0x35, "[Duel] This account already has a waiting character."); return; }
            var old=Waiting.FirstOrDefault(e=>e.Player==p && e.Template==template && e.Auto==auto && e.Ranked==ranked && e.ArenaId==arenaId);
            Remove(p);
            Waiting.Add(new Entry { Player=p, Template=template, ArenaId=arenaId, Auto=auto, Ranked=ranked, Joined=old==null ? DateTime.UtcNow : old.Joined, Renewed=DateTime.UtcNow });
            p.SendMessage(0x35, "[Duel] " + Name(template) + (auto ? ": automatic matching enabled." : ": listed for challenges; you choose whether to accept.") + " Waiting expires in 10 minutes.");
        }
        public static List<Entry> PublicEntries(){return Waiting.Where(e=>Present(e.Player) && !DuelSystem.HasPending(e.Player) && DateTime.UtcNow-e.Renewed<TimeSpan.FromMinutes(10)).ToList();}
        public static List<Entry> List(int template, bool ranked=false)
        {
            return Waiting.Where(e => e.Template == template && e.Ranked==ranked && Present(e.Player) && !DuelSystem.HasPending(e.Player)
                && DateTime.UtcNow-e.Renewed < TimeSpan.FromMinutes(10)).ToList();
        }
        public static string ArenaName(int id) { return id==0 ? "Random free arena" : DuelArena.Get(id)==null ? "Unavailable arena" : DuelArena.Get(id).Name; }
        public static string Status(PlayerMobile p)
        {
            var entry=Waiting.FirstOrDefault(e=>e.Player==p);
            return entry == null ? "Not waiting" : Name(entry.Template) + " / " + ArenaName(entry.ArenaId) + (entry.Auto ? " / Auto match" : " / Listed for challenges");
        }
        public static void ChallengeListed(PlayerMobile p, Entry entry, int template, int arenaId = 0, bool ranked=false)
        {
            if (!Waiting.Contains(entry) || entry.Template != template || entry.Ranked!=ranked || !Present(entry.Player) || DuelSystem.HasPending(entry.Player)
                || DateTime.UtcNow-entry.Renewed >= TimeSpan.FromMinutes(10))
            { p.SendMessage(0x35, "[Duel] This player is no longer available. Refresh the list."); return; }
            if (p == entry.Player || p.Account == entry.Player.Account) { p.SendMessage(0x35,"[Duel] Choose another account's player."); return; }
            if(arenaId!=0 && entry.ArenaId!=0 && arenaId!=entry.ArenaId) { p.SendMessage("[Duel] This player selected Arena "+entry.ArenaId+". Choose that arena or Random first.");return; }
            string problem=Problem(p, template);
            if (problem != null) { p.SendMessage(0x35,"[Duel] " + problem); return; }
            DuelSystem.Challenge(p,entry.Player,3,Rules(template),DuelArena.Get(arenaId!=0 ? arenaId : entry.ArenaId),ranked);
        }
        private static void Tick()
        {
            if (!ArenaService.Enabled) { Waiting.Clear(); return; }
            foreach (var entry in Waiting.ToList())
            {
                if (Problem(entry.Player,entry.Template) != null || DateTime.UtcNow-entry.Renewed >= TimeSpan.FromMinutes(10))
                {
                    if(DateTime.UtcNow-entry.Renewed>=TimeSpan.FromMinutes(10))ArenaExperience.ExpiredWaiting++;
                    Remove(entry.Player);
                    if (entry.Player != null && !entry.Player.Deleted && entry.Player.NetState != null)
                        entry.Player.SendMessage(0x35,"[Duel] You left the waiting list (expired, left lobby or no longer ready).");
                }
            }
            foreach (var entry in Waiting.ToList())
            {
                if (!Waiting.Contains(entry) || !entry.Auto || DuelSystem.HasPending(entry.Player) || DuelArena.FindFree() == null) continue;
                var other=Waiting.FirstOrDefault(e=>e!=entry && e.Auto && e.Ranked==entry.Ranked && e.Template==entry.Template
                    && e.Player.Account!=entry.Player.Account && !DuelSystem.HasPending(e.Player)
                    && (e.ArenaId==0 || entry.ArenaId==0 || e.ArenaId==entry.ArenaId)
                    && (e.ArenaId==0 || !DuelArena.Get(e.ArenaId).Busy)
                    && (entry.ArenaId==0 || !DuelArena.Get(entry.ArenaId).Busy));
                if (other != null) DuelSystem.StartMatch(entry.Player,other.Player,3,Rules(entry.Template),entry.Player,DuelArena.Get(entry.ArenaId!=0 ? entry.ArenaId : other.ArenaId),entry.Ranked);
            }
        }
        public static void ClosePanels(PlayerMobile p)
        {
            p.CloseGump(typeof(ArenaCosmeticsGump));
            p.CloseGump(typeof(ArenaResultGump));
            p.CloseGump(typeof(ArenaQuickSetupGump));
            p.CloseGump(typeof(ArenaGump));
            p.CloseGump(typeof(ArenaDuelSetupGump));
            p.CloseGump(typeof(ArenaDuelInviteGump));
            p.CloseGump(typeof(ArenaStewardGump));
            p.CloseGump(typeof(ArenaTrainingGuideGump));
            p.CloseGump(typeof(ArenaSkillsGump));
            p.CloseGump(typeof(ArenaStatsGump));
            p.CloseGump(typeof(ArenaTravelGump));
            p.CloseGump(typeof(ArenaSelectionGump));
        }
    }
}
