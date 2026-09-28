using Server.Gumps;
using Server.Mobiles;
using Server.Network;
namespace Server.Engines.Dueling
{
    public class ArenaGump : Gump
    {
        public ArenaGump(PlayerMobile p) : base(60,40)
        {
            AddPage(0);AddBackground(0,0,570,465,9200);
            AddLabel(25,20,1153,"ROWAN / LOBBY SUPPLIES & HELP");
            AddLabel(25,55,0,"Free supplies and preparation. Matchmaking keeps your build.");
            Button(25,95,1,"Enter lobby");Button(265,95,15,"Duel / waiting list");
            Button(25,140,13,"7GM skill ball");Button(265,140,16,"5GM skill ball");
            Button(25,185,14,"Stat ball");Button(265,185,5,"Refill supplies");
            Button(25,230,10,"Blue robe");Button(265,230,11,"Dark cloak");
            Button(25,275,12,"Wizard hat");Button(265,275,17,"Leather armor set");
            Button(25,320,18,"How to duel");Button(265,320,19,"Tour arenas / gate");
            Button(25,365,20,"Hair / clothing dyes");
            AddLabel(25,420,0,"Free potions, reagents, armor, clothes and cosmetic supplies.");
        }
        private void Button(int x,int y,int id,string text)
        {AddButton(x,y,4005,4007,id,GumpButtonType.Reply,0);AddLabel(x+35,y,0,text);}
        public override void OnResponse(NetState sender,RelayInfo info)
        {
            var p=sender.Mobile as PlayerMobile;if(p==null || !ArenaService.Enabled)return;
            switch(info.ButtonID)
            {
                case 0:return;
                case 1:ArenaService.Enter(p);return;
                case 15:ArenaService.Open(p);return;
                case 13:ArenaTraining.GiveBall(p);break;
                case 16:ArenaTraining.GiveBall(p,5);break;
                case 14:ArenaTraining.GiveStatBall(p);return;
                case 5:ArenaSupplies.Refill(p);break;
                case 17:ArenaSupplies.ArmorKit(p);break;
                case 20:ArenaCosmeticsGump.Open(p);return;
                case 18:ArenaTrainingGuideGump.Open(p);return;
                case 19:p.SendMessage("[Arena] Step into the blue moongate beside Rowan to choose an arena. Each arena has a return gate.");break;
                case 10:ArenaSupplies.Style(p,"robe",6);break;
                case 11:ArenaSupplies.Style(p,"cloak",2);break;
                case 12:ArenaSupplies.Style(p,"hat",6);break;
            }
            ArenaService.OpenPreparation(p);
        }
    }
    public class ArenaCosmeticsGump : Gump
    {
        public static void Open(PlayerMobile p)
        { ArenaMatchmaking.ClosePanels(p);p.CloseGump(typeof(ArenaCosmeticsGump));p.SendGump(new ArenaCosmeticsGump()); }
        public ArenaCosmeticsGump() : base(60,40)
        {
            AddPage(0);AddBackground(0,0,650,390,9200);
            AddLabel(25,20,1153,"ROWAN / HAIR & CLOTHING COLORS");
            string[] lines={
                "Free kit: hair styling deed, hair dye, dyes, cloth tub and leather tub.",
                "Hair: double-click Hair Dye, choose a shade, then confirm.",
                "No hair? Use the hair styling deed first; then use Hair Dye.",
                "Clothes: double-click the cloth tub, then target your robe or clothes.",
                "Leather: double-click the leather tub, then target your leather armor.",
                "Change tub color: double-click Dyes, target a tub, choose a color.",
                "Put clothing/armor in your backpack. Other players' gear is protected.",
                "Tubs and dyes are reusable. Return here when a consumable is used."
            };
            for(int i=0;i<lines.Length;i++)AddLabel(25,65+i*29,0,lines[i]);
            AddButton(25,330,4005,4007,1,GumpButtonType.Reply,0);AddLabel(60,330,0,"Get / refill cosmetic kit");
            AddButton(370,330,4005,4007,2,GumpButtonType.Reply,0);AddLabel(405,330,0,"Back to Rowan");
        }
        public override void OnResponse(NetState sender,RelayInfo info)
        {
            var p=sender.Mobile as PlayerMobile;if(p==null || !ArenaService.Enabled || DuelSystem.FindMatchOf(p)!=null)return;
            if(info.ButtonID==1){ArenaSupplies.Cosmetics(p);Open(p);}
            else if(info.ButtonID==2)ArenaService.OpenPreparation(p);
        }
    }
}
