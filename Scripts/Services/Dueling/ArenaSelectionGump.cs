using Server.Gumps;
using Server.Mobiles;
using Server.Network;
namespace Server.Engines.Dueling
{
    public class ArenaSelectionGump : Gump
    {
        private readonly int Template;
        public ArenaSelectionGump(int template,int current):base(50,40)
        {
            Template=template;AddPage(0);AddBackground(0,0,610,440,9200);
            AddLabel(25,20,1153,"DUEL / ARENA");
            AddButton(25,60,4005,4007,1,GumpButtonType.Reply,0);AddLabel(60,60,0,"Random free arena (default)");
            AddLabel(25,100,0,"Auto match waits if your chosen arena is occupied.");
            int i=0;
            foreach(var a in DuelArena.All)
            {
                int x=25+(i%2)*285,y=145+(i/2)*36;
                AddButton(x,y,4005,4007,100+a.Id,GumpButtonType.Reply,0);
                AddLabel(x+35,y,0,(a.Id==current ? "* " : "")+"Arena "+a.Id+(a.Id==13 ? " Large" : a.Id==14 ? " Corridor" : "")+(a.Busy ? " (busy)" : ""));i++;
            }
        }
        public override void OnResponse(NetState sender,RelayInfo info)
        {
            var p=sender.Mobile as PlayerMobile;if(p==null)return;
            if(info.ButtonID==1)ArenaDuelSetupGump.Open(p,Template);
            else if(DuelArena.Get(info.ButtonID-100)!=null)ArenaDuelSetupGump.Open(p,Template,0,info.ButtonID-100);
        }
    }
}
