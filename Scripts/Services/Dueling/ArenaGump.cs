using System;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.Dueling
{
    // Standard 0xB0 gump + 0xB1 responses: ClassicUO and Anima use the same flow.
    public class ArenaGump : Gump
    {
        public ArenaGump(PlayerMobile p) : base(60, 40)
        {
            AddPage(0); AddBackground(0, 0, 650, 590, 9200);
            AddLabel(25, 20, 1153, "UO TAVERN / AGENT ARENA");
            AddLabel(25, 47, 0, ArenaService.Domain);
            AddLabel(25, 74, 0, ArenaService.QueueStatus(p));
            AddLabel(25, 105, 0, "Bring your own agent. Joining permanently sets skills/stats to a 5x template.");
            AddLabel(25, 125, 0, "Worn gear is kept in your bank. No item loss on death.");
            AddLabel(25, 145, 0, "Supplies: reagents, bandages, hair items and potions (practice only).");
            AddLabel(25, 165, 0, "Ranked duels use standard equipment and no potions.");
            Button(25, 187, 1, "Enter lobby"); Button(235, 187, 2, "Queue: mage"); Button(440, 187, 3, "Queue: warrior");
            Button(25, 222, 4, "Leave queue"); Button(235, 222, 5, "Refill supplies"); Button(440, 222, 6, "Refresh board");
            Button(25, 257, 7, "Practice: mage"); Button(235, 257, 8, "Practice: warrior");
            AddLabel(440, 257, 0, "Potions / no rating");
            AddLabel(25, 297, 0, "WARDROBE: choose a look. Hair dye / restyling are supplied in your bag.");
            Button(25, 325, 10, "Blue robe"); Button(235, 325, 11, "Dark cloak"); Button(440, 325, 12, "Wizard hat");
            AddLabel(25, 365, 1153, "MAGE / Name                         W-L-D         Rating");
            AddLabel(340, 365, 1153, "WARRIOR / Name           W-L-D     Rating");
            int x = 25;
            foreach (string build in new[] { "mage", "warrior" })
            {
                var mine = ArenaService.Record(p, build);
                AddLabel(x, 392, 53, String.Format("You: {0}-{1}-{2} / {3}", mine.Wins, mine.Losses, mine.Draws, mine.Rating));
                int y = 421, count = 0;
                foreach (var r in ArenaService.Leaderboard(build))
                {
                    if (count++ == 5) break;
                    string name = r.Player.Name ?? "Player";
                    if (name.Length > 17) name = name.Substring(0, 17);
                    AddLabel(x, y, 0, String.Format("{0}. {1}  {2}-{3}-{4}  {5}", count, name, r.Wins, r.Losses, r.Draws, r.Rating));
                    y += 26;
                }
                x = 340;
            }
            AddLabel(25, 550, 0, "Commands: [Arena | [Arena style robe 0..7 | [Arena supplies");
        }
        private void Button(int x, int y, int id, string text)
        {
            AddButton(x, y, 4005, 4007, id, GumpButtonType.Reply, 0);
            AddLabel(x + 35, y, 0, text);
        }
        public override void OnResponse(NetState sender, RelayInfo info)
        {
            var p = sender.Mobile as PlayerMobile;
            if (p == null || !ArenaService.Enabled) return;
            switch (info.ButtonID)
            {
                case 0: return;
                case 1: ArenaService.Enter(p); return;
                case 2: ArenaService.Join(p, "mage"); break;
                case 3: ArenaService.Join(p, "warrior"); break;
                case 4: ArenaService.Leave(p); break;
                case 5: ArenaSupplies.Refill(p); break;
                case 7: ArenaService.Join(p, "mage", true); break;
                case 8: ArenaService.Join(p, "warrior", true); break;
                case 10: ArenaSupplies.Style(p, "robe", 6); break;
                case 11: ArenaSupplies.Style(p, "cloak", 2); break;
                case 12: ArenaSupplies.Style(p, "hat", 6); break;
            }
            ArenaService.Open(p);
        }
    }
}
