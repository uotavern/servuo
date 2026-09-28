using System;
using System.Collections.Generic;
using System.Linq;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using Server.Spells;

namespace Server.Engines.Dueling
{
    /// <summary>Native UO venues and small temporary landmark rings; map statics are retained.</summary>
    public class DuelArena
    {
        public static readonly List<DuelArena> All = new List<DuelArena>();

        public static Map ArenaMap { get { return Map.Felucca; } }

        public const int AnnounceRange = 24;

        public int Id { get; private set; }
        public int Z { get; private set; }
        public string Shape { get; private set; }
        public string Name { get; private set; }
        public Point3D GateLocation { get; private set; }
        public bool Temporary { get; private set; }

        /// <summary>Fighting floor bounds; stands, borders and exits are outside.</summary>
        public Rectangle2D Bounds { get; private set; }

        /// <summary>Bounding rectangle of the fighting floor.</summary>
        public Rectangle2D Floor { get; private set; }

        public Point3D MarkA { get; private set; }
        public Point3D MarkB { get; private set; }

        /// <summary>Where fighters are placed when a match ends (lobby strip south of the fence).</summary>
        public Point3D ExitA { get; private set; }
        public Point3D ExitB { get; private set; }

        public Point3D StoneLocation { get; private set; }
        public Point3D Center { get; private set; }

        public DuelRegion Region { get; private set; }
        public DuelMatch Match { get; set; }

        public bool Busy { get { return Match != null && Match.Phase != DuelPhase.Finished; } }

        private DuelArena(int id, string name, Rectangle2D floor, Point3D a, Point3D b,
            Point3D exitA, Point3D exitB, Point3D stone, Point3D gate)
        {
            Id=id; Name=name; Shape=name; Z=a.Z; Bounds=Floor=floor;
            MarkA=a; MarkB=b; ExitA=exitA; ExitB=exitB;
            StoneLocation=stone; GateLocation=gate;
            Center=new Point3D((a.X+b.X)/2,(a.Y+b.Y)/2,a.Z);
        }

        private static DuelArena TemporaryRing(int id, string name, int x, int y, int groundZ)
        {
            // A 13x9 clear floor, one connected stone perimeter, and an outside arrival strip.
            int z=groundZ+1;
            return new DuelArena(id,name,new Rectangle2D(x+1,y+1,13,9),
                new Point3D(x+3,y+5,z),new Point3D(x+11,y+5,z),
                new Point3D(x+5,y+11,groundZ),new Point3D(x+9,y+11,groundZ),
                new Point3D(x+7,y+11,groundZ),new Point3D(x+10,y+11,groundZ)) {Temporary=true};
        }

        public static void Setup()
        {
            if(All.Count>0)return;
            // Existing Felucca PvP venues, also described in ArenaSystem/Definitions.cs.
            All.Add(new DuelArena(1, "Lost Lands Coliseum", new Rectangle2D(6070,3713,27,16),
                new Point3D(6076,3721,20), new Point3D(6090,3721,20),
                new Point3D(6100,3720,25), new Point3D(6100,3721,25),
                new Point3D(6102,3721,25), new Point3D(6101,3722,25)));
            All.Add(new DuelArena(2, "Ocllo Arena", new Rectangle2D(3749,2757,25,16),
                new Point3D(3754,2765,5), new Point3D(3768,2765,5),
                new Point3D(3781,2764,5), new Point3D(3781,2768,5),
                new Point3D(3782,2766,5), new Point3D(3782,2768,5)));
            // Jhelom's sunken fighting pit; upper east walkway is outside the fight region.
            All.Add(new DuelArena(3, "Jhelom Fighting Pit", new Rectangle2D(1385,3729,30,28),
                new Point3D(1392,3743,-21), new Point3D(1406,3743,-21),
                new Point3D(1417,3741,0), new Point3D(1417,3744,0),
                new Point3D(1418,3742,0), new Point3D(1418,3744,0)));
            All.Add(TemporaryRing(4,"Britain Fields",1221,1712,0));
            All.Add(TemporaryRing(5,"Buccaneers Den",2671,2171,0));
            All.Add(TemporaryRing(6,"Yew Abbey",625,857,0));
            All.Add(TemporaryRing(7,"Trinsic West Gate",1797,2775,0));
            All.Add(TemporaryRing(8,"Moonglow Gate",4444,1147,0));
            All.Add(TemporaryRing(9,"Vesper Cemetery",2784,879,0));
            All.Add(TemporaryRing(10,"Cove Gate",2284,1204,0));
            foreach(var arena in All)
            {
                arena.Region=new DuelRegion(arena);arena.Region.Register();
            }
        }

