using System;
using System.Collections.Generic;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;
using Server.Targeting;

namespace Server.Engines.Dueling
{
    public class ArenaDuelSetupGump : Gump
    {
        private readonly int Template, Page, ArenaId;
        private readonly List<ArenaMatchmaking.Entry> WaitingEntries;
        public ArenaDuelSetupGump() : this(null, 1, 0) { }
        public ArenaDuelSetupGump(PlayerMobile player, int template = 1, int page = 0, int arenaId = 0) : base(40, 40)
        {
            Template = template == 0 ? 0 : 1;
            ArenaId = DuelArena.Get(arenaId)==null ? 0 : arenaId;
            WaitingEntries = ArenaMatchmaking.List(Template);
            Page = Math.Max(0,Math.Min(page,Math.Max(0,(WaitingEntries.Count-1)/6)));
            AddPage(0); AddBackground(0,0,660,600,9200);
            AddLabel(25,20,1153,"DUEL / choose rules, then an opponent");
            Button(25,55,101,(Template==0 ? "[X] " : "[ ] ")+"5x Mage");
            Button(280,55,102,(Template==1 ? "[X] " : "[ ] ")+"7x + EX pot");
            AddLabel(25,90,0,Template==0 ? "5 mage skills / no weapons, armor, bandages, paralyze or EX pots" : "7 skills / weapons, armor, spells, bandages and EX pots allowed");
            AddLabel(25,115,0,"Regular potions allowed. Current build. Best of 3. No queue Elo.");
            Button(25,150,40,"Arena: "+ArenaMatchmaking.ArenaName(ArenaId)+" / Change");
            Button(25,195,10,"Auto match"); Button(320,195,11,"Target a player");
            Button(25,235,12,"List me for challenges"); Button(320,235,13,"Leave waiting list");
            AddLabel(25,275,53,player==null ? "Choose an option above." : ArenaMatchmaking.Status(player));
            AddLabel(25,310,1153,"WAITING / "+ArenaMatchmaking.Name(Template));
            Button(475,305,14,"Refresh");
            int end=Math.Min(WaitingEntries.Count,(Page+1)*6);
            for(int i=Page*6;i<end;i++)
            {
                var e=WaitingEntries[i]; string name=e.Player.Name ?? "Player";
                if(name.Length>24)name=name.Substring(0,24);
                Button(25,345+(i-Page*6)*27,1000+i,name+(e.Player==player ? " (you)" : " / Challenge"));
                AddLabel(415,345+(i-Page*6)*27,0,(e.Auto ? "Auto / " : "Invite / ")+(e.ArenaId==0 ? "Random" : "Arena "+e.ArenaId));
            }
            if(WaitingEntries.Count==0)AddLabel(25,350,0,"Nobody waiting for these rules. Auto match or list yourself.");
            Button(25,535,20,"Preparation / supplies");
            if(Page>0)Button(310,535,30,"Previous");
            if(end<WaitingEntries.Count)Button(440,535,31,"Next");
            AddLabel(25,575,0,"Listed players accept your invitation before the match begins.");
        }
        private void Button(int x,int y,int id,string text)
        { AddButton(x,y,4005,4007,id,GumpButtonType.Reply,0);AddLabel(x+35,y,0,text); }
        public static void Open(PlayerMobile p,int template=1,int page=0,int arenaId=0)
        {
            if(p==null || !ArenaService.Enabled)return;
            if(DuelSystem.FindMatchOf(p)!=null){p.SendMessage("[Duel] Finish your match first.");return;}
            ArenaMatchmaking.ClosePanels(p);p.SendGump(new ArenaDuelSetupGump(p,template,page,arenaId));
        }
        public override void OnResponse(NetState sender,RelayInfo info)
        {
            var p=sender.Mobile as PlayerMobile;
            if(p==null || !ArenaService.Enabled || info.ButtonID==0 || DuelSystem.FindMatchOf(p)!=null)return;
            int id=info.ButtonID;
            if(id==101 || id==102){Open(p,id-101,0,ArenaId);return;}
            if(id==11){p.SendMessage("[Duel] Select a player: "+ArenaMatchmaking.Name(Template));p.Target=new OpponentTarget(Template,ArenaId);return;}
            if(id==40){ArenaMatchmaking.ClosePanels(p);p.SendGump(new ArenaSelectionGump(Template,ArenaId));return;}
            if(id==20){ArenaService.OpenPreparation(p);return;}
            if(id>=1000 && id-1000<WaitingEntries.Count){ArenaMatchmaking.ChallengeListed(p,WaitingEntries[id-1000],Template,ArenaId);return;}
            if(id==10 || id==12)ArenaMatchmaking.Join(p,Template,id==10,ArenaId);
            if(id==13){ArenaService.Leave(p);DuelSystem.CancelChallengeBy(p);}
            Open(p,Template,id==30 ? Page-1 : id==31 ? Page+1 : Page,ArenaId);
        }
        private class OpponentTarget : Target
        {
            private readonly int Template, ArenaId;
            public OpponentTarget(int template,int arenaId):base(12,false,TargetFlags.None){Template=template;ArenaId=arenaId;}
            protected override void OnTarget(Mobile from,object target)
            {
                var a=from as PlayerMobile;var b=target as PlayerMobile;
                if(a==null || b==null){from.SendMessage("Select another player or participant agent.");return;}
                DuelSystem.Challenge(a,b,3,ArenaMatchmaking.Rules(Template),DuelArena.Get(ArenaId));
            }
        }
    }
    public class ArenaDuelInviteGump : Gump
    {
        private readonly DuelChallenge Challenge;
        public ArenaDuelInviteGump(DuelChallenge challenge) : base(60, 60)
        {
            Challenge = challenge;
            AddPage(0); AddBackground(0, 0, 610, 295, 9200);
            AddLabel(25, 20, 1153, "DUEL INVITATION");
            AddLabel(25, 55, 0, "From: " + challenge.Challenger.Name);
            AddLabel(25, 90, 0, ArenaMatchmaking.RuleName(challenge.Rules));
            AddLabel(25, 125, 0, "Best of " + challenge.Rounds + " / Your current build / 5-second countdown");
            AddLabel(25, 160, 0, "Arena: " + (challenge.Arena==null ? "Random free arena" : challenge.Arena.Id.ToString()));
            AddLabel(25, 195, 0, "Accept to enter the arena. Decline to keep waiting.");
            AddButton(25, 250, 4005, 4007, 1, GumpButtonType.Reply, 0); AddLabel(60, 250, 0, "Accept");
            AddButton(300, 250, 4005, 4007, 2, GumpButtonType.Reply, 0); AddLabel(335, 250, 0, "Decline");
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        {
            var p = sender.Mobile as PlayerMobile;
            if (p != null && (info.ButtonID == 1 || info.ButtonID == 2)) DuelSystem.ReplyToChallenge(p, Challenge, info.ButtonID == 1);
        }
    }
}
