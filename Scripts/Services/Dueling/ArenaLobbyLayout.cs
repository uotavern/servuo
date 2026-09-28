using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Engines.Dueling
{
    public class ArenaLobbyTile : Item
    {
        public ArenaLobbyTile(int graphic):base(graphic){Movable=false;Name="arena lobby";}
        public ArenaLobbyTile(Serial serial):base(serial){}
        public override void Serialize(GenericWriter writer){base.Serialize(writer);writer.Write(0);}
        public override void Deserialize(GenericReader reader){base.Deserialize(reader);reader.ReadInt();}
    }
    public static class ArenaLobbyLayout
    {
        public static void Initialize()
        {
            if(ArenaService.Enabled)Timer.DelayCall(TimeSpan.FromSeconds(1),Build);
        }
        private static void Build()
        {
            var floor=ArenaService.LobbyBounds;
            var owned=World.Items.Values.OfType<ArenaLobbyTile>().Where(i=>!i.Deleted).ToList();
            var tiles=owned.GroupBy(i=>i.Location).ToDictionary(g=>g.Key,g=>g.First());
            var keep=new HashSet<Item>();int created=0;
            for(int x=floor.X-1;x<=floor.End.X;x++)for(int y=floor.Y-1;y<=floor.End.Y;y++)
            {
                bool edgeX=x==floor.X-1 || x==floor.End.X, edgeY=y==floor.Y-1 || y==floor.End.Y;
                // A wide southern entrance; all 50x50 interior tiles remain walkable.
                if(edgeY && y==floor.End.Y && Math.Abs(x-ArenaService.Lobby.X)<=2)continue;
                int graphic=!edgeX && !edgeY ? 0x519 : edgeX && edgeY ? 0x84A : edgeX ? 0x849 : 0x84B;
                var location=new Point3D(x,y,ArenaService.Lobby.Z);ArenaLobbyTile tile;
                if(!tiles.TryGetValue(location,out tile) || tile.ItemID!=graphic)
                {tile=new ArenaLobbyTile(graphic);tile.MoveToWorld(location,DuelArena.ArenaMap);created++;}
                keep.Add(tile);
            }
            foreach(var tile in owned)if(!keep.Contains(tile))tile.Delete();
            Console.WriteLine("[Arena] Lobby ready: 50x50 floor at {0}, {1} new pieces.",ArenaService.LobbyBounds,created);
        }
    }
}
