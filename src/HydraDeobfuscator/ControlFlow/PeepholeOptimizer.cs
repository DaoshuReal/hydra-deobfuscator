using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.ControlFlow;

internal static class PeepholeOptimizer
{
    public static void Execute(DeobfuscationContext context)
    {
        int removedAssert = 0;
        int removedLdPop = 0;
        int foldedConst = 0;
        int foldedBranch = 0;
        int removedConfuse = 0;
        int foldedSizeof = 0;
        int removedNops = 0;
        int removedDeadStore = 0;

        for (int iter = 0; iter < 12; iter++)
        {
            bool changed = false;

            foreach (var type in context.Module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;

                    try
                    {
                        var body0 = method.Body;
                        var loaded = new HashSet<int>();

                        foreach (var item in body0.Instructions)
                        {
                            var cc = item.OpCode.Code;

                            if (cc == Code.Ldloc_0 || cc == Code.Ldloc_1 || cc == Code.Ldloc_2 || cc == Code.Ldloc_3 ||
                                cc == Code.Ldloc_S || cc == Code.Ldloc || cc == Code.Ldloca_S || cc == Code.Ldloca)
                            {
                                int index = IlHelpers.GetLocalIndex(item, body0);

                                if (index >= 0)
                                    loaded.Add(index);
                            }
                        }

                        foreach (var item in body0.Instructions)
                        {
                            var cc = item.OpCode.Code;

                            if (cc == Code.Stloc_0 || cc == Code.Stloc_1 || cc == Code.Stloc_2 || cc == Code.Stloc_3 ||
                                cc == Code.Stloc_S || cc == Code.Stloc)
                            {
                                int index = IlHelpers.GetLocalIndex(item, body0);

                                if (index >= 0 && !loaded.Contains(index))
                                {
                                    item.OpCode = OpCodes.Pop;
                                    item.Operand = null;
                                    removedDeadStore++;
                                    changed = true;
                                }
                            }
                        }
                    }
                    catch { }

                    try
                    {
                        var body = method.Body;
                        var targets = new HashSet<Instruction>();

                        foreach (var ins in body.Instructions)
                        {
                            if (ins.Operand is Instruction single)
                                targets.Add(single);
                            else if (ins.Operand is IList<Instruction> list)
                                foreach (var x in list)
                                    targets.Add(x);
                        }

                        foreach (var eh in body.ExceptionHandlers)
                        {
                            if (eh.TryStart != null) targets.Add(eh.TryStart);
                            if (eh.TryEnd != null) targets.Add(eh.TryEnd);
                            if (eh.HandlerStart != null) targets.Add(eh.HandlerStart);
                            if (eh.HandlerEnd != null) targets.Add(eh.HandlerEnd);
                            if (eh.FilterStart != null) targets.Add(eh.FilterStart);
                        }

                        var toRemove = new List<Instruction>();

                        foreach (var ins in body.Instructions)
                        {
                            if (ins.OpCode.Code == Code.Nop && !targets.Contains(ins))
                                toRemove.Add(ins);
                        }

                        foreach (var item in toRemove)
                        {
                            body.Instructions.Remove(item);
                            removedNops++;
                            changed = true;
                        }
                    }
                    catch { }

                    var instrs = method.Body.Instructions;

                    for (int i = 0; i < instrs.Count - 1; i++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[i], out int v) && v == 1 && (instrs[i + 1].OpCode.Code == Code.Call || instrs[i + 1].OpCode.Code == Code.Callvirt))
                        {
                            var target = instrs[i + 1].Operand as IMethod;

                            if (target != null && target.Name.String == "Assert")
                            {
                                instrs[i].OpCode = OpCodes.Nop;
                                instrs[i].Operand = null;
                                instrs[i + 1].OpCode = OpCodes.Nop;
                                instrs[i + 1].Operand = null;
                                removedAssert++;
                                changed = true;
                            }
                        }
                    }

                    for (int i = 0; i < instrs.Count - 1; i++)
                    {
                        var a = instrs[i];
                        var b = instrs[i + 1];

                        if (a.OpCode.Code == Code.Dup && b.OpCode.Code == Code.Pop)
                        {
                            bool targeted = false;

                            try
                            {
                                foreach (var q in instrs)
                                {
                                    if (q.Operand is Instruction ti && (ti == a || ti == b)) { targeted = true; break; }
                                    if (q.Operand is IList<Instruction> lst && (lst.Contains(a) || lst.Contains(b))) { targeted = true; break; }
                                }

                                if (!targeted)
                                {
                                    foreach (var eh in method.Body.ExceptionHandlers)
                                    {
                                        if (eh.TryStart == a || eh.TryStart == b || eh.TryEnd == a || eh.TryEnd == b ||
                                            eh.HandlerStart == a || eh.HandlerStart == b || eh.HandlerEnd == a || eh.HandlerEnd == b ||
                                            eh.FilterStart == a || eh.FilterStart == b) { targeted = true; break; }
                                    }
                                }
                            }
                            catch { targeted = true; }

                            if (targeted)
                                continue;

                            a.OpCode = OpCodes.Nop;
                            a.Operand = null;
                            b.OpCode = OpCodes.Nop;
                            b.Operand = null;
                            removedLdPop++;
                            changed = true;

                            continue;
                        }

                        if ((IlHelpers.IsLdcI4(a, out _) || a.OpCode.Code == Code.Ldnull || a.OpCode.Code == Code.Ldstr) && b.OpCode.Code == Code.Pop)
                        {
                            if (a.OpCode.Code == Code.Ldstr)
                                continue;

                            a.OpCode = OpCodes.Nop;
                            a.Operand = null;
                            b.OpCode = OpCodes.Nop;
                            b.Operand = null;
                            removedLdPop++;
                            changed = true;
                        }
                    }

                    for (int i = 0; i < instrs.Count; i++)
                    {
                        if (instrs[i].OpCode.Code == Code.Sizeof)
                        {
                            string name = "";

                            try { name = (instrs[i].Operand as ITypeDefOrRef)?.FullName ?? ""; } catch { }

                            int size = 4;

                            if (name.Contains("Double") || name.Contains("Int64"))
                                size = 8;
                            else if (name.Contains("Byte") || name.Contains("Boolean"))
                                size = 1;
                            else if (name.Contains("Char") || name.Contains("Int16"))
                                size = 2;

                            instrs[i].OpCode = OpCodes.Ldc_I4;
                            instrs[i].Operand = size;
                            foldedSizeof++;
                            changed = true;
                        }
                    }

                    for (int i = 0; i < instrs.Count - 1; i++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[i], out int zero) && zero == 0)
                        {
                            var next = instrs[i + 1].OpCode.Code;

                            if (next == Code.Add || next == Code.Sub || next == Code.Xor || next == Code.Shl || next == Code.Shr || next == Code.Shr_Un)
                            {
                                instrs[i].OpCode = OpCodes.Nop;
                                instrs[i].Operand = null;
                                instrs[i + 1].OpCode = OpCodes.Nop;
                                instrs[i + 1].Operand = null;
                                removedConfuse++;
                                changed = true;
                            }
                        }
                    }

                    for (int i = 0; i < instrs.Count - 2; i++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[i], out int a) && IlHelpers.IsLdcI4(instrs[i + 1], out int b))
                        {
                            var oc = instrs[i + 2].OpCode.Code;
                            int? result = null;

                            switch (oc)
                            {
                                case Code.Add: result = unchecked(a + b); break;
                                case Code.Sub: result = unchecked(a - b); break;
                                case Code.Mul: result = unchecked(a * b); break;
                                case Code.And: result = a & b; break;
                                case Code.Or: result = a | b; break;
                                case Code.Xor: result = a ^ b; break;
                                case Code.Shl: result = a << (b & 31); break;
                                case Code.Shr: result = a >> (b & 31); break;
                                case Code.Shr_Un: result = unchecked((int)((uint)a >> (b & 31))); break;
                                case Code.Div: if (b != 0) result = a / b; break;
                                case Code.Rem: if (b != 0) result = a % b; break;
                                case Code.Rem_Un: if (b != 0) result = unchecked((int)((uint)a % (uint)b)); break;
                                case Code.Ceq: result = (a == b) ? 1 : 0; break;
                                case Code.Cgt: result = (a > b) ? 1 : 0; break;
                                case Code.Cgt_Un: result = (unchecked((uint)a) > unchecked((uint)b)) ? 1 : 0; break;
                                case Code.Clt: result = (a < b) ? 1 : 0; break;
                                case Code.Clt_Un: result = (unchecked((uint)a) < unchecked((uint)b)) ? 1 : 0; break;
                            }

                            if (result.HasValue)
                            {
                                instrs[i].OpCode = OpCodes.Ldc_I4;
                                instrs[i].Operand = result.Value;
                                instrs[i + 1].OpCode = OpCodes.Nop;
                                instrs[i + 1].Operand = null;
                                instrs[i + 2].OpCode = OpCodes.Nop;
                                instrs[i + 2].Operand = null;
                                foldedConst++;
                                changed = true;
                            }
                        }
                    }

                    for (int i = 0; i < instrs.Count - 1; i++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[i], out int cv) && instrs[i + 1].OpCode.Code == Code.Switch)
                        {
                            var list = instrs[i + 1].Operand as IList<Instruction>;

                            if (list == null || list.Count == 0 || list.Count > 256)
                                continue;

                            bool targeted = false;

                            try
                            {
                                foreach (var q in instrs)
                                {
                                    if (q == instrs[i + 1])
                                        continue;

                                    if (q.Operand is Instruction ti && ti == instrs[i]) { targeted = true; break; }
                                    if (q.Operand is IList<Instruction> ll && ll.Contains(instrs[i])) { targeted = true; break; }
                                }
                            }
                            catch { targeted = true; }

                            if (targeted)
                                continue;

                            int index = (int)((uint)cv % (uint)list.Count);

                            instrs[i].OpCode = OpCodes.Nop;
                            instrs[i].Operand = null;

                            if (index >= 0 && index < list.Count)
                            {
                                instrs[i + 1].OpCode = OpCodes.Br;
                                instrs[i + 1].Operand = list[index];
                            }
                            else
                            {
                                instrs[i + 1].OpCode = OpCodes.Nop;
                                instrs[i + 1].Operand = null;
                            }

                            foldedBranch++;
                            changed = true;
                        }
                    }

                    for (int i = 0; i < instrs.Count - 1; i++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[i], out int cv))
                        {
                            var nc = instrs[i + 1].OpCode.Code;
                            bool isTrue = cv != 0;

                            if (nc == Code.Brtrue || nc == Code.Brtrue_S)
                            {
                                if (isTrue)
                                {
                                    var target = instrs[i + 1].Operand as Instruction;

                                    instrs[i].OpCode = OpCodes.Nop;
                                    instrs[i].Operand = null;
                                    instrs[i + 1].OpCode = OpCodes.Br;
                                    instrs[i + 1].Operand = target;
                                }
                                else
                                {
                                    instrs[i].OpCode = OpCodes.Nop;
                                    instrs[i].Operand = null;
                                    instrs[i + 1].OpCode = OpCodes.Nop;
                                    instrs[i + 1].Operand = null;
                                }

                                foldedBranch++;
                                changed = true;
                            }
                            else if (nc == Code.Brfalse || nc == Code.Brfalse_S)
                            {
                                if (!isTrue)
                                {
                                    var target = instrs[i + 1].Operand as Instruction;

                                    instrs[i].OpCode = OpCodes.Nop;
                                    instrs[i].Operand = null;
                                    instrs[i + 1].OpCode = OpCodes.Br;
                                    instrs[i + 1].Operand = target;
                                }
                                else
                                {
                                    instrs[i].OpCode = OpCodes.Nop;
                                    instrs[i].Operand = null;
                                    instrs[i + 1].OpCode = OpCodes.Nop;
                                    instrs[i + 1].Operand = null;
                                }

                                foldedBranch++;
                                changed = true;
                            }
                        }
                    }

                    for (int i = 0; i < instrs.Count - 2; i++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[i], out int a) && IlHelpers.IsLdcI4(instrs[i + 1], out int b))
                        {
                            var oc = instrs[i + 2].OpCode.Code;
                            bool? take = null;

                            switch (oc)
                            {
                                case Code.Beq:
                                case Code.Beq_S: take = (a == b); break;
                                case Code.Bne_Un:
                                case Code.Bne_Un_S: take = (a != b); break;
                                case Code.Bge:
                                case Code.Bge_S: take = (a >= b); break;
                                case Code.Bgt:
                                case Code.Bgt_S: take = (a > b); break;
                                case Code.Ble:
                                case Code.Ble_S: take = (a <= b); break;
                                case Code.Blt:
                                case Code.Blt_S: take = (a < b); break;
                                case Code.Bge_Un:
                                case Code.Bge_Un_S: take = (unchecked((uint)a) >= unchecked((uint)b)); break;
                                case Code.Bgt_Un:
                                case Code.Bgt_Un_S: take = (unchecked((uint)a) > unchecked((uint)b)); break;
                                case Code.Ble_Un:
                                case Code.Ble_Un_S: take = (unchecked((uint)a) <= unchecked((uint)b)); break;
                                case Code.Blt_Un:
                                case Code.Blt_Un_S: take = (unchecked((uint)a) < unchecked((uint)b)); break;
                            }

                            if (take.HasValue)
                            {
                                var target = instrs[i + 2].Operand as Instruction;

                                instrs[i].OpCode = OpCodes.Nop;
                                instrs[i].Operand = null;
                                instrs[i + 1].OpCode = OpCodes.Nop;
                                instrs[i + 1].Operand = null;

                                if (take.Value)
                                {
                                    instrs[i + 2].OpCode = OpCodes.Br;
                                    instrs[i + 2].Operand = target;
                                }
                                else
                                {
                                    instrs[i + 2].OpCode = OpCodes.Nop;
                                    instrs[i + 2].Operand = null;
                                }

                                foldedBranch++;
                                changed = true;
                            }
                        }
                    }

                    for (int i = 0; i < instrs.Count - 1; i++)
                    {
                        if (instrs[i].OpCode.Code == Code.Br || instrs[i].OpCode.Code == Code.Br_S)
                        {
                            var target = instrs[i].Operand as Instruction;

                            if (target != null)
                            {
                                int ti = -1;

                                for (int k = 0; k < instrs.Count; k++)
                                {
                                    if (instrs[k] == target) { ti = k; break; }
                                }

                                int next = i + 1;

                                while (next < instrs.Count && instrs[next].OpCode.Code == Code.Nop)
                                    next++;

                                if (ti == next || ti == i + 1)
                                {
                                    instrs[i].OpCode = OpCodes.Nop;
                                    instrs[i].Operand = null;
                                    changed = true;
                                }
                            }
                        }
                    }
                }
            }

            if (!changed)
                break;
        }

        HydraLogger.Stats(
            ("assert", removedAssert.ToString()),
            ("ldpop", removedLdPop.ToString()),
            ("sizeof", foldedSizeof.ToString()),
            ("confuse", removedConfuse.ToString()),
            ("const", foldedConst.ToString()),
            ("branch", foldedBranch.ToString()),
            ("nops", removedNops.ToString()),
            ("deadstore", removedDeadStore.ToString()));
    }
}
