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
            "Prepare outside the queue. Practice keeps your build; ranked uses standard 5x templates.",
            "Read the sign beside me. I also refill reagents, bandages and potions."
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
            npc.MoveToWorld(new Point3D(5183, 332, 15), DuelArena.ArenaMap);
            var sign = World.Items.Values.OfType<ArenaTrainingSign>().FirstOrDefault(i => !i.Deleted);
            if (sign == null) sign = new ArenaTrainingSign();
            sign.MoveToWorld(new Point3D(5184, 332, 15), DuelArena.ArenaMap);
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
            p.CloseGump(typeof(ArenaStewardGump)); p.SendGump(new ArenaStewardGump(this));
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
            from.CloseGump(typeof(ArenaTrainingGuideGump)); from.SendGump(new ArenaTrainingGuideGump());
        }
    }
    public class ArenaTrainingGuideGump : Gump
    {
        public ArenaTrainingGuideGump() : base(60, 40)
        {
            AddPage(0); AddBackground(0, 0, 600, 390, 9200);
            AddLabel(25, 20, 1153, "ARENA TRAINING GUIDE");
            string[] lines = {
                "1. Double-click Rowan beside this sign to receive free training balls.",
                "2. Double-click the BLUE skill ball in your backpack. Select 5 or 7 skills.",
                "   Applying sets those skills to 100.0 and ALL other skills to zero.",
                "3. Double-click the GOLD stat ball. Enter STR, DEX and INT.",
                "   Each: 10-100. Total: at most 225. Apply consumes the ball.",
                "4. Both balls are free again after use. Only one of each per backpack.",
                "5. Leave the queue before editing. Balls cannot be used in a match.",
                "6. Practice preserves your build (7x); ranked replaces it with 5x.",
                "7. Rowan also supplies reagents, bandages, hair items and potions.",
                "Commands: [Arena enter / [Arena skills 5|7 / [Arena stats",
                "Say [Arena duel for 5x/7x Mage, Standard, Dexxer and custom challenges."
            };
            for (int i = 0; i < lines.Length; i++) AddLabel(25, 60 + i * 27, 0, lines[i]);
        }
    }
    public class ArenaStewardGump : Gump
    {
        private readonly ArenaSteward Steward;
        public ArenaStewardGump(ArenaSteward steward) : base(60, 60)
        {
            Steward = steward;
            AddPage(0); AddBackground(0, 0, 540, 425, 9200);
            AddLabel(25, 20, 1153, "ROWAN / FREE ARENA SUPPLIES");
            string[] labels = { "Get 7GM skill ball", "Get stat ball", "Refill combat / cosmetic supplies", "Read training guide", "Get 5GM skill ball", "Choose explosion-potion rules", "Choose duel mode / challenge" };
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
                case 6: p.SendGump(new ArenaDuelSetupGump()); return;
                case 7: p.SendGump(new ArenaDuelSetupGump()); return;
                default: return;
            }
            p.SendGump(new ArenaStewardGump(Steward));
        }
    }
}
