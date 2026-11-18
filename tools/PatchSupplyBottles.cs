using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Patch only supply refill and add empty-bottle banking. Pin the artifact so a newer
// deployment cannot be overwritten by an older full assembly rebuild.
class PatchSupplyBottles {
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
        if(args.Length!=2) throw new Exception("input output required");
        if(Hash(args[0])!="e05037e49859138f14fae1b091dbb7f1b753501278ef0385d087fc6b3c6cfb02")
            throw new Exception("Unexpected deployed assembly");
        var resolver=new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        var reader=new ReaderParameters { AssemblyResolver=resolver };
        var a=AssemblyDefinition.ReadAssembly(args[0],reader);var mod=a.MainModule;
        var before=Methods(a);
        var refs=mod.GetMemberReferences().OfType<MethodReference>().ToArray();
        var owner=mod.Types.Single(t=>t.FullName=="Server.Engines.Dueling.ArenaSupplies");
        var player=mod.Types.Single(t=>t.FullName=="Server.Mobiles.PlayerMobile");
        var bottle=mod.Types.Single(t=>t.FullName=="Server.Items.Bottle");
        var getPack=refs.First(m=>m.Name=="get_Backpack" && m.DeclaringType.FullName=="Server.Mobile");
        var getBank=refs.First(m=>m.Name=="get_BankBox" && m.DeclaringType.FullName=="Server.Mobile");
        var find=refs.First(m=>m.Name=="FindItemsByType" && m.Parameters.Count==2 && m.Parameters[0].ParameterType.FullName=="System.Type" && m.Parameters[1].ParameterType.FullName=="System.Boolean");
        var drop=refs.First(m=>m.Name=="DropItem" && m.Parameters.Count==1 && m.Parameters[0].ParameterType.FullName=="Server.Item");
        var getType=mod.ImportReference(typeof(object).GetMethod("GetType"));
        var fromHandle=mod.ImportReference(typeof(Type).GetMethod("GetTypeFromHandle"));
        var equal=mod.ImportReference(typeof(Type).GetMethod("op_Equality"));
        var helper=new MethodDefinition("StoreEmptyBottles",MethodAttributes.Private|MethodAttributes.Static|MethodAttributes.HideBySig,mod.TypeSystem.Void);
        helper.Parameters.Add(new ParameterDefinition("p",ParameterAttributes.None,player));owner.Methods.Add(helper);
        var body=helper.Body;body.InitLocals=true;body.MaxStackSize=3;
        body.Variables.Add(new VariableDefinition(find.ReturnType));
        body.Variables.Add(new VariableDefinition(mod.TypeSystem.Int32));
        body.Variables.Add(new VariableDefinition(((ArrayType)find.ReturnType).ElementType));
        var il=body.GetILProcessor();var end=il.Create(OpCodes.Ret);
        var loop=il.Create(OpCodes.Ldloc_0);var increment=il.Create(OpCodes.Ldloc_1);var check=il.Create(OpCodes.Ldloc_1);
        il.Append(il.Create(OpCodes.Ldarg_0));il.Append(il.Create(OpCodes.Callvirt,getPack));il.Append(il.Create(OpCodes.Brfalse,end));
        il.Append(il.Create(OpCodes.Ldarg_0));il.Append(il.Create(OpCodes.Callvirt,getPack));
        il.Append(il.Create(OpCodes.Ldtoken,bottle));il.Append(il.Create(OpCodes.Call,fromHandle));il.Append(il.Create(OpCodes.Ldc_I4_1));il.Append(il.Create(OpCodes.Callvirt,find));il.Append(il.Create(OpCodes.Stloc_0));
        il.Append(il.Create(OpCodes.Ldc_I4_0));il.Append(il.Create(OpCodes.Stloc_1));il.Append(il.Create(OpCodes.Br,check));
        il.Append(loop);il.Append(il.Create(OpCodes.Ldloc_1));il.Append(il.Create(OpCodes.Ldelem_Ref));il.Append(il.Create(OpCodes.Stloc_2));
        il.Append(il.Create(OpCodes.Ldloc_2));il.Append(il.Create(OpCodes.Callvirt,getType));il.Append(il.Create(OpCodes.Ldtoken,bottle));il.Append(il.Create(OpCodes.Call,fromHandle));il.Append(il.Create(OpCodes.Call,equal));il.Append(il.Create(OpCodes.Brfalse,increment));
        il.Append(il.Create(OpCodes.Ldarg_0));il.Append(il.Create(OpCodes.Callvirt,getBank));il.Append(il.Create(OpCodes.Ldloc_2));il.Append(il.Create(OpCodes.Callvirt,drop));
        il.Append(increment);il.Append(il.Create(OpCodes.Ldc_I4_1));il.Append(il.Create(OpCodes.Add));il.Append(il.Create(OpCodes.Stloc_1));
        il.Append(check);il.Append(il.Create(OpCodes.Ldloc_0));il.Append(il.Create(OpCodes.Ldlen));il.Append(il.Create(OpCodes.Conv_I4));il.Append(il.Create(OpCodes.Blt,loop));il.Append(end);
        var refill=owner.Methods.Single(m=>m.Name=="Refill");var ril=refill.Body.GetILProcessor();
        var stock=refill.Body.Instructions.Single(i=>i.Operand is MethodReference && ((MethodReference)i.Operand).Name=="StockCombat");
        if(stock.Previous.OpCode.Code!=Code.Ldarg_0)throw new Exception("unexpected Refill layout");
        ril.InsertBefore(stock,ril.Create(OpCodes.Call,helper));ril.InsertBefore(stock,ril.Create(OpCodes.Ldarg_0));
        foreach(var i in refill.Body.Instructions)
            if(i.OpCode.Code==Code.Ldstr && ((string)i.Operand).StartsWith("[Arena] Supplies refilled"))
                i.Operand=((string)i.Operand).Replace("[Arena] Supplies refilled","[Arena] Empty bottles stored in your bank. Supplies refilled");
        var longCodes=typeof(OpCodes).GetFields().Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)).ToDictionary(o=>o.Name,o=>o);
        foreach(var i in refill.Body.Instructions)if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)i.OpCode=longCodes[i.OpCode.Name.Substring(0,i.OpCode.Name.Length-2)];
        a.Write(args[1]);var after=Methods(AssemblyDefinition.ReadAssembly(args[1],reader));
        var changed=before.Keys.Where(k=>!after.ContainsKey(k)||before[k]!=after[k]).ToArray();
        var added=after.Keys.Except(before.Keys).ToArray();
        if(changed.Length!=1||changed[0]!=refill.FullName||added.Length!=1||added[0]!=helper.FullName)throw new Exception("unexpected method changes");
        Console.WriteLine("Verified "+before.Count+" existing methods: only Refill changed; only StoreEmptyBottles added.");
        Console.WriteLine(Hash(args[1]));
    }
}
