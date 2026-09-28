using Server.Gumps;
using Server.Mobiles;
using Server.Network;
namespace Server.Engines.Dueling
{
    public class ArenaGump : Gump
    {
        public ArenaGump(PlayerMobile p) : base(60,40)
        {
            AddPage(0);AddBackground(0,0,570,420,9200);
            AddLabel(25,20,1153,"ROWAN / LOBBY SUPPLIES & HELP");
            AddLabel(25,55,0,"Free supplies and preparation. Matchmaking keeps your build.");
            Button(25,95,1,"Enter lobby");Button(265,95,15,"Duel / waiting list");
            Button(25,140,13,"7GM skill ball");Button(265,140,16,"5GM skill ball");
            Button(25,185,14,"Stat ball");Button(265,185,5,"Refill supplies");
            Button(25,230,10,"Blue robe");Button(265,230,11,"Dark cloak");
            Button(25,275,12,"Wizard hat");Button(265,275,17,"Leather armor set");
            Button(25,320,18,"How to duel");Button(265,320,19,"Tour arenas / gate");
            AddLabel(25,375,0,"Refill supplies: potions, reagents, bandages, spellbook and hair items.");
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
                case 18:ArenaTrainingGuideGump.Open(p);return;
                case 19:p.SendMessage("[Arena] Step into the blue moongate beside Rowan to choose an arena. Each arena has a return gate.");break;
                case 10:ArenaSupplies.Style(p,"robe",6);break;
                case 11:ArenaSupplies.Style(p,"cloak",2);break;
                case 12:ArenaSupplies.Style(p,"hat",6);break;
            }
            ArenaService.OpenPreparation(p);
        }
    }
}