        public static DuelArena Get(int id)
        {
            return All.FirstOrDefault(a => a.Id == id);
        }

        public static DuelArena FindFree()
        {
            var free = All.Where(a => !a.Busy).ToArray();
            return free.Length == 0 ? null : free[Utility.Random(free.Length)];
        }

        /// <summary>The arena whose bounds contain the mobile's location, or null.</summary>
        public static DuelArena Find(Mobile m)
        {
            if (m == null || m.Map != ArenaMap)
                return null;

            return All.FirstOrDefault(a => a.Bounds.Contains(m.Location));
        }

        public bool Contains(Mobile m)
        {
            return m != null && m.Map == ArenaMap && Bounds.Contains(m.Location);
        }

        public Point3D MarkOf(DuelMatch match, Mobile m)
        {
            return m == match.A ? MarkA : MarkB;
        }

        public Point3D ExitOf(DuelMatch match, Mobile m)
        {
            return m == match.A ? ExitA : ExitB;
        }

        #region Construction

        public string CheckLayout()
        {
            var points=new[] {MarkA,MarkB,Center,ExitA,ExitB,GateLocation};
            var failures=new List<string>();var labels=new[] {"start A","start B","center","exit A","exit B","gate"};
            for(int i=0;i<points.Length;i++)
            {
                var p=points[i];
                if(!ArenaMap.CanFit(p.X,p.Y,p.Z,16,false,false,true))failures.Add(labels[i]);
            }
            if(Temporary)
                for(int x=Floor.X;x<Floor.End.X;x++)
                    for(int y=Floor.Y;y<Floor.End.Y;y++)
                        if(!ArenaMap.CanFit(x,y,Z,16,false,false,true))failures.Add("floor "+x+","+y);
            bool los=ArenaMap.LineOfSight(new Point3D(MarkA.X,MarkA.Y,MarkA.Z+14),new Point3D(MarkB.X,MarkB.Y,MarkB.Z+14));
            return Name+": surfaces="+(failures.Count==0 ? "PASS" : String.Join(",",failures))+", start LOS="+los;
        }

        private struct LayoutPart
        {
            public int Art;
            public Point3D Location;
            public LayoutPart(int art,int x,int y,int z){Art=art;Location=new Point3D(x,y,z);}
        }

        private IEnumerable<LayoutPart> Layout()
        {
            if(!Temporary)yield break;
            for(int x=Floor.X;x<Floor.End.X;x++)
                for(int y=Floor.Y;y<Floor.End.Y;y++)yield return new LayoutPart(0x519,x,y,Z);
            int x0=Floor.X-1,y0=Floor.Y-1,x1=Floor.End.X,y1=Floor.End.Y;
            // Same corner/edge topology as HouseFoundation's stone foundation.
            // Only NW has a post; SE has the joined corner. No protruding extra posts.
            yield return new LayoutPart(0x66,x0,y0,Z);
            yield return new LayoutPart(0x65,x1,y1,Z);
            for(int x=x0+1;x<=x1;x++)
            {
                yield return new LayoutPart(0x63,x,y0,Z);
                if(x<x1)yield return new LayoutPart(0x63,x,y1,Z);
            }
            for(int y=y0+1;y<=y1;y++)
            {
                yield return new LayoutPart(0x64,x0,y,Z);
                if(y<y1)yield return new LayoutPart(0x64,x1,y,Z);
            }
        }

        public bool IsBuilt()
        {
            var stones=World.Items.Values.OfType<DuelStone>().Where(i=>!i.Deleted && i.ArenaId==Id).ToList();
            if(stones.Count!=1 || stones[0].Map!=ArenaMap || stones[0].Location!=StoneLocation)return false;
            var tiles=World.Items.Values.OfType<DuelArenaTile>().Where(i=>!i.Deleted && i.ArenaId==Id).ToList();
            var expected=Layout().ToList();
            return tiles.Count==expected.Count && expected.All(p=>tiles.Count(i=>i.ItemID==p.Art
                && i.Map==ArenaMap && i.Location==p.Location && !i.Movable)==1);
        }

        public static int EnsureAllBuilt()
        {
            // Retire only objects owned by generated rings. Never touch map statics.
            foreach(var item in World.Items.Values.Where(i=>i is DuelArenaFence ||
                (i is DuelArenaTile && Get(((DuelArenaTile)i).ArenaId)==null) ||
                (i is DuelStone && (Get(((DuelStone)i).ArenaId)==null ||
                 i.Location!=Get(((DuelStone)i).ArenaId).StoneLocation || i.Map!=ArenaMap))).ToList())item.Delete();
            int built=0;
            foreach(var arena in All)if(!arena.IsBuilt())
            {
                arena.Build();built++;
                Console.WriteLine("[Duel] Venue {0}: {1} ready.",arena.Id,arena.Name);
            }
            return built;
        }

