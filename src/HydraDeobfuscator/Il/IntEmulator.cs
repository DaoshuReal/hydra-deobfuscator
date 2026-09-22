using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;

namespace HydraDeobfuscator.Il;

internal static class IntEmulator
{
    public static bool TryEmulate(MethodDef method, DeobfuscationContext context, HashSet<MethodDef> visiting, out int result, int depth)
    {
        result = 0;

        if (context.IntProxyValues.TryGetValue(method, out int cached)) { result = cached; return true; }
        if (visiting.Contains(method)) return false;
        if (depth > 10) return false;
        if (!method.HasBody) return false;

        var body = method.Body;

        if (body.Instructions.Count == 0) return false;

        visiting.Add(method);

        try
        {
            var instrs = body.Instructions;
            var offsetToIndex = IlHelpers.BuildOffsetIndex(instrs);

            int locCount = body.Variables.Count;
            object?[] locals = new object?[Math.Max(locCount, 32)];

            for (int i = 0; i < locals.Length; i++) locals[i] = (int)0;

            var stack = new Stack<object?>();
            int pc = 0;
            int steps = 0;

            const int maxSteps = 20000;

            while (pc >= 0 && pc < instrs.Count)
            {
                if (++steps > maxSteps) return false;
                var ins = instrs[pc];
                var code = ins.OpCode.Code;
                switch (code)
                {
                    case Code.Nop: pc++; break;
                    case Code.Ldc_I4_M1:
                    case Code.Ldc_I4_0:
                    case Code.Ldc_I4_1:
                    case Code.Ldc_I4_2:
                    case Code.Ldc_I4_3:
                    case Code.Ldc_I4_4:
                    case Code.Ldc_I4_5:
                    case Code.Ldc_I4_6:
                    case Code.Ldc_I4_7:
                    case Code.Ldc_I4_8:
                    case Code.Ldc_I4_S:
                    case Code.Ldc_I4:
                        IlHelpers.IsLdcI4(ins, out int lv); stack.Push(lv); pc++; break;
                    case Code.Ldc_I8:
                        stack.Push((long)Convert.ToInt64(ins.Operand)); pc++; break;
                    case Code.Ldc_R4:
                        stack.Push((double)Convert.ToDouble(ins.Operand)); pc++; break;
                    case Code.Ldc_R8:
                        stack.Push(Convert.ToDouble(ins.Operand)); pc++; break;
                    case Code.Ldstr:
                        return false;
                    case Code.Ldnull:
                        stack.Push(null); pc++; break;
                    case Code.Dup:
                        if (stack.Count == 0) return false;
                        stack.Push(stack.Peek()); pc++; break;
                    case Code.Pop:
                        if (stack.Count == 0) return false;
                        stack.Pop(); pc++; break;
                    case Code.Sizeof:
                        {
                            string tn = "";
                            try { tn = (ins.Operand as ITypeDefOrRef)?.FullName ?? ""; } catch { }
                            int sz = 4;
                            if (tn.Contains("Double") || tn.Contains("Int64") || tn.Contains("UInt64")) sz = 8;
                            else if (tn.Contains("Byte") || tn.Contains("SByte") || tn.Contains("Boolean")) sz = 1;
                            else if (tn.Contains("Char") || tn.Contains("Int16") || tn.Contains("UInt16")) sz = 2;
                            else sz = 4;
                            stack.Push(sz); pc++; break;
                        }
                    case Code.Ldloc_0:
                    case Code.Ldloc_1:
                    case Code.Ldloc_2:
                    case Code.Ldloc_3:
                    case Code.Ldloc_S:
                    case Code.Ldloc:
                        {
                            int idx = IlHelpers.GetLocalIndex(ins, body);
                            if (idx < 0 || idx >= locals.Length) return false;
                            stack.Push(locals[idx]); pc++; break;
                        }
                    case Code.Stloc_0:
                    case Code.Stloc_1:
                    case Code.Stloc_2:
                    case Code.Stloc_3:
                    case Code.Stloc_S:
                    case Code.Stloc:
                        {
                            int idx = IlHelpers.GetLocalIndex(ins, body);
                            if (idx < 0) return false;
                            if (stack.Count == 0) return false;
                            if (idx >= locals.Length) Array.Resize(ref locals, idx + 8);
                            locals[idx] = stack.Pop(); pc++; break;
                        }
                    case Code.Ldloca_S:
                    case Code.Ldloca:
                        return false;
                    case Code.Ldarg_0:
                    case Code.Ldarg_1:
                    case Code.Ldarg_2:
                    case Code.Ldarg_3:
                    case Code.Ldarg_S:
                    case Code.Ldarg:
                    case Code.Ldarga_S:
                    case Code.Ldarga:
                        return false;
                    case Code.Add:
                    case Code.Add_Ovf:
                    case Code.Add_Ovf_Un:
                        { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(unchecked(IlHelpers.ToInt32(a) + IlHelpers.ToInt32(b))); pc++; break; }
                    case Code.Sub:
                    case Code.Sub_Ovf:
                    case Code.Sub_Ovf_Un:
                        { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(unchecked(IlHelpers.ToInt32(a) - IlHelpers.ToInt32(b))); pc++; break; }
                    case Code.Mul:
                    case Code.Mul_Ovf:
                    case Code.Mul_Ovf_Un:
                        { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(unchecked(IlHelpers.ToInt32(a) * IlHelpers.ToInt32(b))); pc++; break; }
                    case Code.Div:
                    case Code.Div_Un:
                        { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); int bi = IlHelpers.ToInt32(b); if (bi == 0) return false; stack.Push(IlHelpers.ToInt32(a) / bi); pc++; break; }
                    case Code.Rem: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); int bi = IlHelpers.ToInt32(b); if (bi == 0) return false; stack.Push(IlHelpers.ToInt32(a) % bi); pc++; break; }
                    case Code.Rem_Un: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); uint au = unchecked((uint)IlHelpers.ToInt32(a)); uint bu = unchecked((uint)IlHelpers.ToInt32(b)); if (bu == 0) return false; stack.Push(unchecked((int)(au % bu))); pc++; break; }
                    case Code.And: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ToInt32(a) & IlHelpers.ToInt32(b)); pc++; break; }
                    case Code.Or: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ToInt32(a) | IlHelpers.ToInt32(b)); pc++; break; }
                    case Code.Xor: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ToInt32(a) ^ IlHelpers.ToInt32(b)); pc++; break; }
                    case Code.Shl: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ToInt32(a) << (IlHelpers.ToInt32(b) & 31)); pc++; break; }
                    case Code.Shr: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ToInt32(a) >> (IlHelpers.ToInt32(b) & 31)); pc++; break; }
                    case Code.Shr_Un: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(unchecked((int)((uint)IlHelpers.ToInt32(a) >> (IlHelpers.ToInt32(b) & 31)))); pc++; break; }
                    case Code.Neg: { if (stack.Count < 1) return false; var a = stack.Pop(); stack.Push(unchecked(-IlHelpers.ToInt32(a))); pc++; break; }
                    case Code.Not: { if (stack.Count < 1) return false; var a = stack.Pop(); stack.Push(~IlHelpers.ToInt32(a)); pc++; break; }
                    case Code.Ceq: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ValuesEqual(a, b) ? 1 : 0); pc++; break; }
                    case Code.Cgt: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ToInt32(a) > IlHelpers.ToInt32(b) ? 1 : 0); pc++; break; }
                    case Code.Cgt_Un: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(unchecked((uint)IlHelpers.ToInt32(a)) > unchecked((uint)IlHelpers.ToInt32(b)) ? 1 : 0); pc++; break; }
                    case Code.Clt: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(IlHelpers.ToInt32(a) < IlHelpers.ToInt32(b) ? 1 : 0); pc++; break; }
                    case Code.Clt_Un: { if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop(); stack.Push(unchecked((uint)IlHelpers.ToInt32(a)) < unchecked((uint)IlHelpers.ToInt32(b)) ? 1 : 0); pc++; break; }
                    case Code.Conv_I1:
                    case Code.Conv_I2:
                    case Code.Conv_I4:
                    case Code.Conv_I8:
                    case Code.Conv_U1:
                    case Code.Conv_U2:
                    case Code.Conv_U4:
                    case Code.Conv_U8:
                    case Code.Conv_R4:
                    case Code.Conv_R8:
                    case Code.Conv_I:
                    case Code.Conv_U:
                        {
                            if (stack.Count < 1) return false; var a = stack.Pop();
                            if (code == Code.Conv_R4 || code == Code.Conv_R8) stack.Push((double)IlHelpers.ToInt32(a));
                            else if (code == Code.Conv_U1) stack.Push(IlHelpers.ToInt32(a) & 0xFF);
                            else if (code == Code.Conv_U2) stack.Push(IlHelpers.ToInt32(a) & 0xFFFF);
                            else if (code == Code.Conv_I1) stack.Push((int)(sbyte)IlHelpers.ToInt32(a));
                            else if (code == Code.Conv_I2) stack.Push((int)(short)IlHelpers.ToInt32(a));
                            else stack.Push(IlHelpers.ToInt32(a));
                            pc++; break;
                        }
                    case Code.Br_S:
                    case Code.Br:
                        { var tgt = ins.Operand as Instruction; if (tgt == null) return false; if (!offsetToIndex.TryGetValue(tgt.Offset, out int ni)) return false; pc = ni; break; }
                    case Code.Brfalse_S:
                    case Code.Brfalse:
                    case Code.Brtrue_S:
                    case Code.Brtrue:
                        { if (stack.Count < 1) return false; var c = stack.Pop(); bool isTrue = IlHelpers.IsTrue(c); bool isBrFalse = code == Code.Brfalse || code == Code.Brfalse_S; bool take = isBrFalse ? !isTrue : isTrue; if (take) { var tgt = ins.Operand as Instruction; if (tgt == null) return false; if (!offsetToIndex.TryGetValue(tgt.Offset, out int ni)) return false; pc = ni; } else pc++; break; }
                    case Code.Beq_S:
                    case Code.Beq:
                    case Code.Bne_Un_S:
                    case Code.Bne_Un:
                    case Code.Bge_S:
                    case Code.Bge:
                    case Code.Bgt_S:
                    case Code.Bgt:
                    case Code.Ble_S:
                    case Code.Ble:
                    case Code.Blt_S:
                    case Code.Blt:
                    case Code.Bge_Un_S:
                    case Code.Bge_Un:
                    case Code.Bgt_Un_S:
                    case Code.Bgt_Un:
                    case Code.Ble_Un_S:
                    case Code.Ble_Un:
                    case Code.Blt_Un_S:
                    case Code.Blt_Un:
                        {
                            if (stack.Count < 2) return false; var b = stack.Pop(); var a = stack.Pop();
                            bool take = IlHelpers.EvalBranch(code, a, b);
                            if (take) { var tgt = ins.Operand as Instruction; if (tgt == null) return false; if (!offsetToIndex.TryGetValue(tgt.Offset, out int ni)) return false; pc = ni; } else pc++; break;
                        }
                    case Code.Switch:
                        {
                            if (stack.Count < 1) return false; var v = stack.Pop(); int idx = unchecked((int)IlHelpers.ToUInt32(v));
                            var targets = ins.Operand as IList<Instruction>;
                            if (targets == null) return false;
                            if (idx >= 0 && idx < targets.Count)
                            {
                                var tgt = targets[idx];
                                if (!offsetToIndex.TryGetValue(tgt.Offset, out int ni)) return false;
                                pc = ni;
                            }
                            else pc++;
                            break;
                        }
                    case Code.Leave_S:
                    case Code.Leave:
                        {
                            var tgt = ins.Operand as Instruction; if (tgt == null) return false;
                            if (!offsetToIndex.TryGetValue(tgt.Offset, out int ni)) return false; pc = ni; break;
                        }
                    case Code.Ret:
                        {
                            if (stack.Count == 0) return false;
                            var rv = stack.Pop();
                            result = IlHelpers.ToInt32(rv);
                            return true;
                        }
                    case Code.Call:
                    case Code.Callvirt:
                        {
                            var mref = ins.Operand as IMethod;
                            if (mref == null) return false;
                            string mn = mref.Name.String;
                            string dt = mref.DeclaringType?.FullName ?? "";
                            if (mn == "Assert")
                            {
                                if (stack.Count < 1) return false;
                                stack.Pop(); pc++; break;
                            }
                            if (mn == "get_EmptyTypes")
                            {
                                stack.Push(new Type[0]); pc++; break;
                            }
                            if (dt.Contains("Random") && mn == ".ctor")
                            {
                                if (stack.Count < 1) return false;
                                var seedObj = stack.Pop();
                                int seed = IlHelpers.ToInt32(seedObj);
                                stack.Push(new Random(seed)); pc++; break;
                            }
                            if (dt.Contains("Random") && mn == "Next")
                            {
                                int pcount = 1;
                                try { var md0 = mref.ResolveMethodDef(); if (md0 != null) pcount = md0.Parameters.Count; } catch { }
                                if (pcount == 0)
                                {
                                    if (stack.Count < 1) return false;
                                    var inst0 = stack.Pop();
                                    if (inst0 is not Random r0) return false;
                                    try { stack.Push(r0.Next()); } catch { return false; }
                                    pc++; break;
                                }
                                else if (pcount == 1)
                                {
                                    if (stack.Count < 2) return false;
                                    var maxObj = stack.Pop();
                                    var instObj = stack.Pop();
                                    if (instObj is not Random rnd) return false;
                                    int max = IlHelpers.ToInt32(maxObj);
                                    try { stack.Push(rnd.Next(max)); } catch { return false; }
                                    pc++; break;
                                }
                                else if (pcount == 2)
                                {
                                    if (stack.Count < 3) return false;
                                    var max2 = IlHelpers.ToInt32(stack.Pop());
                                    var min2 = IlHelpers.ToInt32(stack.Pop());
                                    var inst2 = stack.Pop();
                                    if (inst2 is not Random r2) return false;
                                    try { stack.Push(r2.Next(min2, max2)); } catch { return false; }
                                    pc++; break;
                                }
                                return false;
                            }
                            if (mn == "get_Size" && dt.Contains("IntPtr"))
                            {
                                stack.Push(IntPtr.Size); pc++; break;
                            }
                            if (dt.Contains("Convert") && mn == "ToInt32")
                            {
                                if (stack.Count < 1) return false;
                                var a0 = stack.Pop();
                                double dv = a0 is double dd ? dd : IlHelpers.ToInt32(a0);
                                try { stack.Push(Convert.ToInt32(dv)); } catch { return false; }
                                pc++; break;
                            }
                            if (dt.Contains("Math") && (mn == "Abs" || mn == "Sin" || mn == "Cos" || mn == "Tan" || mn == "Tanh" || mn == "Sqrt" || mn == "Log" || mn == "Log10" || mn == "Floor" || mn == "Ceiling" || mn == "Round" || mn == "Truncate" || mn == "Exp"))
                            {
                                if (stack.Count < 1) return false;
                                var a0 = stack.Pop();
                                double dv = a0 is double dd ? dd : IlHelpers.ToInt32(a0);
                                double res = 0;
                                try
                                {
                                    switch (mn)
                                    {
                                        case "Abs": res = Math.Abs(dv); break;
                                        case "Sin": res = Math.Sin(dv); break;
                                        case "Cos": res = Math.Cos(dv); break;
                                        case "Tan": res = Math.Tan(dv); break;
                                        case "Tanh": res = Math.Tanh(dv); break;
                                        case "Sqrt": res = Math.Sqrt(dv); break;
                                        case "Log": res = Math.Log(dv); break;
                                        case "Log10": res = Math.Log10(dv); break;
                                        case "Floor": res = Math.Floor(dv); break;
                                        case "Ceiling": res = Math.Ceiling(dv); break;
                                        case "Round": res = Math.Round(dv); break;
                                        case "Truncate": res = Math.Truncate(dv); break;
                                        case "Exp": res = Math.Exp(dv); break;
                                        default: return false;
                                    }
                                }
                                catch { return false; }
                                stack.Push(res); pc++; break;
                            }
                            if ((mn == "Pow" || mn == "Min" || mn == "Max" || mn == "IEEERemainder") && dt.Contains("Math"))
                            {
                                if (stack.Count < 2) return false;
                                var b0 = stack.Pop(); var a0 = stack.Pop();
                                double da = a0 is double dda ? dda : IlHelpers.ToInt32(a0);
                                double db = b0 is double ddb ? ddb : IlHelpers.ToInt32(b0);
                                double res = 0;
                                try
                                {
                                    if (mn == "Pow") res = Math.Pow(da, db);
                                    else if (mn == "Min") res = Math.Min(da, db);
                                    else if (mn == "Max") res = Math.Max(da, db);
                                    else res = Math.IEEERemainder(da, db);
                                }
                                catch { return false; }
                                stack.Push(res); pc++; break;
                            }
                            {
                                var targetDef = (mref as IMethod)?.ResolveMethodDef();
                                if (targetDef == null) return false;
                                if (targetDef.ReturnType?.FullName == "System.Int32" && targetDef.Parameters.Count == 0 && targetDef.IsStatic)
                                {
                                    if (!TryEmulate(targetDef, context, visiting, out int subVal, depth + 1)) return false;
                                    stack.Push(subVal); pc++; break;
                                }
                                return false;
                            }
                        }
                    case Code.Newobj:
                        {
                            var mref = ins.Operand as IMethod;
                            if (mref == null) return false;
                            string mn = mref.Name.String;
                            string dt = mref.DeclaringType?.FullName ?? "";
                            if (dt.Contains("Random") && mn == ".ctor")
                            {
                                int pcount = 0;
                                try { var md = mref.ResolveMethodDef(); if (md != null) pcount = md.Parameters.Count; } catch { }
                                if (pcount == 1)
                                {
                                    if (stack.Count < 1) return false;
                                    int seed = IlHelpers.ToInt32(stack.Pop());
                                    stack.Push(new Random(seed));
                                }
                                else if (pcount == 0)
                                {
                                    stack.Push(new Random());
                                }
                                else return false;
                                pc++; break;
                            }
                            return false;
                        }
                    case Code.Newarr:
                        return false;
                    case Code.Ldlen:
                        {
                            if (stack.Count < 1) return false;
                            var arr = stack.Pop();
                            if (arr is Array a) stack.Push(a.Length);
                            else if (arr is Type[] t) stack.Push(t.Length);
                            else return false;
                            pc++; break;
                        }
                    case Code.Ldsfld:
                        {
                            var fld = (ins.Operand as FieldDef) ?? (ins.Operand as IField)?.ResolveFieldDef();
                            if (fld == null) return false;
                            if (context.HailFieldValues.TryGetValue(fld.Name.String, out int fv))
                            {
                                stack.Push(fv); pc++; break;
                            }
                            return false;
                        }
                    case Code.Stsfld:
                        return false;
                    case Code.Ldfld:
                    case Code.Stfld:
                    case Code.Ldflda:
                    case Code.Ldsflda:
                        return false;
                    case Code.Box:
                    case Code.Unbox_Any:
                        pc++; break;
                    case Code.Throw:
                    case Code.Rethrow:
                    case Code.Endfinally:
                    case Code.Endfilter:
                        return false;
                    default:
                        return false;
                }
            }
            return false;
        }
        finally { visiting.Remove(method); }
    }
}
