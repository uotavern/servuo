#region References
using System;
using System.Linq;
using System.Collections;

using Server.Network;
using Server.Spells;
using Server.Targeting;
#endregion

namespace Server.Items
{
	public abstract class BaseExplosionPotion : BasePotion
	{
		private const int ExplosionRange = 2; // How long is the blast radius?
		private Timer m_Timer;
		private Point3D m_ThrownTo; // where a potion in flight will land (it sits on the internal map for a second)
		private Map m_ThrownMap;

		public BaseExplosionPotion(PotionEffect effect)
			: base(0xF0D, effect)
		{ }

		public BaseExplosionPotion(Serial serial)
			: base(serial)
		{ }

		public abstract int MinDamage { get; }
		public abstract int MaxDamage { get; }
		public override bool RequireFreeHand { get { return false; } }

		public override void Serialize(GenericWriter writer)
		{
			base.Serialize(writer);

			writer.Write(0); // version
		}

		public override void Deserialize(GenericReader reader)
		{
			base.Deserialize(reader);

			int version = reader.ReadInt();
		}

		public virtual object FindParent(Mobile from)
		{
			Mobile m = HeldBy;

			if (m != null && m.Holding == this)
			{
				return m;
			}

			object obj = RootParent;

			if (obj != null)
			{
				return obj;
			}

			if (Map == Map.Internal)
			{
				return from;
			}

			return this;
		}

		public override void Drink(Mobile from)
		{
            if (!Server.Engines.Dueling.DuelSystem.AllowPotion(from, this)) return;
			if (Core.AOS && (from.Paralyzed || from.Frozen || (from.Spell != null && from.Spell.IsCasting)))
			{
				from.SendLocalizedMessage(1062725); // You can not use a purple potion while paralyzed.
				return;
			}

			ThrowTarget targ = from.Target as ThrowTarget;
			Stackable = false; // Scavenged explosion potions won't stack with those ones in backpack, and still will explode.

			if (targ != null && targ.Potion == this)
			{
				return;
			}

			from.RevealingAction();
			from.Target = new ThrowTarget(this);

			if (m_Timer == null)
			{
				from.SendLocalizedMessage(500236); // You should throw it now!
                ReplayState(from, "prime");

				if (Core.ML)
				{
					m_Timer = Timer.DelayCall(
						TimeSpan.FromSeconds(1.0),
						TimeSpan.FromSeconds(1.25),
						5,
						new TimerStateCallback(Detonate_OnTick),
						new object[] {from, 3}); // 3.6 seconds explosion delay
				}
				else
				{
					m_Timer = Timer.DelayCall(
						TimeSpan.FromSeconds(0.75),
						TimeSpan.FromSeconds(1.0),
						4,
						new TimerStateCallback(Detonate_OnTick),
						new object[] {from, 3}); // 2.6 seconds explosion delay
				}
			}
		}

        private void ReplayState(Mobile from, string phase, int count = -1)
        {
            if (from == null) return;
            var holder = RootParent as Mobile;
            bool flight = Map == Map.Internal && m_ThrownMap != null;
            Point3D pos = flight ? m_ThrownTo : GetWorldLocation();
            Server.Engines.Dueling.DuelReplay.Event(Server.Engines.Dueling.DuelSystem.FindMatchOf(from), "potion_state",
                "\"actor\":" + from.Serial.Value + ",\"item\":" + Serial.Value + ",\"graphic\":" + ItemID +
                ",\"hue\":" + Hue + ",\"phase\":" + Server.Engines.Dueling.ArenaService.Json(phase) +
                ",\"count\":" + count + ",\"holder\":" + (holder == null ? 0 : holder.Serial.Value) +
                ",\"flight\":" + (flight ? "true" : "false") + ",\"pos\":[" + pos.X + "," + pos.Y + "," + pos.Z +
                "],\"from\":[" + from.X + "," + from.Y + "," + from.Z + "]");
        }

