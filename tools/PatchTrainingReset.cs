using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Patch only the deployed PrepareFighter condition. Pin the artifact so a newer
// deployment cannot be overwritten by an older full assembly rebuild.
class PatchTrainingReset {
    static string Hash(string p) { using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(p))).Replace("-", "").ToLowerInvariant(); }
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> ts) {
        foreach(var t in ts) { yield return t; foreach(var n in Types(t.NestedTypes)) yield return n; }
    }
    static string Fingerprint(MethodDefinition m) {
        if(!m.HasBody) return "no-body";
        var b=m.Body;
        Func<object,string> operand=o=>o is Instruction ? "@"+b.Instructions.IndexOf((Instruction)o) :
            o is Instruction[] ? String.Join(",",((Instruction[])o).Select(i=>b.Instructions.IndexOf(i))) :
            o==null ? "" : o.ToString();
        return String.Join(";",b.Instructions.Select(i=>i.OpCode.Name+":"+operand(i.Operand))) +
            "|"+String.Join(";",b.Variables.Select(v=>v.VariableType.FullName))+
            "|"+String.Join(";",b.ExceptionHandlers.Select(e=>e.HandlerType+":"+operand(e.TryStart)+":"+
                operand(e.TryEnd)+":"+operand(e.HandlerStart)+":"+operand(e.HandlerEnd)+":"+operand(e.FilterStart)));
    }
    static Dictionary<string,string> Methods(AssemblyDefinition a) {
        return Types(a.MainModule.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName,m=>Fingerprint(m));
    }
    static void Main(string[] args) {
        if(args.Length != 2) throw new Exception("input output required");
        if(Hash(args[0]) != "b03a253bb85b1ead9eff732dab7e8749e9e3a38476351ccfaa149dfa678225b0")
            throw new Exception("Unexpected deployed assembly; inspect before patching");
        var resolver=new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        var reader=new ReaderParameters { AssemblyResolver=resolver };
        var a=AssemblyDefinition.ReadAssembly(args[0],reader);
        var before=Methods(a);
        var t=a.MainModule.Types.Single(x=>x.FullName=="Server.Engines.Dueling.DuelMatch");
        var m=t.Methods.Single(x=>x.Name=="PrepareFighter");
        var rules=t.Methods.Single(x=>x.Name=="get_Rules");
        var training=a.MainModule.Types.Single(x=>x.FullName=="Server.Engines.Dueling.DuelRules").Methods.Single(x=>x.Name=="get_Training");
        var call=m.Body.Instructions.Single(x=>x.Operand is MethodReference && ((MethodReference)x.Operand).Name=="IsServiceMatch");
        if(call.Next.OpCode.Code != Code.Brfalse && call.Next.OpCode.Code != Code.Brfalse_S)
            throw new Exception("Unexpected condition shape");
        var il=m.Body.GetILProcessor();
        var next=call.Next;
        il.InsertBefore(next,il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(next,il.Create(OpCodes.Call,rules));
        il.InsertBefore(next,il.Create(OpCodes.Callvirt,training));
        il.InsertBefore(next,il.Create(OpCodes.Or));
        var first=next.Next;
        foreach(var setterName in new[]{"set_StunReady","set_DisarmReady"}) {
            var setter=a.MainModule.GetMemberReferences().OfType<MethodReference>()
                .First(x=>x.Name==setterName && x.DeclaringType.FullName=="Server.Mobile");
            il.InsertBefore(first,il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first,il.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(first,il.Create(OpCodes.Callvirt,setter));
        }
        // Avoid short-branch displacement overflow after inserting instructions.
        var longCodes=typeof(OpCodes).GetFields().Where(f=>f.FieldType==typeof(OpCode))
            .Select(f=>(OpCode)f.GetValue(null)).ToDictionary(o=>o.Name,o=>o);
        foreach(var i in m.Body.Instructions)
            if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)
                i.OpCode=longCodes[i.OpCode.Name.Substring(0,i.OpCode.Name.Length-2)];
        m.Body.MaxStackSize=Math.Max(m.Body.MaxStackSize,3);
        a.Write(args[1]);
        var check=AssemblyDefinition.ReadAssembly(args[1],reader);
        var after=Methods(check);
        var changed=before.Keys.Where(k=>!after.ContainsKey(k)||before[k]!=after[k]).ToArray();
        if(before.Count!=after.Count || changed.Length!=1 || changed[0]!=m.FullName)
            throw new Exception("Unexpected method changes; do not deploy");
        Console.WriteLine("Compared "+before.Count+" method bodies; exactly one changed");
        Console.WriteLine("Patched only DuelMatch.PrepareFighter: IsServiceMatch(this) | Rules.Training");
        Console.WriteLine(Hash(args[1]));
    }
}
