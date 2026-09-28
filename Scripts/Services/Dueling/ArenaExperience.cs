using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Server.Commands;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.Dueling
{
    // Public labels are self-reported, never evidence of a model or a human identity.
    public static class ArenaExperience
    {
        private class Profile { public string Kind="undeclared", Model="", Version="", Policy=""; }
        private static readonly Dictionary<PlayerMobile,Profile> Profiles=new Dictionary<PlayerMobile,Profile>();
        public static int Completed, Aborted, DisconnectRounds, ExpiredWaiting;
        private static readonly DateTime Boot=DateTime.UtcNow;
        public static string Q(string s){return s==null ? "null" : ArenaService.Json(s);}
        public static string ProfileJson(Mobile mobile)
        {
            Profile p;var m=mobile as PlayerMobile;
            if(m==null || !Profiles.TryGetValue(m,out p))p=new Profile();
            return "{\"kind\":"+Q(p.Kind)+",\"model\":"+Q(p.Model)+",\"version\":"+Q(p.Version)+",\"policy\":"+Q(p.Policy)+",\"selfReported\":true}";
        }
        public static void Initialize()
        {
            CommandSystem.Register("ArenaAgent",AccessLevel.Player,e=>{
                var p=e.Mobile as PlayerMobile;
                if(p==null || !ArenaService.Enabled || DuelSystem.FindMatchOf(p)!=null)return;
                if(e.Length!=3 || Enumerable.Range(0,3).Any(i=>!Regex.IsMatch(e.GetString(i),"^[A-Za-z0-9_.+-]{1,48}$")))
                {p.SendMessage("[Arena] Usage: [ArenaAgent model version policy (letters, digits, . _ + -; 48 chars each). Public self-reported labels.");return;}
                Profiles[p]=new Profile {Kind="bot",Model=e.GetString(0),Version=e.GetString(1),Policy=e.GetString(2)};
                p.SendMessage("[Arena] Bot labels registered publicly (self-reported).");
            });
            CommandSystem.Register("ArenaHuman",AccessLevel.Player,e=>{
                var p=e.Mobile as PlayerMobile;if(p!=null && DuelSystem.FindMatchOf(p)==null)Profiles[p]=new Profile {Kind="human"};
            });
            Timer.DelayCall(TimeSpan.FromMinutes(1),TimeSpan.FromMinutes(1),()=>{
                foreach(var p in Profiles.Keys.Where(p=>p.Deleted || (p.NetState==null && DuelSystem.FindMatchOf(p)==null)).ToList())Profiles.Remove(p);
            });
            DuelWeb.Sections.Add(Snapshot);
        }
        private static string Snapshot()
        {
            var players=NetState.Instances.Select(n=>n.Mobile as PlayerMobile).Where(p=>p!=null && !p.Deleted && p.AccessLevel==AccessLevel.Player && ArenaService.InLobby(p)).Distinct().ToList();
            var waiting=ArenaMatchmaking.PublicEntries();
            string rows=String.Join(",",waiting.Take(100).Select(e=>"{\"serial\":"+e.Player.Serial.Value+",\"name\":"+Q(e.Player.Name)+",\"rules\":"+Q(ArenaMatchmaking.Name(e.Template))+",\"ranked\":"+(e.Ranked?"true":"false")+",\"auto\":"+(e.Auto?"true":"false")+",\"arena\":"+Q(ArenaMatchmaking.ArenaName(e.ArenaId))+",\"waitingSeconds\":"+(int)(DateTime.UtcNow-e.Joined).TotalSeconds+",\"profile\":"+ProfileJson(e.Player)+"}"));
            string people=String.Join(",",players.Take(100).Select(p=>"{\"serial\":"+p.Serial.Value+",\"name\":"+Q(p.Name)+",\"profile\":"+ProfileJson(p)+"}"));
            return "\"activity\":{\"lobby\":["+people+"],\"waiting\":["+rows+"],\"waitingCount\":"+waiting.Count+"},\"operations\":{\"uptimeSeconds\":"+(int)(DateTime.UtcNow-Boot).TotalSeconds+",\"completed\":"+Completed+",\"aborted\":"+Aborted+",\"disconnectRounds\":"+DisconnectRounds+",\"expiredWaiting\":"+ExpiredWaiting+",\"replayFailures\":"+DuelReplay.Failures+",\"replayDroppedRows\":"+DuelReplay.DroppedRows+",\"rankedStorageHealthy\":"+(ArenaLadder.Healthy?"true":"false")+"},\"ladder\":"+ArenaLadder.Snapshot();
        }
        public static void Result(DuelMatch m,PlayerMobile winner,string aborted)
        {
            if(aborted==null)Completed++;else Aborted++;
            if(m.Rules.Training)return;
            foreach(var p in new[]{m.A,m.B})if(p!=null && !p.Deleted && p.NetState!=null)
            {ArenaMatchmaking.ClosePanels(p);p.SendGump(new ArenaResultGump(m,winner,aborted));}
        }
    }

    public class ArenaQuickSetupGump:Gump
    {
        private readonly int Template,ArenaId;private readonly bool Ranked;
        public ArenaQuickSetupGump(int template,int arenaId,bool ranked):base(50,50)
        {
            Template=template;ArenaId=arenaId;Ranked=ranked;
            AddPage(0);AddBackground(0,0,650,300,9200);
            AddLabel(25,20,1153,"QUICK PREPARATION / "+ArenaMatchmaking.Name(template));
            AddLabel(25,60,0,"Replace skills: Magery, Eval Int, Meditation, Resist, Wrestling = 100.");
            AddLabel(25,90,0,template==1 ? "Also Anatomy and Alchemy = 100. All other skills become zero." : "All other skills become zero. You can customize with skill balls later.");
            AddLabel(25,125,0,"Replace stats: STR 100 / DEX 25 / INT 100. Skills and stats locked.");
            AddLabel(25,155,0,"Refill spellbook, reagents and potions. Equipment stays yours.");
            AddLabel(25,185,0,"This changes your character. It does not join a queue automatically.");
            AddButton(25,240,4005,4007,1,GumpButtonType.Reply,0);AddLabel(60,240,0,"Apply and return to duel board");
            AddButton(395,240,4005,4007,2,GumpButtonType.Reply,0);AddLabel(430,240,0,"Cancel");
        }
        public override void OnResponse(NetState sender,RelayInfo info)
        {
            var p=sender.Mobile as PlayerMobile;if(p==null)return;
            if(info.ButtonID==1)
            {
                if(!ArenaTraining.CanEdit(p) || DuelSystem.HasPending(p))return;
                int[] skills=Template==0 ? new[]{25,16,46,26,43} : new[]{25,16,46,26,43,1,0};
                for(int i=0;i<p.Skills.Length;i++)p.Skills[i].Base=0;
                foreach(int i in skills)p.Skills[i].Base=100;
                for(int i=0;i<p.Skills.Length;i++)p.Skills[i].SetLockNoRelay(SkillLock.Locked);
                p.RawStr=p.RawDex=p.RawInt=10;p.RawStr=100;p.RawDex=25;p.RawInt=100;
                p.StrLock=p.DexLock=p.IntLock=StatLockType.Locked;
                DuelMatch.FullHeal(p);ArenaSupplies.Refill(p);
            }
            if(info.ButtonID==1 || info.ButtonID==2)ArenaDuelSetupGump.Open(p,Template,0,ArenaId,Ranked);
        }
    }
    public class ArenaResultGump:Gump
    {
        private readonly DuelMatch Match;private readonly DateTime Expires=DateTime.UtcNow.AddMinutes(2);
        public ArenaResultGump(DuelMatch m,PlayerMobile winner,string aborted):base(60,60)
        {
            Match=m;AddPage(0);AddBackground(0,0,650,330,9200);
            AddLabel(25,20,1153,"MATCH COMPLETE");
            AddLabel(25,55,0,aborted!=null ? "Interrupted: "+aborted : winner==null ? "Draw" : winner.Name+" wins!");
            AddLabel(25,90,0,m.A.Name+" "+m.ScoreA+" - "+m.ScoreB+" "+m.B.Name);
            AddLabel(25,125,0,(m.Ranked ? "Ranked / " : "Friendly / ")+ArenaMatchmaking.RuleName(m.Rules));
            AddLabel(25,155,0,"Rematch sends a new invitation. Both players must agree (2 minutes).");
            AddLabel(25,185,0,m.LadderResult ?? (aborted!=null ? "No rating change." : "No template rating change."));
            AddButton(25,250,4005,4007,1,GumpButtonType.Reply,0);AddLabel(60,250,0,"Rematch");
            AddButton(225,250,4005,4007,2,GumpButtonType.Reply,0);AddLabel(260,250,0,"Replay");
            AddButton(420,250,4005,4007,3,GumpButtonType.Reply,0);AddLabel(455,250,0,"Lobby / duel board");
            AddLabel(25,295,0,"Replays may take a few seconds to finish saving.");
        }
        public override void OnResponse(NetState sender,RelayInfo info)
        {
            var p=sender.Mobile as PlayerMobile;
            if(p==null || !Match.IsFighter(p) || DuelSystem.FindMatchOf(p)!=null)return;
            if(info.ButtonID==1)
            {
                var other=Match.Opponent(p) as PlayerMobile;
                if(DateTime.UtcNow>Expires || !ArenaService.InLobby(p) || !ArenaService.InLobby(other))
                {p.SendMessage("[Arena] Rematch expired or opponent left the lobby. Use the duel board.");return;}
                DuelSystem.Challenge(p,other,Match.Rounds,Match.Rules,Match.Arena,Match.Ranked);
            }
            else if(info.ButtonID==2)p.LaunchBrowser("https://arena.uotavern.com/replay/?replay="+Match.Id);
            else if(info.ButtonID==3)ArenaService.Enter(p);
        }
    }
}
