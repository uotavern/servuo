using System;
using System.Linq;
using Server.Items;
using Server.Mobiles;

namespace Server.Engines.Dueling
{
    public static class ArenaSupplies
    {
        private static bool CanUse(PlayerMobile p)
        {
            return p != null && p.Alive && p.AccessLevel == AccessLevel.Player && ArenaService.InLobby(p) && DuelSystem.FindMatchOf(p) == null;
        }
        // Tops up a bounded amount instead of handing out a new full bag on every click.
        private static void Stock(PlayerMobile p, Type type, int amount, Func<Item> create)
        {
            if (p.Backpack == null) return;
            int have = p.Backpack.FindItemsByType(type, true).Sum(i => i.Amount);
            for (int i = have; i < amount;)
            {
                Item item = create();
                item.LootType = LootType.Blessed;
                if (item.Stackable) { item.Amount = amount - have; i = amount; } else i++;
                if (!p.PlaceInBackpack(item)) { item.Delete(); break; }
            }
        }
        public static void Refill(PlayerMobile p)
        {
            if (!CanUse(p)) { if (p != null) p.SendMessage(0x35, "[Arena] Supplies are available while alive and idle in the lobby."); return; }
            StockCombat(p);
            StockPotions(p);
            Stock(p, typeof(HairRestylingDeed), 1, () => new HairRestylingDeed());
            Stock(p, typeof(HairDye), 1, () => new HairDye());
            p.SendMessage(0x35, "[Arena] Supplies refilled. Regular potions are allowed; explosion potions require an enabled duel option. Hair items are in your backpack.");
        }
        public static void StockPotions(PlayerMobile p)
        {
            Stock(p, typeof(GreaterHealPotion), 5, () => new GreaterHealPotion());
            Stock(p, typeof(GreaterCurePotion), 5, () => new GreaterCurePotion());
            Stock(p, typeof(TotalRefreshPotion), 5, () => new TotalRefreshPotion());
            Stock(p, typeof(GreaterExplosionPotion), 5, () => new GreaterExplosionPotion());
            Stock(p, typeof(GreaterStrengthPotion), 5, () => new GreaterStrengthPotion());
            Stock(p, typeof(GreaterAgilityPotion), 5, () => new GreaterAgilityPotion());
        }
        public static void StockCombat(PlayerMobile p)
        {
            Stock(p, typeof(BlackPearl), 100, () => new BlackPearl());
            Stock(p, typeof(Bloodmoss), 100, () => new Bloodmoss());
            Stock(p, typeof(Garlic), 100, () => new Garlic());
            Stock(p, typeof(Ginseng), 100, () => new Ginseng());
            Stock(p, typeof(MandrakeRoot), 100, () => new MandrakeRoot());
            Stock(p, typeof(Nightshade), 100, () => new Nightshade());
            Stock(p, typeof(SulfurousAsh), 100, () => new SulfurousAsh());
            Stock(p, typeof(SpidersSilk), 100, () => new SpidersSilk());
            Stock(p, typeof(Bandage), 100, () => new Bandage());
            var books = p.Backpack == null ? new Spellbook[0] : p.Backpack.FindItemsByType(typeof(Spellbook), true).OfType<Spellbook>().ToArray();
            if (books.Length == 0) Stock(p, typeof(Spellbook), 1, () => new Spellbook(UInt64.MaxValue));
            else books[0].Content = UInt64.MaxValue;
        }
        public static void Prepare(PlayerMobile p, string build, bool preserveTraining = false)
        {
            if (!p.Alive) p.Resurrect();
            if (p.Mount != null) p.Mount.Rider = null;
            // Dedicated shard templates: explicit consent is the queue button, and the
            // UI says skills/stats change permanently. Personal gear is kept in the bank.
            int robeHue = 0, cloakHue = -1;
            foreach (Item item in p.Items.ToList())
            {
                if (item.Layer == Layer.Backpack || item.Layer == Layer.Bank || item.Layer == Layer.Hair || item.Layer == Layer.FacialHair || item.Layer == Layer.Mount) continue;
                if (item.Name == "arena equipment")
                {
                    if (item is Robe) robeHue = item.Hue;
                    if (item is Cloak) cloakHue = item.Hue;
                    item.Delete();
                }
                else p.BankBox.DropItem(item);
            }
            if (!preserveTraining)
            {
                for (int i = 0; i < p.Skills.Length; i++) { p.Skills[i].Base = 0; p.Skills[i].SetLockNoRelay(SkillLock.Locked); }
                SkillName[] skills = build == "mage" ?
                    new[] { SkillName.Magery, SkillName.EvalInt, SkillName.Meditation, SkillName.MagicResist, SkillName.Wrestling } :
                    new[] { SkillName.Swords, SkillName.Tactics, SkillName.Anatomy, SkillName.Healing, SkillName.MagicResist };
                foreach (var skill in skills) p.Skills[skill].Base = 100;
                p.RawStr = build == "mage" ? 90 : 100;
                p.RawDex = build == "mage" ? 35 : 100;
                p.RawInt = build == "mage" ? 100 : 25;
                p.StrLock = p.DexLock = p.IntLock = StatLockType.Locked;
            }
            Equip(p, new Robe { Hue = robeHue });
            if (cloakHue >= 0) Equip(p, new Cloak { Hue = cloakHue });
            Equip(p, new Boots());
            if (build == "warrior")
            {
                Equip(p, new Katana()); Equip(p, new LeatherChest()); Equip(p, new LeatherLegs());
                Equip(p, new LeatherArms()); Equip(p, new LeatherGloves()); Equip(p, new LeatherGorget()); Equip(p, new LeatherCap());
            }
            StockCombat(p);
            DuelMatch.FullHeal(p);
        }
        private static void Equip(PlayerMobile p, Item item)
        {
            item.Name = "arena equipment";
            item.LootType = LootType.Blessed;
            if (!p.EquipItem(item)) { item.Delete(); }
        }
        public static void Style(PlayerMobile p, string style, int color)
        {
            if (!CanUse(p)) return;
            int[] palette = { 0, 1153, 1109, 33, 53, 73, 93, 0x47E };
            if (color < 0 || color >= palette.Length) { p.SendMessage(0x35, "[Arena] Color must be 0 through 7."); return; }
            Item item;
            switch (style.ToLowerInvariant())
            {
                case "robe": item = new Robe(); break;
                case "cloak": item = new Cloak(); break;
                case "hat": item = new WizardsHat(); break;
                default: p.SendMessage(0x35, "[Arena] Choose robe, cloak or hat; colors 0 through 7."); return;
            }
            Item old = p.FindItemOnLayer(item.Layer);
            if (old != null)
            {
                if (old.Name == "arena equipment") old.Delete();
                else p.BankBox.DropItem(old);
            }
            item.Hue = palette[color];
            Equip(p, item);
        }
    }
}
