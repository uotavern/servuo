using System;
using System.Reflection;
using System.Runtime.Serialization;
using Server;
using Server.Mobiles;
using Server.Engines.Dueling;
class CheckRoundReset {
 static void Main() {
  Server.Misc.MapDefinitions.Configure();
  typeof(World).GetField("m_Mobiles",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,new System.Collections.Generic.Dictionary<Serial,Mobile>());
  var m = new PlayerMobile(); m.RawStr=100; m.RawDex=25; m.RawInt=100;
  m.Hits=10; m.Stam=5; m.Mana=7;
  m.StunReady=true; m.DisarmReady=true;
  m.AddStatMod(new StatMod(StatType.Str,"test-curse",-20,TimeSpan.Zero));
  var arena=(DuelArena)FormatterServices.GetUninitializedObject(typeof(DuelArena));
  var rules=new DuelRules(); rules.Training=true;
  var match=new DuelMatch(arena,m,null,1,rules);
  try { typeof(DuelMatch).GetMethod("PrepareFighter",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(match,new object[]{m}); }
  catch(Exception e){Console.WriteLine(e.ToString());throw;}
  if(m.Str!=100 || m.StatMods.Count!=0 || m.StunReady || m.DisarmReady || m.Hits!=m.HitsMax || m.Stam!=m.StamMax || m.Mana!=m.ManaMax)
   throw new Exception("training reset failed");
  Console.WriteLine("str="+m.Str+" mods="+m.StatMods.Count+" stun="+m.StunReady+" disarm="+m.DisarmReady+" hp="+m.Hits+"/"+m.HitsMax);
 }
}