        public void Clear()
        {
            foreach(Item item in World.Items.Values.Where(i =>
                (i is DuelArenaFence && ((DuelArenaFence)i).ArenaId==Id) ||
                (i is DuelArenaTile && ((DuelArenaTile)i).ArenaId==Id) ||
                (i is DuelStone && ((DuelStone)i).ArenaId==Id)).ToList())item.Delete();
        }

        public int Build()
        {
            Clear();int count=1;
            foreach(var p in Layout())
            {
                new DuelArenaTile(p.Art,Id).MoveToWorld(p.Location,ArenaMap);count++;
            }
            var stone=new DuelStone(Id);stone.Name=Name+" / duel board";
            stone.MoveToWorld(StoneLocation,ArenaMap);
            return count;
        }

        #endregion

        /// <summary>Moves non-staff spectators to the safe outside exit, away from the solid stone.</summary>
        public void EvictOthers(Mobile keepA, Mobile keepB)
        {
            if (Region == null)
                return;

            foreach (Mobile m in Region.GetPlayers())
            {
                if (m == keepA || m == keepB || m.IsStaff())
                    continue;

                m.MoveToWorld(ExitB, ArenaMap);
                m.SendMessage(DuelSystem.MessageHue, "[Duel] You have been moved out of the arena.");
            }
        }

        public string Describe()
        {
            return String.Format("Arena {0} ({1}): floor x{2}-{3} y{4}-{5} z{6}, marks {7} / {8}, exits {9} / {10}, stone {11}",
                Id, Shape, Floor.Start.X, Floor.End.X - 1, Floor.Start.Y, Floor.End.Y - 1, Z, MarkA, MarkB, ExitA, ExitB, StoneLocation);
        }
    }

    public class DuelRegion : BaseRegion
    {
        public const int RegionPriority = 60; // above TownRegion and native PvP regions (50)

        public DuelArena Arena { get; private set; }

        public DuelRegion(DuelArena arena)
            : base(String.Format("Duel Arena {0}", arena.Id), DuelArena.ArenaMap, RegionPriority, arena.Bounds)
        {
            Arena = arena;
        }

        private DuelMatch Match { get { return Arena.Match; } }

        private bool RoundLive
        {
            get { return Match != null && (Match.Phase == DuelPhase.Countdown || Match.Phase == DuelPhase.Fighting); }
        }

        public override bool AllowHousing(Mobile from, Point3D p)
        {
            return false;
        }

        public override bool OnHeal(Mobile m, ref int amount)
        {
            return DuelSystem.AllowHealing(m, false) && base.OnHeal(m, ref amount);
        }

        public override bool OnSkillUse(Mobile m, int skill)
        {
            if ((skill == (int)SkillName.Healing || skill == (int)SkillName.Veterinary || skill == (int)SkillName.SpiritSpeak)
                && !DuelSystem.AllowHealing(m)) return false;
            bool allowed = base.OnSkillUse(m, skill);
            if (allowed) DuelReplay.Action(m, null, "skill_attempt", ((SkillName)skill).ToString());
            return allowed;
        }

        public override bool OnBeginSpellCast(Mobile m, ISpell s)
        {
            if (DuelSystem.IsHealingSpell(s) && !DuelSystem.AllowHealing(m)) return false;
            if (m.IsStaff())
                return base.OnBeginSpellCast(m, s);

            var match = Match;

            if (match != null && match.Rules.Magic && match.IsFighter(m))
            {
                if (match.Phase != DuelPhase.Fighting)
                {
                    m.SendMessage(DuelSystem.MessageHue, "[Duel] Wait for FIGHT! before casting.");
                    return false;
                }

                if (match.Rules.BlocksSpell(s))
                {
                    m.SendMessage(DuelSystem.MessageHue, "[Duel] That spell is not allowed in the arena.");
                    return false;
                }

                bool allowed = base.OnBeginSpellCast(m, s);
                if (allowed) DuelReplay.Action(m, null, "cast", s.GetType().Name);
                return allowed;
            }

            m.SendMessage(DuelSystem.MessageHue, "[Duel] Spellcasting is not allowed in the arena.");
            return false;
        }

        public override bool AllowHarmful(Mobile from, IDamageable target)
        {
            if (from == null || from.IsStaff())
                return true;

            var match = Match;

            if (match == null || match.Phase != DuelPhase.Fighting)
                return false;

            var targetMobile = target as Mobile;

            return targetMobile != null && from != targetMobile && match.IsFighter(from) && match.IsFighter(targetMobile);
        }

