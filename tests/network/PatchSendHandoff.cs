using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Preserve the deployed assembly; serialize its existing callback with Send/Flush.
class PatchSendHandoff {
 static string Hash(string p) { using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(p))).Replace("-", "").ToLowerInvariant(); }
 static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> ts) {
  foreach(var t in ts) { yield return t; foreach(var n in Types(t.NestedTypes)) yield return n; }
 }
 static string Fingerprint(MethodDefinition m) {
  if(!m.HasBody)return "no-body";
  var b=m.Body;
  Func<object,string> op=o=>o is Instruction ? "@"+b.Instructions.IndexOf((Instruction)o) :
   o is Instruction[] ? String.Join(",",((Instruction[])o).Select(i=>b.Instructions.IndexOf(i))) : o==null?"":o.ToString();
  return String.Join(";",b.Instructions.Select(i=>i.OpCode.Name+":"+op(i.Operand)))+"|"+
   String.Join(";",b.Variables.Select(v=>v.VariableType.FullName))+"|"+
   String.Join(";",b.ExceptionHandlers.Select(e=>e.HandlerType+":"+op(e.TryStart)+":"+op(e.TryEnd)+":"+op(e.HandlerStart)+":"+op(e.HandlerEnd)+":"+op(e.FilterStart)));
 }
 static Dictionary<string,string> Methods(AssemblyDefinition a) {return Types(a.MainModule.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName,Fingerprint);}
 static void Main(string[] args) {
  if(args.Length!=2 || Hash(args[0])!="49fd952b952e11939c194c3cc8fdec03309f14a19e0d7a8a0eece750dbc10898")throw new Exception("Unexpected input assembly");
  var a=AssemblyDefinition.ReadAssembly(args[0]);var mod=a.MainModule;var before=Methods(a);
  var t=mod.Types.Single(x=>x.FullName=="Server.Network.NetState");
  var m=t.Methods.Single(x=>x.Name=="OnSend");var old=Fingerprint(m);
  var core=new MethodDefinition("OnSendSerializedCore",m.Attributes,m.ReturnType);
  foreach(var p in m.Parameters)core.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,p.ParameterType));
  t.Methods.Add(core);
  var cb=core.Body;cb.InitLocals=m.Body.InitLocals;cb.MaxStackSize=m.Body.MaxStackSize;
  foreach(var v in m.Body.Variables)cb.Variables.Add(new VariableDefinition(v.VariableType));
  var map=new Dictionary<Instruction,Instruction>();
  foreach(var i in m.Body.Instructions){var n=Instruction.Create(OpCodes.Nop);n.OpCode=i.OpCode;n.Operand=i.Operand;map[i]=n;cb.Instructions.Add(n);}
  foreach(var i in cb.Instructions){
   if(i.Operand is Instruction)i.Operand=map[(Instruction)i.Operand];
   else if(i.Operand is Instruction[])i.Operand=((Instruction[])i.Operand).Select(x=>map[x]).ToArray();
   else if(i.Operand is VariableDefinition)i.Operand=cb.Variables[m.Body.Variables.IndexOf((VariableDefinition)i.Operand)];
   else if(i.Operand is ParameterDefinition)i.Operand=core.Parameters[((ParameterDefinition)i.Operand).Index];
  }
  Func<Instruction,Instruction> remap=i=>i==null?null:map[i];
  foreach(var e in m.Body.ExceptionHandlers)cb.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType){TryStart=remap(e.TryStart),TryEnd=remap(e.TryEnd),HandlerStart=remap(e.HandlerStart),HandlerEnd=remap(e.HandlerEnd),FilterStart=remap(e.FilterStart),CatchType=e.CatchType});
  if(Fingerprint(core)!=old)throw new Exception("Callback clone differs");
  // Continuations in the clone still reference original OnSend, hence the lock
  // also protects every subsequent asynchronous completion.
  m.Body=new MethodBody(m){InitLocals=true,MaxStackSize=2};var b=m.Body;var il=b.GetILProcessor();
  var gate=new VariableDefinition(mod.TypeSystem.Object);var held=new VariableDefinition(mod.TypeSystem.Boolean);b.Variables.Add(gate);b.Variables.Add(held);
  il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,t.Fields.Single(f=>f.Name=="_SendLock"));il.Emit(OpCodes.Stloc,gate);
  var start=Instruction.Create(OpCodes.Ldloc,gate);il.Append(start);il.Emit(OpCodes.Ldloca,held);
  il.Emit(OpCodes.Call,mod.ImportReference(typeof(System.Threading.Monitor).GetMethod("Enter",new[]{typeof(object),typeof(bool).MakeByRefType()})));
  il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Call,core);
  var done=Instruction.Create(OpCodes.Ret);il.Emit(OpCodes.Leave,done);
  var cleanup=Instruction.Create(OpCodes.Ldloc,held);il.Append(cleanup);var endFinally=Instruction.Create(OpCodes.Endfinally);
  il.Emit(OpCodes.Brfalse,endFinally);il.Emit(OpCodes.Ldloc,gate);il.Emit(OpCodes.Call,mod.ImportReference(typeof(System.Threading.Monitor).GetMethod("Exit",new[]{typeof(object)})));il.Append(endFinally);il.Append(done);
  b.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally){TryStart=start,TryEnd=cleanup,HandlerStart=cleanup,HandlerEnd=done});
  a.Write(args[1]);var check=AssemblyDefinition.ReadAssembly(args[1]);var after=Methods(check);
  foreach(var x in before)if(x.Key!=m.FullName && (!after.ContainsKey(x.Key)||after[x.Key]!=x.Value))throw new Exception("Unexpected method change: "+x.Key);
  if(after.Count!=before.Count+1 || after[core.FullName]!=old)throw new Exception("Unexpected callback change");
  Console.WriteLine("Verified "+before.Count+" existing methods; only OnSend wrapper changed, original body preserved in helper. SHA256 "+Hash(args[1]));
 }
}
