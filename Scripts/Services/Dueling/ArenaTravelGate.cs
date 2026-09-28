using System;
using System.Linq;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.Dueling
{
    public class ArenaTravelGate : Item
    {
        public int ArenaId { get; private set; }
        public static void Initialize()
        {
            if(ArenaService.Enabled)Timer.DelayCall(TimeSpan.FromSeconds(3),Install);
        }
        private static void Install()
        {
            foreach(var retired in World.Items.Values.OfType<ArenaTravelGate>().Where(g=>g.ArenaId!=0 && DuelArena.Get(g.ArenaId)==null).ToList())retired.Delete();
            Ensure(0,new Point3D(ArenaService.Lobby.X,ArenaService.Lobby.Y+3,ArenaService.Lobby.Z));
            foreach(var arena in DuelArena.All)Ensure(arena.Id,arena.GateLocation);
        }
        private static void Ensure(int id,Point3D location)
        {
            var gates=World.Items.Values.OfType<ArenaTravelGate>().Where(g=>!g.Deleted && g.ArenaId==id).ToList();
            var gate=gates.FirstOrDefault() ?? new ArenaTravelGate(id);
            foreach(var duplicate in gates.Skip(1))duplicate.Delete();
            gate.MoveToWorld(location,DuelArena.ArenaMap);
        }
        public ArenaTravelGate(int id):base(0xF6C)
        { ArenaId=id;Movable=false;Light=LightType.Circle300;Name=id==0 ? "Arena tour moongate" : "Arena return moongate"; }
        public ArenaTravelGate(Serial serial):base(serial){}
        public override bool OnMoveOver(Mobile from){Show(from);return true;}
        public override void OnDoubleClick(Mobile from){Show(from);}
        private void Show(Mobile from)
        {
            var p=from as PlayerMobile;
            if(p==null || !CanTravel(p,this))return;
            ArenaMatchmaking.ClosePanels(p);p.SendGump(new ArenaTravelGump(this));
        }
        public static bool CanTravel(PlayerMobile p,ArenaTravelGate gate)
        {
            if(!ArenaService.Enabled || p==null || gate==null || gate.Deleted || p.Map!=gate.Map || !p.InRange(gate.Location,3))return false;
            if(!p.Alive || DuelSystem.FindMatchOf(p)!=null || ArenaService.IsQueued(p) || DuelSystem.HasPending(p)
                || p.Combatant!=null || p.Criminal || p.Aggressors.Count>0 || p.Aggressed.Count>0)
            {p.SendMessage("[Arena] Travel while alive, out of combat, and not waiting for a duel. Use [Arena leave first.");return false;}
            return true;
        }
        public override void Serialize(GenericWriter writer){base.Serialize(writer);writer.Write(0);writer.Write(ArenaId);}
        public override void Deserialize(GenericReader reader){base.Deserialize(reader);reader.ReadInt();ArenaId=reader.ReadInt();}
    }
    public class ArenaTravelGump : Gump
    {
        private readonly ArenaTravelGate Gate;
        public ArenaTravelGump(ArenaTravelGate gate):base(50,40)
        {
            Gate=gate;AddPage(0);AddBackground(0,0,670,485,9200);
            AddLabel(25,20,1153,"MOONGATE / ARENA TOUR");
            AddLabel(25,55,0,"Visit the sidelines. You will never be placed inside an active ring.");
            AddButton(25,90,4005,4007,1,GumpButtonType.Reply,0);AddLabel(60,90,0,"Return to Rowan / main lobby");
            int index=0;
            foreach(var a in DuelArena.All)
            {
                int x=25+(index%2)*325,y=140+(index/2)*60;
                AddButton(x,y,4005,4007,100+a.Id,GumpButtonType.Reply,0);
                AddLabel(x+35,y,0,a.Id+" / "+a.Name);
                AddLabel(x+35,y+23,0,(a.Temporary ? "Temporary ring" : "Native venue")+" / "+(a.Busy ? "In match" : "Open"));index++;
            }
            AddLabel(25,450,0,"Tours only. Choose the match arena separately on the duel board.");
        }
        public override void OnResponse(NetState sender,RelayInfo info)
        {
            var p=sender.Mobile as PlayerMobile;
            if(!ArenaTravelGate.CanTravel(p,Gate))return;
            if(info.ButtonID==1){p.MoveToWorld(ArenaService.Lobby,DuelArena.ArenaMap);ArenaService.OpenPreparation(p);return;}
            var arena=DuelArena.Get(info.ButtonID-100);if(arena==null)return;
            // Exit marks are the established safe positions outside the arena fence.
            p.MoveToWorld(arena.ExitB,DuelArena.ArenaMap);
            p.SendMessage("[Arena] "+arena.Name+" sidelines. The blue gate beside you returns to Rowan.");
        }
    }
}
