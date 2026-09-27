using System;
using System.Linq;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.Dueling
{
    public static class ArenaTraining
    {
        public static bool CanEdit(PlayerMobile p)
        {
            bool ok = ArenaService.Enabled && p != null && !p.Deleted && p.Alive
                && p.AccessLevel == AccessLevel.Player && ArenaService.InLobby(p)
                && DuelSystem.FindMatchOf(p) == null && !ArenaService.IsQueued(p)
                && p.Combatant == null;
            if (!ok && p != null)
                p.SendMessage(0x35, "[Arena] Customize while alive, out of combat and out of the queue in the lobby.");
            return ok;
        }
        public static void GiveBall(PlayerMobile p, int count = 6)
        {
            if (!CanEdit(p) || p.Backpack == null || count < 5 || count > 7) return;
            if (p.Backpack.FindItemsByType(typeof(ArenaSkillBall), true).Length > 0)
            { p.SendMessage(0x35, "[Arena] You already have a skill ball in your backpack."); return; }
            var ball = new ArenaSkillBall(count);
            if (!p.PlaceInBackpack(ball)) { ball.Delete(); return; }
            p.SendMessage(0x35, "[Arena] Double-click the skill ball. Choose the indicated number of skills; all other skills become zero.");
        }
        public static void GiveStatBall(PlayerMobile p)
        {
            if (!CanEdit(p) || p.Backpack == null) return;
            if (p.Backpack.FindItemsByType(typeof(ArenaStatBall), true).Length > 0)
            { p.SendMessage(0x35, "[Arena] You already have a stat ball in your backpack."); return; }
            var ball = new ArenaStatBall();
            if (!p.PlaceInBackpack(ball)) { ball.Delete(); return; }
            p.SendMessage(0x35, "[Arena] Double-click the stat ball to choose STR, DEX and INT.");
        }

    }
    public class ArenaSkillBall : Item
    {
        public int SkillCount { get; private set; }
        [Constructable]
        public ArenaSkillBall() : this(6) { }
        public ArenaSkillBall(int count) : base(0xE2D)
        { SkillCount = count >= 5 && count <= 7 ? count : 6; Name = "arena " + SkillCount + "GM skill ball"; Hue = 1153; Weight = 1; LootType = LootType.Blessed; }
        public ArenaSkillBall(Serial serial) : base(serial) { }
        public override void Serialize(GenericWriter writer) { base.Serialize(writer); writer.Write(1); writer.Write(SkillCount); }
        public override void Deserialize(GenericReader reader) { base.Deserialize(reader); int v = reader.ReadInt(); SkillCount = v >= 1 ? reader.ReadInt() : 6; }
        public override void OnDoubleClick(Mobile from)
        {
            var p = from as PlayerMobile;
            if (!ArenaTraining.CanEdit(p)) return;
            if (!IsChildOf(p.Backpack)) { p.SendMessage("Put the skill ball in your backpack first."); return; }
            p.CloseGump(typeof(ArenaSkillsGump));
            p.SendGump(new ArenaSkillsGump(this, p));
        }
    }
    public class ArenaStatBall : Item
    {
        [Constructable]
        public ArenaStatBall() : base(0xE2D)
        { Name = "arena stat ball"; Hue = 53; Weight = 1; LootType = LootType.Blessed; }
        public ArenaStatBall(Serial serial) : base(serial) { }
        public override void Serialize(GenericWriter writer) { base.Serialize(writer); writer.Write(0); }
        public override void Deserialize(GenericReader reader) { base.Deserialize(reader); reader.ReadInt(); }
        public override void OnDoubleClick(Mobile from)
        {
            var p = from as PlayerMobile;
            if (!ArenaTraining.CanEdit(p)) return;
            if (!IsChildOf(p.Backpack)) { p.SendMessage("Put the stat ball in your backpack first."); return; }
            p.CloseGump(typeof(ArenaStatsGump));
            p.SendGump(new ArenaStatsGump(this, p));
        }
    }
    public class ArenaSkillsGump : Gump
    {
        private readonly ArenaSkillBall Ball;
        public ArenaSkillsGump(ArenaSkillBall ball, PlayerMobile p) : base(30, 30)
        {
            Ball = ball;
            AddPage(0); AddBackground(0, 0, 760, 700, 9200);
            AddLabel(25, 20, 1153, ball.SkillCount + "GM SKILL BALL - choose exactly " + ball.SkillCount + " skills");
            AddLabel(25, 45, 0, "Apply: selected skills = 100.0, all others = 0.0. This consumes the ball.");
            AddLabel(25, 70, 0, "Practice keeps your skills. Ranked queues replace them with a 5x template.");
            for (int i = 0; i < p.Skills.Length; i++)
            {
                int x = 25 + (i / 20) * 245, y = 105 + (i % 20) * 26;
                AddCheck(x, y, 210, 211, p.Skills[i].Base >= 100, i);
                AddLabel(x + 30, y, 0, p.Skills[i].Name);
            }
            AddButton(25, 650, 4005, 4007, 1, GumpButtonType.Reply, 0);
            AddLabel(60, 650, 0, "Apply " + ball.SkillCount + " GM skills");
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        {
            var p = sender.Mobile as PlayerMobile;
            if (info.ButtonID != 1 || !ArenaTraining.CanEdit(p)) return;
            if (Ball.Deleted || !Ball.IsChildOf(p.Backpack)) return;
            int[] skills = info.Switches;
            if (skills == null || skills.Length != Ball.SkillCount || skills.Distinct().Count() != Ball.SkillCount
                || skills.Any(i => i < 0 || i >= p.Skills.Length))
            { p.SendMessage(0x35, "[Arena] Select exactly " + Ball.SkillCount + " different skills. Your ball was not consumed."); p.SendGump(new ArenaSkillsGump(Ball, p)); return; }
            for (int i = 0; i < p.Skills.Length; i++) p.Skills[i].Base = 0;
            foreach (int i in skills) p.Skills[i].Base = 100;
            for (int i = 0; i < p.Skills.Length; i++) p.Skills[i].SetLockNoRelay(SkillLock.Locked);
            Ball.Delete();
            p.SendMessage(0x35, "[Arena] Selected skills set to 100.0; all others reset to zero. Skills are locked. Request another ball whenever needed.");
        }
    }
    public class ArenaStatsGump : Gump
    {
        private readonly ArenaStatBall Ball;
        public ArenaStatsGump(ArenaStatBall ball, PlayerMobile p) : base(60, 60)
        {
            Ball = ball;
            AddPage(0); AddBackground(0, 0, 540, 300, 9200);
            AddLabel(25, 20, 1153, "ARENA STATS");
            AddLabel(25, 50, 0, "Each stat: 10-100. Total: at most 225.");
            string[] labels = { "Strength", "Dexterity", "Intelligence" };
            int[] values = { p.RawStr, p.RawDex, p.RawInt };
            for (int i = 0; i < 3; i++)
            {
                AddLabel(25, 85 + i * 35, 0, labels[i]);
                AddBackground(175, 82 + i * 35, 110, 30, 3000);
                AddTextEntry(180, 85 + i * 35, 100, 25, 0, i, values[i].ToString());
            }
            AddLabel(25, 200, 0, "Practice keeps stats; ranked applies its standard template.");
            AddButton(25, 245, 4005, 4007, 1, GumpButtonType.Reply, 0);
            AddLabel(60, 245, 0, "Apply stats (consumes this ball)");
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        {
            var p = sender.Mobile as PlayerMobile;
            if (info.ButtonID != 1 || !ArenaTraining.CanEdit(p)) return;
            if (Ball.Deleted || !Ball.IsChildOf(p.Backpack)) return;
            int[] values = new int[3];
            for (int i = 0; i < 3; i++)
            {
                var entry = info.GetTextEntry(i);
                if (entry == null || !Int32.TryParse(entry.Text, out values[i]) || values[i] < 10 || values[i] > 100)
                { p.SendMessage(0x35, "[Arena] Enter whole numbers from 10 to 100."); p.SendGump(new ArenaStatsGump(Ball, p)); return; }
            }
            if (values.Sum() > 225)
            { p.SendMessage(0x35, "[Arena] Stat total cannot exceed 225."); p.SendGump(new ArenaStatsGump(Ball, p)); return; }
            // Lower first so an intermediate total never exceeds the character cap.
            p.RawStr = p.RawDex = p.RawInt = 10;
            p.RawStr = values[0]; p.RawDex = values[1]; p.RawInt = values[2];
            p.StrLock = p.DexLock = p.IntLock = StatLockType.Locked;
            DuelMatch.FullHeal(p);
            Ball.Delete();
            p.SendMessage(0x35, String.Format("[Arena] Stats set: STR {0}, DEX {1}, INT {2}. Stats are locked.", values[0], values[1], values[2]));
        }
    }
}