		public void Explode(Mobile from, bool direct, Point3D loc, Map map)
		{
			if (Deleted)
			{
				return;
			}

            if (from != null && !Server.Engines.Dueling.DuelSystem.AllowPotion(from, this, false)) { Consume(); return; }

            ReplayState(from, "explode", 0);
            Server.Engines.Dueling.DuelReplay.Projectile(from, "potion_explode", loc);
            bool damageThrower = false;

            if (from != null)
            {
                if (from.Target is ThrowTarget && ((ThrowTarget)from.Target).Potion == this)
                {
                    Target.Cancel(from);
                }

                if (IsChildOf(from.Backpack) || Parent == from)
                {
                    damageThrower = true;
                }
            }

            Consume();

            if (map == null)
			{
				return;
			}

			Effects.PlaySound(loc, map, 0x307);

			Effects.SendLocationEffect(loc, map, 0x36B0, 9, 10, 0, 0);
			int alchemyBonus = 0;

			if (direct)
			{
				alchemyBonus = (int)(from.Skills.Alchemy.Value / (Core.AOS ? 5 : 10));
			}

			int min = Scale(from, MinDamage);
			int max = Scale(from, MaxDamage);

            var list = SpellHelper.AcquireIndirectTargets(from, loc, map, ExplosionRange, false).OfType<Mobile>().ToList();

            if (from != null && damageThrower && !list.Contains(from))
            {
                list.Add(from);
            }

            foreach (var m in list)
            {
                if (from != null)
                {
                    from.DoHarmful(m);
                }

                int damage = Utility.RandomMinMax(min, max);

                damage += alchemyBonus;

                if (!Core.AOS && damage > 40)
                {
                    damage = 40;
                }
                else if (Core.AOS && list.Count > 2)
                {
                    damage /= list.Count - 1;
                }

                AOS.Damage(m, from, damage, 0, 100, 0, 0, 0, Server.DamageType.SpellAOE);
            }

            list.Clear();
		}

		private void Detonate_OnTick(object state)
		{
			if (Deleted)
			{
				return;
			}

			var states = (object[])state;
			Mobile from = (Mobile)states[0];
			int timer = (int)states[1];

			object parent = FindParent(from);
            ReplayState(from, "tick", timer);

			if (Map == Map.Internal && m_ThrownMap != null)
			{
				// Still in flight: the countdown can't be shown, and the blast happens at the landing spot.
				if (timer == 0)
				{
					Explode(from, true, m_ThrownTo, m_ThrownMap);
					m_Timer = null;
				}
				else
				{
					states[1] = timer - 1;
				}

				return;
			}

			if (timer == 0)
			{
				Point3D loc;
				Map map;

				if (parent is Item)
				{
					Item item = (Item)parent;

					loc = item.GetWorldLocation();
					map = item.Map;
				}
				else if (parent is Mobile)
				{
					Mobile m = (Mobile)parent;

					loc = m.Location;
					map = m.Map;
				}
				else
				{
					return;
				}

				Explode(from, true, loc, map);
				m_Timer = null;
			}
			else
			{
				if (parent is Item)
				{
					((Item)parent).PublicOverheadMessage(MessageType.Regular, 0x22, false, timer.ToString());
				}
				else if (parent is Mobile)
				{
					((Mobile)parent).PublicOverheadMessage(MessageType.Regular, 0x22, false, timer.ToString());
				}

				states[1] = timer - 1;
			}
		}

		private void Reposition_OnTick(object state)
		{
			if (Deleted)
			{
				return;
			}

			var states = (object[])state;
			Mobile from = (Mobile)states[0];
			IPoint3D p = (IPoint3D)states[1];
			Map map = (Map)states[2];

			Point3D loc = new Point3D(p);
			m_ThrownMap = null;
		    MoveToWorld(loc, map);
            ReplayState(from, "land");
		}

		private class ThrowTarget : Target
		{
			private readonly BaseExplosionPotion m_Potion;

			public ThrowTarget(BaseExplosionPotion potion)
				: base(12, true, TargetFlags.None)
			{
				m_Potion = potion;
			}

			public BaseExplosionPotion Potion { get { return m_Potion; } }

			protected override void OnTarget(Mobile from, object targeted)
			{
                if (!Server.Engines.Dueling.DuelSystem.AllowPotion(from, m_Potion)) return;
				if (m_Potion.Deleted || m_Potion.Map == Map.Internal)
				{
					return;
				}

				IPoint3D p = targeted as IPoint3D;

				if (p == null)
				{
					return;
				}

				Map map = from.Map;

				if (map == null)
				{
					return;
				}

				SpellHelper.GetSurfaceTop(ref p);

				from.RevealingAction();

				IEntity to;

				to = new Entity(Serial.Zero, new Point3D(p), map);

                if (p is Mobile)
                {
                    to = (Mobile)p;
                }

                Server.Engines.Dueling.DuelReplay.Projectile(from, "potion_throw", p);
				Effects.SendMovingEffect(from, to, m_Potion.ItemID, 7, 0, false, false, m_Potion.Hue, 0);

				if (m_Potion.Amount > 1)
				{
					Mobile.LiftItemDupe(m_Potion, 1);
				}

				m_Potion.m_ThrownTo = new Point3D(p);
				m_Potion.m_ThrownMap = map;
				m_Potion.Internalize();
                m_Potion.ReplayState(from, "throw");
				Timer.DelayCall(
					TimeSpan.FromSeconds(1.0), new TimerStateCallback(m_Potion.Reposition_OnTick), new object[] {from, p, map});
			}
		}
	}
}