        public override bool AllowBeneficial(Mobile from, Mobile target)
        {
            if (from == target || from.IsStaff())
                return true;

            var match = Match;

            if (match != null && match.Phase != DuelPhase.Finished && match.IsFighter(target))
                return false; // nobody may assist a duelist during a match

            return base.AllowBeneficial(from, target);
        }

        public override bool OnMoveInto(Mobile m, Direction d, Point3D newLocation, Point3D oldLocation)
        {
            if (RoundLive && !m.IsStaff() && !Match.IsFighter(m))
            {
                m.SendMessage(DuelSystem.MessageHue, "[Duel] A duel is in progress; you cannot enter the arena.");
                return false;
            }

            return base.OnMoveInto(m, d, newLocation, oldLocation);
        }

        public override bool OnDoubleClick(Mobile m, object o)
        {
            if (o is Corpse && ((Corpse)o).Owner != m && !m.IsStaff())
            {
                m.SendMessage(DuelSystem.MessageHue, "[Duel] You cannot loot corpses in the arena.");
                return false;
            }

            if (o is BasePotion && !DuelSystem.AllowPotion(m, (BasePotion)o))
                return false;

            if (o is Bandage && !DuelSystem.AllowBandage(m, m))
                return false;

            return base.OnDoubleClick(m, o);
        }

        public override void OnDidHarmful(Mobile harmer, IDamageable harmed)
        {
            base.OnDidHarmful(harmer, harmed);

            var match = Match;

            if (match != null)
                match.NoteHarmful(harmer);
        }

        public override void OnDeath(Mobile m)
        {
            base.OnDeath(m);

            var match = Match;

            if (match != null)
                match.HandleDeath(m);
        }

        public override bool CheckTravel(Mobile traveller, Point3D p, TravelCheckType type)
        {
            // Nobody may recall/gate INTO the arena while a round is live; leaving is always allowed.
            if (RoundLive && !traveller.IsStaff() && !Match.IsFighter(traveller) && (type == TravelCheckType.RecallTo || type == TravelCheckType.GateTo))
                return false;

            return base.CheckTravel(traveller, p, type);
        }
    }

    public class DuelArenaTile : Item
    {
        public int ArenaId { get; private set; }
        public DuelArenaTile(int art,int arenaId):base(art)
        {ArenaId=arenaId;Movable=false;Name=art==0x519 ? "arena paving" : "arena stone border";}
        public DuelArenaTile(Serial serial):base(serial){}
        public override void Serialize(GenericWriter writer){base.Serialize(writer);writer.Write(0);writer.Write(ArenaId);}
        public override void Deserialize(GenericReader reader){base.Deserialize(reader);reader.ReadInt();ArenaId=reader.ReadInt();}
    }

    public class DuelArenaFence : Item
    {
        [CommandProperty(AccessLevel.GameMaster)]
        public int ArenaId { get; set; }

        [Constructable]
        public DuelArenaFence(int itemId, int arenaId)
            : base(itemId)
        {
            ArenaId = arenaId;
            Movable = false;
            Name = "arena fence";
        }

        public DuelArenaFence(Serial serial)
            : base(serial)
        {
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(1); // version
            writer.Write(ArenaId);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();
            ArenaId = version >= 1 ? reader.ReadInt() : 1;
        }
    }

    public class DuelStone : Item
    {
        [CommandProperty(AccessLevel.GameMaster)]
        public int ArenaId { get; set; }

        [Constructable]
        public DuelStone(int arenaId)
            : base(0xEDD)
        {
            ArenaId = arenaId;
            Movable = false;
            Hue = 0x4E9;
            Name = String.Format("duel stone (arena {0})", arenaId);
        }

        public DuelStone(Serial serial)
            : base(serial)
        {
        }

        public override void OnDoubleClick(Mobile from)
        {
            if (!from.InRange(GetWorldLocation(), 8))
            {
                from.SendMessage(DuelSystem.MessageHue, "[Duel] You are too far away from the duel stone.");
                return;
            }

            if (ArenaService.Enabled && from is Server.Mobiles.PlayerMobile)
            {
                ArenaService.Open((Server.Mobiles.PlayerMobile)from);
                return;
            }

            DuelCommands.SendUsage(from);

            DuelArena arena = DuelArena.Get(ArenaId);

            if (arena != null)
                from.SendMessage(DuelSystem.MessageHue, DuelCommands.ArenaStatusLine(arena));
        }

        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(1); // version
            writer.Write(ArenaId);
        }

        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            int version = reader.ReadInt();
            ArenaId = version >= 1 ? reader.ReadInt() : 1;

            if (version < 1)
                Name = "duel stone (arena 1)";
        }
    }
}
