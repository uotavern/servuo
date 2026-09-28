using System;
using System.Linq;
using Server.Gumps;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.Dueling
{
    public class ArenaSteward : Mobile
    {
        private static int Tip;
        private static readonly string[] Tips = {
            "Need a build? Double-click me for a free stat ball and 5/7GM skill balls!",
            "Blue skill balls set 5 or 7 skills to 100.0 and reset all others.",
            "The gold stat ball sets STR, DEX and INT: 10-100 each, 225 total maximum.",
            "Say [Arena to choose 5x or 7x + EX pot. Auto match, target someone or browse waiting players.",
            "The blue moongate beside me lets you visit every arena. Return gates bring you home."
        };
        public static void Initialize()
        {
            if (!ArenaService.Enabled) return;
            Timer.DelayCall(TimeSpan.FromSeconds(2), EnsureInstalled);
            Timer.DelayCall(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), Announce);
        }
        private static void EnsureInstalled()
        {
            var npc = World.Mobiles.Values.OfType<ArenaSteward>().FirstOrDefault(m => !m.Deleted);
            if (npc == null) npc = new ArenaSteward();
            npc.MoveToWorld(new Point3D(ArenaService.Lobby.X+3, ArenaService.Lobby.Y, ArenaService.Lobby.Z), DuelArena.ArenaMap);
            var sign = World.Items.Values.OfType<ArenaTrainingSign>().FirstOrDefault(i => !i.Deleted);
            if (sign == null) sign = new ArenaTrainingSign();
            sign.MoveToWorld(new Point3D(ArenaService.Lobby.X+4, ArenaService.Lobby.Y, ArenaService.Lobby.Z), DuelArena.ArenaMap);
        }
        private static void Announce()
        {
            var npc = World.Mobiles.Values.OfType<ArenaSteward>().FirstOrDefault(m => !m.Deleted);
            if (npc == null || npc.Map == null) return;
            if (NetState.Instances.Any(n => n.Mobile is PlayerMobile && n.Mobile.Map == npc.Map && n.Mobile.InRange(npc.Location, 10)))
                npc.Say(Tips[Tip++ % Tips.Length]);
        }
        [Constructable]
        public ArenaSteward()
        {
            Name = "Rowan"; Title = "the arena steward"; Body = 0x190; Hue = 0x83EA;
            Blessed = true; Frozen = true; Direction = Direction.South;
            AddItem(new Robe { Hue = 1153 }); AddItem(new Boots());
        }
        public ArenaSteward(Serial serial) : base(serial) { }
        public override void Serialize(GenericWriter writer) { base.Serialize(writer); writer.Write(0); }
        public override void Deserialize(GenericReader reader) { base.Deserialize(reader); reader.ReadInt(); Blessed = true; Frozen = true; }
        public override void OnDoubleClick(Mobile from)
        {
            if (from.Map != Map || !from.InRange(Location, 4)) { from.SendMessage("Come closer to the arena steward."); return; }
            var p = from as PlayerMobile;
            if (p == null) return;
            ArenaService.OpenPreparation(p);
        }
    }
    public class ArenaTrainingSign : Item
    {
        [Constructable]
        public ArenaTrainingSign() : base(0xBD2) { Name = "Arena training guide - double-click to read"; Movable = false; }
        public ArenaTrainingSign(Serial serial) : base(serial) { }
        public override void Serialize(GenericWriter writer) { base.Serialize(writer); writer.Write(0); }
        public override void Deserialize(GenericReader reader) { base.Deserialize(reader); reader.ReadInt(); }
        public override void OnDoubleClick(Mobile from)
        {
            if (from.Map != Map || !from.InRange(Location, 4)) return;
            var p=from as PlayerMobile; if(p!=null)ArenaTrainingGuideGump.Open(p);
        }
    }
    public class ArenaTrainingGuideGump : Gump
    {
        public static void Open(PlayerMobile p)
        {
            ArenaMatchmaking.ClosePanels(p);p.SendGump(new ArenaTrainingGuideGump());
        }
        public ArenaTrainingGuideGump() : base(60, 40)
        {
            AddPage(0); AddBackground(0, 0, 620, 430, 9200);
            AddLabel(25, 20, 1153, "ARENA TRAINING GUIDE");
            string[] lines = {
                "1. Rowan supplies all potions, reagents, bandages, armor and clothes.",
                "2. Use skill/stat balls in the lobby BEFORE joining a waiting list.",
                "3. Say [Arena or choose Duel to open the simple duel board.",
                "4. Pick 5x Mage or 7x + EX pot. Both are best of three.",
                "5. Auto match pairs you with another Auto player using the same rules.",
                "6. Target a player, or choose a name from the waiting list to challenge.",
                "7. List me waits for invitations. Nothing starts until you accept.",
                "8. A central stone wall disappears after five seconds: FIGHT!",
                "9. Showdown disables healing after 3 minutes. Round limit: 5 minutes.",
                "10. The blue moongate tours arena sidelines; use a return gate to come back.",
                "Rankings and replays: arena.uotavern.com / [Arena leave cancels waiting."
            };
            for (int i = 0; i < lines.Length; i++) AddLabel(25, 60 + i * 27, 0, lines[i]);
            AddButton(25,380,4005,4007,1,GumpButtonType.Reply,0);AddLabel(60,380,0,"Back to Rowan");
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        { var p=sender.Mobile as PlayerMobile;if(p!=null && info.ButtonID==1)ArenaService.OpenPreparation(p); }
    }
    public class ArenaStewardGump : Gump
    {
        private readonly ArenaSteward Steward;
        public ArenaStewardGump(ArenaSteward steward) : base(60, 60)
        {
            Steward = steward;
            AddPage(0); AddBackground(0, 0, 540, 425, 9200);
            AddLabel(25, 20, 1153, "ROWAN / FREE ARENA SUPPLIES");
            string[] labels = { "Get 7GM skill ball", "Get stat ball", "Refill combat / cosmetic supplies", "Read training guide", "Get 5GM skill ball", "Duel / waiting list" };
            for (int i = 0; i < labels.Length; i++)
            { AddButton(25, 65 + i * 45, 4005, 4007, i + 1, GumpButtonType.Reply, 0); AddLabel(60, 65 + i * 45, 0, labels[i]); }
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        {
            var p = sender.Mobile as PlayerMobile;
            if (p == null || Steward.Deleted || p.Map != Steward.Map || !p.InRange(Steward.Location, 4)) return;
            switch (info.ButtonID)
            {
                case 1: ArenaTraining.GiveBall(p); break;
                case 2: ArenaTraining.GiveStatBall(p); break;
                case 3: ArenaSupplies.Refill(p); break;
                case 4: p.SendGump(new ArenaTrainingGuideGump()); return;
                case 5: ArenaTraining.GiveBall(p, 5); break;
                case 6: ArenaService.Open(p); return;
                case 7: ArenaService.Open(p); return;
                default: return;
            }
            p.SendGump(new ArenaStewardGump(Steward));
        }
    }
}
