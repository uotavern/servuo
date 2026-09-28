using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Server.Mobiles;

namespace Server.Engines.Dueling
{
    // Separate ladder for the two public templates. Existing public records are never migrated.
    public static class ArenaLadder
    {
        private class Row {public int Serial,Template,Rating=1000,Wins,Losses,Draws;}
        private static readonly Dictionary<string,Row> Rows=new Dictionary<string,Row>();
        private static readonly Dictionary<string,int> Pairs=new Dictionary<string,int>();
        private static string Day=DateTime.UtcNow.ToString("yyyyMMdd");
        private static readonly string PathName=Path.Combine("Saves","ArenaTemplateLadder.bin");
        public static bool Healthy=true;
        public const int PairLimit=3;
        public static void Initialize()
        {
            // AutoSave rotates the entire Saves directory before WorldSave.
            // Recreate our snapshot there even when no match has just finished.
            EventSink.WorldSave += e => {
                if(!Healthy)return;
                try { Save(); }
                catch(Exception ex) { Healthy=false;Console.WriteLine("[Arena] Template ladder world save failed: "+ex.Message); }
            };
            if(!File.Exists(PathName))return;
            try
            {
                using(var r=new BinaryReader(File.OpenRead(PathName)))
                {
                    if(r.ReadInt32()!=1)throw new IOException("Unsupported ladder version");
                    Day=r.ReadString();int count=r.ReadInt32();if(count<0 || count>100000)throw new IOException("Invalid ladder rows");
                    for(int i=0;i<count;i++){var row=new Row {Serial=r.ReadInt32(),Template=r.ReadInt32(),Rating=r.ReadInt32(),Wins=r.ReadInt32(),Losses=r.ReadInt32(),Draws=r.ReadInt32()};Rows[row.Serial+":"+row.Template]=row;}
                    count=r.ReadInt32();if(count<0 || count>100000)throw new IOException("Invalid pair count");
                    for(int i=0;i<count;i++)Pairs[r.ReadString()]=r.ReadInt32();
                }
            }
            catch(Exception e){Rows.Clear();Pairs.Clear();Healthy=false;Console.WriteLine("[Arena] Template ladder unavailable: "+e.Message);}
        }
        private static void Save()
        {
            Directory.CreateDirectory("Saves");
            using(var f=new FileStream(PathName+".tmp",FileMode.Create,FileAccess.Write,FileShare.None))
            {
                using(var w=new BinaryWriter(f,Encoding.UTF8,true))
                {
                    w.Write(1);w.Write(Day);w.Write(Rows.Count);
                    foreach(var row in Rows.Values){w.Write(row.Serial);w.Write(row.Template);w.Write(row.Rating);w.Write(row.Wins);w.Write(row.Losses);w.Write(row.Draws);}
                    w.Write(Pairs.Count);foreach(var pair in Pairs){w.Write(pair.Key);w.Write(pair.Value);}
                }
                f.Flush(true);
            }
            if(File.Exists(PathName))File.Replace(PathName+".tmp",PathName,null);else File.Move(PathName+".tmp",PathName);
        }
        public static int Template(DuelRules rules)
        {
            for(int i=0;i<2;i++)if(ArenaMatchmaking.Rules(i).ToString()==rules.ToString())return i;
            return -1;
        }
        public static bool Eligible(PlayerMobile a,PlayerMobile b,DuelRules rules)
        {
            return Healthy && a!=null && b!=null && a.AccessLevel==AccessLevel.Player && b.AccessLevel==AccessLevel.Player
                && a.Account!=null && b.Account!=null && a.Account!=b.Account && Template(rules)>=0 && !rules.Training;
        }
        private static Row Get(PlayerMobile p,int template)
        {
            string key=p.Serial.Value+":"+template;Row row;
            if(!Rows.TryGetValue(key,out row))Rows[key]=row=new Row {Serial=p.Serial.Value,Template=template};
            return row;
        }
        public static void Finish(DuelMatch m,PlayerMobile winner)
        {
            if(!m.Ranked)return;
            m.LadderResult="unrated: ladder unavailable";
            if(!Eligible(m.A,m.B,m.Rules))return;
            string today=DateTime.UtcNow.ToString("yyyyMMdd");if(Day!=today){Day=today;Pairs.Clear();}
            var accounts=new[]{m.A.Account.Username.ToLowerInvariant(),m.B.Account.Username.ToLowerInvariant()};Array.Sort(accounts,StringComparer.Ordinal);
            string pair;
            using(var sha=SHA256.Create())pair=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n",accounts))));
            // Shared across both templates and all characters of each account pair.
            int played;Pairs.TryGetValue(pair,out played);
            if(played>=PairLimit){m.LadderResult="unrated: daily opponent limit (3 per account pair, UTC)";return;}
            int template=Template(m.Rules);var a=Get(m.A,template);var b=Get(m.B,template);
            var old=new[]{a.Rating,a.Wins,a.Losses,a.Draws,b.Rating,b.Wins,b.Losses,b.Draws};
            double expected=1.0/(1.0+Math.Pow(10,(b.Rating-a.Rating)/400.0));
            int delta=(int)Math.Round(24*((winner==null ? .5 : winner==m.A ? 1.0 : 0.0)-expected),MidpointRounding.AwayFromZero);
            a.Rating+=delta;b.Rating-=delta;
            if(winner==null){a.Draws++;b.Draws++;}else if(winner==m.A){a.Wins++;b.Losses++;}else{b.Wins++;a.Losses++;}
            Pairs[pair]=played+1;
            try{Save();m.LadderResult="rated: "+ArenaMatchmaking.Name(template);}
            catch(Exception e)
            {
                a.Rating=old[0];a.Wins=old[1];a.Losses=old[2];a.Draws=old[3];b.Rating=old[4];b.Wins=old[5];b.Losses=old[6];b.Draws=old[7];
                if(played==0)Pairs.Remove(pair);else Pairs[pair]=played;
                Healthy=false;Console.WriteLine("[Arena] Template ladder save failed: "+e.Message);
            }
            foreach(var p in new[]{m.A,m.B})p.SendMessage("[Arena] "+m.LadderResult);
        }
        public static string Snapshot()
        {
            return "{\"pairLimit\":3,\"initialRating\":1000,\"k\":24,\"rows\":["+String.Join(",",Rows.Values.OrderByDescending(r=>r.Rating).Select(r=>{
                var p=World.FindMobile(r.Serial);if(p==null || p.Deleted)return null;
                return "{\"serial\":"+r.Serial+",\"name\":"+ArenaExperience.Q(p.Name)+",\"template\":"+r.Template+",\"rating\":"+r.Rating+",\"wins\":"+r.Wins+",\"losses\":"+r.Losses+",\"draws\":"+r.Draws+",\"online\":"+(p.NetState!=null?"true":"false")+"}";
            }).Where(s=>s!=null))+"]}";
        }
    }
}
