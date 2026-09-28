using System;

namespace Server.Engines.Dueling
{
    // Match-owned countdown scenery using the real Wall of Stone graphic.
    // Never survives a save/restart, and expires defensively if a match loses ownership.
    public class DuelStartWall : Item
    {
        private Timer m_Expiry;

        public DuelStartWall(Point3D location, Map map) : base(0x82)
        {
            Movable = false;
            Name = "duel starting wall";
            MoveToWorld(location, map);
            m_Expiry = Timer.DelayCall(TimeSpan.FromSeconds(DuelMatch.CountdownSeconds + 10), Delete);
        }

        public DuelStartWall(Serial serial) : base(serial) { }
        public override bool BlocksFit { get { return true; } }
        public override bool OnMoveOver(Mobile m) { return false; }
        public override void OnAfterDelete()
        {
            if (m_Expiry != null) m_Expiry.Stop();
            base.OnAfterDelete();
        }
        public override void Serialize(GenericWriter writer)
        {
            base.Serialize(writer);
            writer.Write(0);
        }
        public override void Deserialize(GenericReader reader)
        {
            base.Deserialize(reader);
            reader.ReadInt();
            m_Expiry = Timer.DelayCall(TimeSpan.Zero, Delete);
        }
    }
}
