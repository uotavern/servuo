using System;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;
using Server.Targeting;

namespace Server.Engines.Dueling
{
    // Local presets inspired by classic duel-pit selection; not a claim of Hybrid parity.
    public class ArenaDuelSetupGump : Gump
    {
        private static readonly string[] Presets = { "mage5", "mage7", "standard7", "dexxer7", "open7" };
        public ArenaDuelSetupGump() : base(40, 40)
        {
            AddPage(0); AddBackground(0, 0, 760, 630, 9200);
            AddLabel(25, 20, 1153, "DUEL MODES / challenge another player or participant agent");
            AddLabel(25, 48, 0, "Preserves your build. Opponent must accept. Results appear in duel history.");
            string[] labels = {
                "5x Mage: five mage skills; no weapons, armor, bandages, potions or paralyze.",
                "7x Mage: classic skills; no weapons, armor, bandages, potions or paralyze.",
                "7x Standard: weapons, armor, Magery and bandages; no potions.",
                "7x Dexxer: weapons, armor and bandages; no spells or potions.",
                "7x Open spar: weapons, armor, Magery, bandages and potions."
            };
            for (int i = 0; i < labels.Length; i++)
            { AddButton(25, 88 + i * 42, 4005, 4007, i + 1, GumpButtonType.Reply, 0); AddLabel(60, 88 + i * 42, 0, labels[i]); }
            AddLabel(25, 310, 0, "All presets: classic skills, each <= 100; STR/DEX/INT <= 100, total <= 225.");
            AddLabel(25, 335, 0, "No fields, summons, travel or resurrection. Prepare with Rowan's 5/6/7GM balls.");
            AddLabel(25, 375, 1153, "CUSTOM: choose a cap and restrictions, then select your opponent.");
            string[] caps = { "5x", "6x", "7x" };
            AddGroup(1);
            for (int i = 0; i < 3; i++) { AddRadio(25 + i * 130, 410, 208, 209, i == 2, 100 + i); AddLabel(55 + i * 130, 410, 0, caps[i]); }
            string[] options = { "Magery", "No bandages", "No armor", "No potions", "No paralyze", "Fists only" };
            for (int i = 0; i < options.Length; i++)
            { int x = 25 + (i % 3) * 230, y = 450 + (i / 3) * 35; AddCheck(x, y, 210, 211, i == 0 || i == 3, 200 + i); AddLabel(x + 30, y, 0, options[i]); }
            AddButton(25, 535, 4005, 4007, 10, GumpButtonType.Reply, 0); AddLabel(60, 535, 0, "Challenge with custom rules");
            AddLabel(25, 575, 0, "Best of 3; free arena assigned automatically. [Challenge supports arena:N / rounds.");
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        {
            var p = sender.Mobile as PlayerMobile;
            if (p == null || !ArenaService.Enabled || info.ButtonID == 0) return;
            string text;
            if (info.ButtonID >= 1 && info.ButtonID <= Presets.Length) text = Presets[info.ButtonID - 1];
            else if (info.ButtonID == 10)
            {
                text = (info.IsSwitched(100) ? "5x" : info.IsSwitched(101) ? "6x" : "7x") + "-classic";
                string[] tokens = { "magic", "nobandage", "noarmor", "nopotions", "noparalyze", "fists" };
                for (int i = 0; i < tokens.Length; i++) if (info.IsSwitched(200 + i)) text += "-" + tokens[i];
            }
            else return;
            DuelRules rules; string error;
            if (!DuelRules.TryParse(text, out rules, out error)) return;
            error = DuelSystem.CheckAvailable(p, rules, null);
            if (error != null) { p.SendMessage(0x35, error); return; }
            p.SendMessage(0x35, "[Duel] Select your opponent. Rules: " + rules);
            p.Target = new OpponentTarget(rules);
        }
        private class OpponentTarget : Target
        {
            private readonly DuelRules Rules;
            public OpponentTarget(DuelRules rules) : base(12, false, TargetFlags.None) { Rules = rules; }
            protected override void OnTarget(Mobile from, object target)
            {
                var a = from as PlayerMobile; var b = target as PlayerMobile;
                if (a == null || b == null) { from.SendMessage("Select another player or participant agent."); return; }
                DuelSystem.Challenge(a, b, 3, Rules, null);
            }
        }
    }
    public class ArenaDuelInviteGump : Gump
    {
        private readonly DuelChallenge Challenge;
        public ArenaDuelInviteGump(DuelChallenge challenge) : base(60, 60)
        {
            Challenge = challenge;
            AddPage(0); AddBackground(0, 0, 680, 300, 9200);
            AddLabel(25, 20, 1153, "DUEL INVITATION");
            AddLabel(25, 55, 0, "From: " + challenge.Challenger.Name + " / best of " + challenge.Rounds);
            AddHtml(25, 95, 620, 90, "Rules: " + challenge.Rules.ToString(), true, false);
            AddLabel(25, 195, 0, "Your current build is used. Check the rules before accepting.");
            AddButton(25, 245, 4005, 4007, 1, GumpButtonType.Reply, 0); AddLabel(60, 245, 0, "Accept");
            AddButton(300, 245, 4005, 4007, 2, GumpButtonType.Reply, 0); AddLabel(335, 245, 0, "Decline");
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        {
            var p = sender.Mobile as PlayerMobile;
            if (p != null && (info.ButtonID == 1 || info.ButtonID == 2)) DuelSystem.ReplyToChallenge(p, Challenge, info.ButtonID == 1);
        }
    }
}
