using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.ControlFlow;

internal static class HeaderOnlyCollapser
{
    public static void Execute(DeobfuscationContext context)
    {
        int collapsed = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods.ToList())
            {
                if (!method.HasBody)
                    continue;

                try
                {
                    for (int iter = 0; iter < 3; iter++)
                    {
                        if (!TryCollapseOne(method))
                            break;

                        collapsed++;

                        try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }
                    }
                }
                catch { }
            }
        }

        HydraLogger.Success($"Header-only collapsed={collapsed}");
    }

    private static bool TryCollapseOne(MethodDef method)
    {
        try
        {
            var instrs = method.Body.Instructions;
            var positions = new Dictionary<Instruction, int>();

            for (int i = 0; i < instrs.Count; i++)
                positions[instrs[i]] = i;

            for (int sw = 0; sw < instrs.Count; sw++)
            {
                if (instrs[sw].OpCode.Code != Code.Switch)
                    continue;

                var list = instrs[sw].Operand as IList<Instruction>;

                if (list == null || list.Count < 3)
                    continue;

                if (sw < 2)
                    continue;

                int r = sw - 1;

                while (r >= 0 && instrs[r].OpCode.Code == Code.Nop)
                    r--;

                if (r < 0)
                    continue;

                var rc = instrs[r].OpCode.Code;

                if (rc != Code.Rem && rc != Code.Rem_Un)
                    continue;

                int nIndex = r - 1;

                while (nIndex >= 0 && instrs[nIndex].OpCode.Code == Code.Nop)
                    nIndex--;

                if (nIndex < 0 || !IlHelpers.IsLdcI4(instrs[nIndex], out int div) || div != list.Count)
                    continue;

                for (int candidate = System.Math.Max(0, sw - 14); candidate <= sw - 2; candidate++)
                {
                    var hcode = instrs[candidate].OpCode.Code;

                    if (hcode != Code.Dup && !IlHelpers.IsLdcI4(instrs[candidate], out _))
                        continue;

                    if (!StateDispatcherRemover.IsPureHeader(method, instrs, candidate, sw))
                        continue;

                    int pp = candidate - 1;

                    while (pp >= 0 && instrs[pp].OpCode.Code == Code.Nop)
                        pp--;

                    int seed = 0;
                    bool hasSeed = pp >= 0 && IlHelpers.IsLdcI4(instrs[pp], out seed);

                    if (EmulateSpan(method, instrs, candidate, sw, System.Array.Empty<int>(), out int keyB))
                    {
                        if (TryApply(method, instrs, candidate, pp, false, sw, list, keyB))
                            return true;

                        continue;
                    }

                    if (hasSeed)
                    {
                        if (IsTarget(method, instrs, pp))
                            continue;

                        if (EmulateSpan(method, instrs, candidate, sw, new int[] { seed }, out int keyA))
                        {
                            if (TryApply(method, instrs, candidate, pp, true, sw, list, keyA))
                                return true;
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool EmulateSpan(MethodDef method, IList<Instruction> instrs, int start, int switchIndex, int[] incoming, out int key)
    {
        key = 0;

        try
        {
            var stack = new Stack<int>();

            foreach (int v in incoming)
                stack.Push(v);

            var locals = new Dictionary<int, int>();

            for (int k = start; k <= switchIndex; k++)
            {
                var ins = instrs[k];
                var code = ins.OpCode.Code;

                if (code == Code.Nop)
                    continue;

                if (code == Code.Br || code == Code.Br_S)
                    continue;

                if (IlHelpers.IsLdcI4(ins, out int lv)) { stack.Push(lv); continue; }
                if (code == Code.Dup) { if (stack.Count < 1) return false; stack.Push(stack.Peek()); continue; }
                if (code == Code.Pop) { if (stack.Count < 1) return false; stack.Pop(); continue; }

                if (code == Code.Stloc || code == Code.Stloc_S || code == Code.Stloc_0 || code == Code.Stloc_1 || code == Code.Stloc_2 || code == Code.Stloc_3)
                {
                    if (stack.Count < 1)
                        return false;

                    int li = IlHelpers.GetLocalIndex(ins, method.Body);
                    locals[li] = stack.Pop();

                    continue;
                }

                if (code == Code.Add || code == Code.Sub || code == Code.Xor || code == Code.And || code == Code.Or || code == Code.Mul ||
                    code == Code.Shl || code == Code.Shr || code == Code.Shr_Un || code == Code.Div || code == Code.Div_Un || code == Code.Rem || code == Code.Rem_Un ||
                    code == Code.Neg || code == Code.Not)
                {
                    if (code == Code.Neg || code == Code.Not)
                    {
                        if (stack.Count < 1)
                            return false;

                        int av = stack.Pop();

                        stack.Push(code == Code.Neg ? unchecked(-av) : ~av);

                        continue;
                    }

                    if (stack.Count < 2)
                        return false;

                    int bv = stack.Pop();
                    int av2 = stack.Pop();
                    int rr = 0;
                    bool ok = true;

                    switch (code)
                    {
                        case Code.Add: rr = unchecked(av2 + bv); break;
                        case Code.Sub: rr = unchecked(av2 - bv); break;
                        case Code.Xor: rr = av2 ^ bv; break;
                        case Code.And: rr = av2 & bv; break;
                        case Code.Or: rr = av2 | bv; break;
                        case Code.Mul: rr = unchecked(av2 * bv); break;
                        case Code.Shl: rr = av2 << (bv & 31); break;
                        case Code.Shr: rr = av2 >> (bv & 31); break;
                        case Code.Shr_Un: rr = unchecked((int)((uint)av2 >> (bv & 31))); break;
                        case Code.Div: case Code.Div_Un: if (bv == 0) ok = false; else rr = av2 / bv; break;
                        case Code.Rem: if (bv == 0) ok = false; else rr = av2 % bv; break;
                        case Code.Rem_Un: if (bv == 0) ok = false; else rr = unchecked((int)((uint)av2 % (uint)bv)); break;
                    }

                    if (!ok)
                        return false;

                    stack.Push(rr);

                    continue;
                }

                if (code == Code.Switch)
                    continue;

                return false;
            }

            if (stack.Count < 1)
                return false;

            key = stack.Pop();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTarget(MethodDef method, IList<Instruction> instrs, int index)
    {
        try
        {
            Instruction wanted = instrs[index];

            for (int k = 0; k < instrs.Count; k++)
            {
                if (k == index)
                    continue;

                var q = instrs[k];

                if (q.Operand is Instruction single && single == wanted)
                    return true;

                if (q.Operand is IList<Instruction> list && list.Contains(wanted))
                    return true;
            }

            foreach (var eh in method.Body.ExceptionHandlers)
            {
                if (eh.TryStart == wanted || eh.TryEnd == wanted || eh.HandlerStart == wanted || eh.HandlerEnd == wanted || eh.FilterStart == wanted)
                    return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryApply(MethodDef method, IList<Instruction> instrs, int header, int seedIndex, bool useSeed, int switchIndex, IList<Instruction> targets, int key)
    {
        try
        {
            int headerEnd = switchIndex + 1;

            if (headerEnd >= instrs.Count || (instrs[headerEnd].OpCode.Code != Code.Br && instrs[headerEnd].OpCode.Code != Code.Br_S))
                headerEnd = switchIndex;

            int unitStart = useSeed ? seedIndex : header;

            for (int k = 0; k < instrs.Count; k++)
            {
                if (k >= unitStart && k <= headerEnd)
                    continue;

                var q = instrs[k];
                bool hits = false;

                if (q.Operand is Instruction single)
                {
                    for (int u = unitStart; u <= headerEnd; u++)
                    {
                        if (instrs[u] == single) { hits = true; break; }
                    }
                }
                else if (q.Operand is IList<Instruction> list)
                {
                    for (int u = unitStart; u <= headerEnd && !hits; u++)
                    {
                        if (list.Contains(instrs[u]))
                            hits = true;
                    }
                }

                if (hits)
                    return false;
            }

            foreach (var eh in method.Body.ExceptionHandlers)
            {
                Instruction?[] bounds = new Instruction?[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart };

                foreach (var bound in bounds)
                {
                    if (bound == null)
                        continue;

                    for (int u = unitStart; u <= headerEnd; u++)
                    {
                        if (instrs[u] == bound)
                            return false;
                    }
                }
            }

            var written = new HashSet<int>();

            for (int k = header; k <= switchIndex; k++)
            {
                var cc = instrs[k].OpCode.Code;

                if (cc == Code.Stloc || cc == Code.Stloc_S || cc == Code.Stloc_0 || cc == Code.Stloc_1 || cc == Code.Stloc_2 || cc == Code.Stloc_3)
                {
                    try { written.Add(IlHelpers.GetLocalIndex(instrs[k], method.Body)); } catch { }
                }
            }

            for (int k = 0; k < instrs.Count; k++)
            {
                if (k >= unitStart && k <= headerEnd)
                    continue;

                var cc = instrs[k].OpCode.Code;

                if (cc == Code.Ldloc || cc == Code.Ldloc_S || cc == Code.Ldloc_0 || cc == Code.Ldloc_1 || cc == Code.Ldloc_2 || cc == Code.Ldloc_3 ||
                    cc == Code.Ldloca || cc == Code.Ldloca_S)
                {
                    int li = -1;

                    try { li = IlHelpers.GetLocalIndex(instrs[k], method.Body); } catch { }

                    if (written.Contains(li))
                        return false;
                }
            }

            int fc = (int)((uint)key % (uint)targets.Count);
            Instruction first;

            if (fc >= 0 && fc < targets.Count)
            {
                first = targets[fc];
            }
            else
            {
                int after = switchIndex + 1;

                while (after < instrs.Count && instrs[after].OpCode.Code == Code.Nop)
                    after++;

                if (after >= instrs.Count)
                    return false;

                first = instrs[after];
            }

            for (int k = header + 1; k <= headerEnd; k++)
            {
                instrs[k].OpCode = OpCodes.Nop;
                instrs[k].Operand = null;
            }

            instrs[header].OpCode = OpCodes.Br;
            instrs[header].Operand = first;

            if (useSeed)
            {
                instrs[seedIndex].OpCode = OpCodes.Nop;
                instrs[seedIndex].Operand = null;
            }

            try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }

            DeadCodeEliminator.RemoveUnreachable(method);

            try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
