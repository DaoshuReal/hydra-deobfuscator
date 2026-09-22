using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.ControlFlow;

internal static class StateDispatcherRemover
{
    public static void Execute(DeobfuscationContext context)
    {
        context.DispatcherFailReasons.Clear();
        context.DispatcherFailSamples.Clear();
        context.DispatcherDoneSamples.Clear();
        context.DispatcherDebugDumps = 0;
        context.DispatcherFileDumps = 0;

        try { System.IO.File.Delete("dispfail.txt"); } catch { }

        int done = 0;
        int skip = 0;
        int converted = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods.ToList())
            {
                if (!method.HasBody)
                    continue;

                try
                {
                    int count = TryRemoveDispatcher(method, context);

                    if (count > 0)
                    {
                        done++;
                        converted += count;

                        if (context.DispatcherDoneSamples.Count < 25)
                            context.DispatcherDoneSamples.Add($"{type.Name}::{method.Name} edges={count}");
                    }
                    else
                    {
                        skip++;
                    }
                }
                catch
                {
                    skip++;
                }
            }
        }

        HydraLogger.Success($"State dispatchers done={done} skip={skip} converted={converted} demoted={context.DemotedPathCount}");

        foreach (var pair in context.DispatcherFailReasons.OrderByDescending(x => x.Value).Take(12))
            HydraLogger.Detail($"[skip] {pair.Key}: {pair.Value}");

        foreach (var sample in context.DispatcherFailSamples.Take(20))
            HydraLogger.Sample(sample);

        foreach (var sample in context.DispatcherDoneSamples.Take(15))
            HydraLogger.Detail($"[done] {sample}");
    }

    public static int TryRemoveDispatcher(MethodDef method, DeobfuscationContext context)
    {
        try
        {
            if (method == context.DecryptCaesarDef || method == context.DecryptBase64Def || method == context.GetXorKeyDef)
                return 0;

            if (context.IntProxyValues.ContainsKey(method))
                return 0;

            if (context.StringProxyValues.ContainsKey(method))
                return 0;

            if (context.DynamicPayloads.ContainsKey(method))
                return 0;
        }
        catch { }

        var instrs = method.Body.Instructions;

        if (instrs.Count < 20)
            return 0;

        int converted = 0;

        for (int iter = 0; iter < 4; iter++)
        {
            if (!TryRemoveOne(method, context, out int count))
                break;

            converted += count;

            try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }
        }

        return converted;
    }

    public static bool TryRemoveOne(MethodDef method, DeobfuscationContext context, out int converted)
    {
        converted = 0;

        var instrs = method.Body.Instructions;
        var positions = new Dictionary<Instruction, int>();

        for (int i = 0; i < instrs.Count; i++)
            positions[instrs[i]] = i;

        var switches = new List<int>();

        for (int i = 0; i < instrs.Count; i++)
        {
            if (instrs[i].OpCode.Code != Code.Switch)
                continue;

            var list = instrs[i].Operand as IList<Instruction>;

            if (list == null || list.Count < 3)
                continue;

            if (i < 2)
                continue;

            var prev = instrs[i - 1].OpCode.Code;

            if (prev != Code.Rem && prev != Code.Rem_Un)
                continue;

            if (!IlHelpers.IsLdcI4(instrs[i - 2], out int div))
                continue;

            if (div != list.Count)
                continue;

            switches.Add(i);
        }

        if (switches.Count == 0)
            return false;

        int bestSwitch = -1;
        int bestHeader = -1;
        int bestCount = 0;

        foreach (int sw in switches)
        {
            var freq = new Dictionary<int, List<int>>();

            for (int i = 0; i < instrs.Count; i++)
            {
                var code = instrs[i].OpCode.Code;

                if (code != Code.Br && code != Code.Br_S)
                    continue;

                if (!(instrs[i].Operand is Instruction target))
                    continue;

                if (!positions.TryGetValue(target, out int ti))
                    continue;

                if (ti >= sw)
                    continue;

                if (ti <= sw - 40)
                    continue;

                if (!freq.TryGetValue(ti, out var list))
                    freq[ti] = list = new List<int>();

                list.Add(i);
            }

            foreach (var pair in freq)
            {
                if (pair.Value.Count <= bestCount)
                    continue;

                if (!IsPureHeader(method, instrs, pair.Key, sw))
                    continue;

                bestCount = pair.Value.Count;
                bestSwitch = sw;
                bestHeader = pair.Key;
            }
        }

        if (bestSwitch < 0)
        {
            Skip(context, "no-header", method);
            return false;
        }

        int switchIndex = bestSwitch;
        int headerIndex = bestHeader;
        var targets = (instrs[switchIndex].Operand as IList<Instruction>)!;
        int count = targets.Count;
        var targetIndex = new List<int>();

        foreach (var item in targets)
        {
            if (!positions.TryGetValue(item, out int ti))
            {
                Skip(context, "bad-target", null);
                return false;
            }

            targetIndex.Add(ti);
        }

        int stateVar = -1;
        int storeIndex = switchIndex - 2 - 1;

        while (storeIndex >= 0 && instrs[storeIndex].OpCode.Code == Code.Nop)
            storeIndex--;

        if (storeIndex >= 0)
        {
            var cc = instrs[storeIndex].OpCode.Code;

            if (cc == Code.Stloc || cc == Code.Stloc_S || cc == Code.Stloc_0 || cc == Code.Stloc_1 || cc == Code.Stloc_2 || cc == Code.Stloc_3)
                stateVar = IlHelpers.GetLocalIndex(instrs[storeIndex], method.Body);
        }

        if (stateVar >= 0)
        {
            string headerRegion = RegionHelpers.Key(method, headerIndex);
            var reachable = RegionHelpers.PlainReachable(method);
            var others = OtherHeaderRanges(method, instrs, positions, switches, switchIndex, stateVar);

            for (int i = 0; i < instrs.Count; i++)
            {
                if (i >= headerIndex && i <= switchIndex)
                    continue;

                bool inOther = false;

                foreach (var r in others)
                {
                    if (i >= r.Item1 && i <= r.Item2) { inOther = true; break; }
                }

                if (inOther)
                    continue;

                var cc = instrs[i].OpCode.Code;

                if (cc == Code.Stloc || cc == Code.Stloc_S || cc == Code.Stloc_0 || cc == Code.Stloc_1 || cc == Code.Stloc_2 || cc == Code.Stloc_3)
                {
                    int li = -1;

                    try { li = IlHelpers.GetLocalIndex(instrs[i], method.Body); } catch { }

                    if (li != stateVar)
                        continue;

                    if (!reachable.Contains(i))
                        continue;

                    string rk;

                    try { rk = RegionHelpers.Key(method, i); } catch { continue; }

                    if (rk != headerRegion)
                        continue;

                    Skip(context, "statevar-reused", method);

                    return false;
                }
            }
        }

        int initState = 0;
        bool haveInit = false;

        {
            int p = headerIndex - 1;

            while (p >= 0 && instrs[p].OpCode.Code == Code.Nop)
                p--;

            if (p >= 0 && IlHelpers.IsLdcI4(instrs[p], out int piv))
            {
                bool prologueOk = true;

                for (int q = p + 1; q < headerIndex; q++)
                {
                    var qc = instrs[q].OpCode.Code;

                    if (qc != Code.Nop && qc != Code.Br && qc != Code.Br_S) { prologueOk = false; break; }
                }

                if (prologueOk)
                {
                    initState = piv;
                    haveInit = true;
                }
            }

            if (!haveInit && TryEmulateHeader(method, headerIndex, switchIndex, new List<int>(), out int st, out _))
            {
                initState = st;
                haveInit = true;
            }

            if (!haveInit && TryEmulateHeader(method, 0, switchIndex, new List<int>(), out int st2, out _))
            {
                initState = st2;
                haveInit = true;
            }

            if (!haveInit)
                Skip(context, "no-init-entrywalk", method);
        }

        bool fullOk = haveInit && RegionHelpers.SameRegion(method, headerIndex, switchIndex, targetIndex);

        if (!fullOk && haveInit)
            Skip(context, "eh-region", null);

        if (!StatePropagator.Propagate(method, headerIndex, switchIndex, targets, count, stateVar, initState, haveInit, context, out var transitions, out var siteStates, out string propFail))
        {
            Skip(context, "prop-" + propFail, method);
            return false;
        }

        if (transitions.Count == 0)
        {
            Skip(context, "prop-empty", method);
            return false;
        }

        var reachableBranches = RegionHelpers.ReachableBranchesToHeader(method, headerIndex);
        var missing = new List<int>();

        foreach (int edge in reachableBranches)
        {
            if (!transitions.ContainsKey(edge))
                missing.Add(edge);
        }

        foreach (int edge in transitions.Keys)
        {
            if (!reachableBranches.Contains(edge))
            {
                Skip(context, "coverage-extra", null);
                return false;
            }
        }

        if (fullOk)
        {
            if (missing.Count > 0)
            {
                Skip(context, "coverage-missing", method);
                DumpToFile(method, context, "coverage-missing", headerIndex, switchIndex, count, stateVar, initState, missing);

                return false;
            }

            var ordered = transitions.Keys.OrderByDescending(x => x).ToList();

            foreach (int edge in ordered)
            {
                int start = FindUpdateStart(method, edge, stateVar);

                if (!siteStates.TryGetValue(edge, out int site))
                {
                    Skip(context, "verify-nosite", null);
                    return false;
                }

                if (!TryComputeNext(method, start, edge, site, stateVar, out int check) || check != transitions[edge])
                {
                    Skip(context, "verify-mismatch", null);
                    return false;
                }
            }

            foreach (int edge in ordered)
            {
                ConvertEdge(method, instrs, targets, switchIndex, count, stateVar, edge, transitions[edge]);
                converted++;
            }
        }
        else
        {
            var ordered = transitions.Keys.OrderByDescending(x => x).ToList();
            int skipped = 0;

            foreach (int edge in ordered)
            {
                if (!reachableBranches.Contains(edge))
                    continue;

                int start = FindUpdateStart(method, edge, stateVar);

                if (!siteStates.TryGetValue(edge, out int site)) { skipped++; continue; }
                if (!TryComputeNext(method, start, edge, site, stateVar, out int check) || check != transitions[edge]) { skipped++; continue; }
                if (!EdgeInSameRegion(method, instrs, targets, switchIndex, count, edge, transitions[edge])) { skipped++; continue; }

                ConvertEdge(method, instrs, targets, switchIndex, count, stateVar, edge, transitions[edge]);
                converted++;
            }

            if (converted == 0)
            {
                Skip(context, fullOk ? "verify-mismatch" : (haveInit ? "eh-region-noedge" : "noinit-noedge"), method);
                return false;
            }
        }

        if (fullOk)
        {
            int fc = (int)((uint)initState % (uint)count);
            Instruction first;

            if (fc >= 0 && fc < targets.Count)
                first = targets[fc];
            else
            {
                int after = switchIndex + 1;

                while (after < instrs.Count && instrs[after].OpCode.Code == Code.Nop)
                    after++;

                first = instrs[after];
            }

            int headerEnd = switchIndex + 1;

            if (headerEnd >= instrs.Count || (instrs[headerEnd].OpCode.Code != Code.Br && instrs[headerEnd].OpCode.Code != Code.Br_S))
                headerEnd = switchIndex;

            for (int p = headerIndex + 1; p <= headerEnd; p++)
            {
                instrs[p].OpCode = OpCodes.Nop;
                instrs[p].Operand = null;
            }

            instrs[headerIndex].OpCode = OpCodes.Br;
            instrs[headerIndex].Operand = first;

            try
            {
                var allTargets = new HashSet<Instruction>();

                foreach (var ins in instrs)
                {
                    if (ins.Operand is Instruction single)
                        allTargets.Add(single);
                    else if (ins.Operand is IList<Instruction> list)
                        foreach (var x in list)
                            allTargets.Add(x);
                }

                foreach (var eh in method.Body.ExceptionHandlers)
                {
                    if (eh.TryStart != null) allTargets.Add(eh.TryStart);
                    if (eh.TryEnd != null) allTargets.Add(eh.TryEnd);
                    if (eh.HandlerStart != null) allTargets.Add(eh.HandlerStart);
                    if (eh.HandlerEnd != null) allTargets.Add(eh.HandlerEnd);
                    if (eh.FilterStart != null) allTargets.Add(eh.FilterStart);
                }

                int p = headerIndex - 1;

                while (p >= 0)
                {
                    if (allTargets.Contains(instrs[p]))
                        break;

                    var cc = instrs[p].OpCode.Code;

                    if (cc == Code.Nop) { p--; continue; }

                    if (cc == Code.Br || cc == Code.Br_S)
                    {
                        var tt = instrs[p].Operand as Instruction;
                        int ni = p + 1;

                        while (ni < headerIndex && instrs[ni].OpCode.Code == Code.Nop)
                            ni++;

                        if (tt != null && ni < instrs.Count && tt == instrs[ni]) { p--; continue; }

                        break;
                    }

                    if (IlHelpers.IsLdcI4(instrs[p], out _))
                    {
                        instrs[p].OpCode = OpCodes.Nop;
                        instrs[p].Operand = null;
                    }

                    break;
                }
            }
            catch { }
        }

        try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }

        DeadCodeEliminator.RemoveUnreachable(method);

        try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }

        return true;
    }

    public static bool IsPureHeader(MethodDef method, IList<Instruction> instrs, int header, int switchIndex)
    {
        try
        {
            if (header < 0 || switchIndex >= instrs.Count || header >= switchIndex)
                return false;

            for (int i = header; i <= switchIndex; i++)
            {
                var code = instrs[i].OpCode.Code;

                switch (code)
                {
                    case Code.Nop:
                    case Code.Ldc_I4_M1: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2:
                    case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6:
                    case Code.Ldc_I4_7: case Code.Ldc_I4_8: case Code.Ldc_I4_S: case Code.Ldc_I4:
                    case Code.Dup: case Code.Pop:
                    case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                    case Code.Stloc_S: case Code.Stloc:
                    case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                    case Code.Ldloc_S: case Code.Ldloc:
                    case Code.Add: case Code.Sub: case Code.Xor: case Code.And: case Code.Or:
                    case Code.Mul: case Code.Shl: case Code.Shr: case Code.Shr_Un:
                    case Code.Div: case Code.Div_Un: case Code.Rem: case Code.Rem_Un:
                    case Code.Neg: case Code.Not:
                    case Code.Switch:
                        break;
                    case Code.Br: case Code.Br_S:
                        {
                            var target = instrs[i].Operand as Instruction;

                            if (target == null)
                                return false;

                            int next = i + 1;

                            while (next <= switchIndex && instrs[next].OpCode.Code == Code.Nop)
                                next++;

                            int ti = instrs.IndexOf(target);

                            if (ti != i + 1 && ti != next)
                                return false;

                            break;
                        }

                    default:
                        return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static List<Tuple<int, int>> OtherHeaderRanges(MethodDef method, IList<Instruction> instrs, Dictionary<Instruction, int> positions, List<int> switches, int skip, int wantVar)
    {
        var result = new List<Tuple<int, int>>();

        try
        {
            foreach (int s in switches)
            {
                if (s == skip)
                    continue;

                var list = instrs[s].Operand as IList<Instruction>;

                if (list == null)
                    continue;

                int p = s - 1;

                while (p >= 0 && instrs[p].OpCode.Code == Code.Nop)
                    p--;

                if (p < 0)
                    continue;

                var remCode = instrs[p].OpCode.Code;

                if (remCode != Code.Rem && remCode != Code.Rem_Un)
                    continue;

                p--;

                while (p >= 0 && instrs[p].OpCode.Code == Code.Nop)
                    p--;

                if (p < 0 || !IlHelpers.IsLdcI4(instrs[p], out int nv) || nv != list.Count)
                    continue;

                p--;

                while (p >= 0 && instrs[p].OpCode.Code == Code.Nop)
                    p--;

                if (p < 0)
                    continue;

                var stCode = instrs[p].OpCode.Code;

                if (stCode != Code.Stloc && stCode != Code.Stloc_S && stCode != Code.Stloc_0 && stCode != Code.Stloc_1 && stCode != Code.Stloc_2 && stCode != Code.Stloc_3)
                    continue;

                int li = -1;

                try { li = IlHelpers.GetLocalIndex(instrs[p], method.Body); } catch { continue; }

                if (li != wantVar)
                    continue;

                p--;

                while (p >= 0 && instrs[p].OpCode.Code == Code.Nop)
                    p--;

                if (p < 0 || instrs[p].OpCode.Code != Code.Dup)
                    continue;

                int start = p;
                int q = p - 1;
                int guard = 0;

                while (q >= 0 && guard < 8)
                {
                    var qc = instrs[q].OpCode.Code;

                    if (qc == Code.Nop) { q--; continue; }

                    if (IlHelpers.IsLdcI4(instrs[q], out _)) { start = q; q--; guard++; continue; }

                    if (qc == Code.Stloc || qc == Code.Stloc_S || qc == Code.Stloc_0 || qc == Code.Stloc_1 || qc == Code.Stloc_2 || qc == Code.Stloc_3)
                    {
                        int qli = -1;

                        try { qli = IlHelpers.GetLocalIndex(instrs[q], method.Body); } catch { break; }

                        if (qli != wantVar)
                            break;

                        start = q;
                        q--;
                        guard++;

                        continue;
                    }

                    break;
                }

                result.Add(Tuple.Create(start, s));
            }
        }
        catch { }

        return result;
    }

    public static void ConvertEdge(MethodDef method, IList<Instruction> instrs, IList<Instruction> targets, int switchIndex, int count, int stateVar, int edge, int next)
    {
        int nc = (int)((uint)next % (uint)count);
        Instruction dest;

        if (nc >= 0 && nc < targets.Count)
        {
            dest = targets[nc];
        }
        else
        {
            int after = switchIndex + 1;

            while (after < instrs.Count && instrs[after].OpCode.Code == Code.Nop)
                after++;

            if (after >= instrs.Count)
                return;

            dest = instrs[after];
        }

        int start = FindUpdateStart(method, edge, stateVar);

        for (int p = start; p < edge; p++)
        {
            instrs[p].OpCode = OpCodes.Nop;
            instrs[p].Operand = null;
        }

        instrs[edge].OpCode = OpCodes.Br;
        instrs[edge].Operand = dest;
    }

    public static bool EdgeInSameRegion(MethodDef method, IList<Instruction> instrs, IList<Instruction> targets, int switchIndex, int count, int edge, int next)
    {
        try
        {
            int nc = (int)((uint)next % (uint)count);
            int destIndex;

            if (nc >= 0 && nc < targets.Count)
            {
                var positions = new Dictionary<Instruction, int>();

                for (int i = 0; i < instrs.Count; i++)
                    positions[instrs[i]] = i;

                if (!positions.TryGetValue(targets[nc], out destIndex))
                    return false;
            }
            else
            {
                destIndex = switchIndex + 1;

                while (destIndex < instrs.Count && instrs[destIndex].OpCode.Code == Code.Nop)
                    destIndex++;

                if (destIndex >= instrs.Count)
                    return false;
            }

            string a;
            string b;

            try { a = RegionHelpers.Key(method, edge); } catch { return false; }
            try { b = RegionHelpers.Key(method, destIndex); } catch { return false; }

            if (a == "O?" || b == "O?")
                return false;

            return a == b;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryEmulateHeader(MethodDef method, int header, int switchIndex, List<int> incoming, out int stateBeforeRem, out int switchCase)
    {
        stateBeforeRem = 0;
        switchCase = -1;

        try
        {
            var instrs = method.Body.Instructions;
            var stack = new Stack<int>();

            foreach (int v in incoming)
                stack.Push(v);

            var locals = new Dictionary<int, int>();

            for (int i = header; i <= switchIndex; i++)
            {
                var ins = instrs[i];
                var code = ins.OpCode.Code;

                if (code == Code.Nop)
                    continue;

                if (code == Code.Br || code == Code.Br_S)
                {
                    var target = ins.Operand as Instruction;

                    if (target == null)
                        return false;

                    int ti = instrs.IndexOf(target);

                    if (ti < header || ti > switchIndex)
                        return false;

                    i = ti - 1;

                    continue;
                }

                if (IlHelpers.IsLdcI4(ins, out int lv)) { stack.Push(lv); continue; }
                if (code == Code.Dup) { if (stack.Count < 1) return false; stack.Push(stack.Peek()); continue; }
                if (code == Code.Pop) { if (stack.Count < 1) return false; stack.Pop(); continue; }

                if (code == Code.Stloc || code == Code.Stloc_S || code == Code.Stloc_0 || code == Code.Stloc_1 || code == Code.Stloc_2 || code == Code.Stloc_3)
                {
                    int li = IlHelpers.GetLocalIndex(ins, method.Body);

                    if (stack.Count < 1)
                        return false;

                    locals[li] = stack.Pop();

                    continue;
                }

                if (code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0 || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3)
                {
                    int li = IlHelpers.GetLocalIndex(ins, method.Body);

                    locals.TryGetValue(li, out int vv);
                    stack.Push(vv);

                    continue;
                }

                if (code == Code.Add) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(unchecked(aa + bb)); continue; }
                if (code == Code.Sub) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(unchecked(aa - bb)); continue; }
                if (code == Code.Xor) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa ^ bb); continue; }
                if (code == Code.And) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa & bb); continue; }
                if (code == Code.Or) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa | bb); continue; }
                if (code == Code.Mul) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(unchecked(aa * bb)); continue; }
                if (code == Code.Shl) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa << (bb & 31)); continue; }
                if (code == Code.Shr || code == Code.Shr_Un) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(code == Code.Shr ? aa >> (bb & 31) : unchecked((int)((uint)aa >> (bb & 31)))); continue; }

                if (code == Code.Rem || code == Code.Rem_Un)
                {
                    if (stack.Count < 2)
                        return false;

                    int bb = stack.Pop();
                    int aa = stack.Pop();

                    if (bb == 0)
                        return false;

                    int r = code == Code.Rem ? aa % bb : unchecked((int)((uint)aa % (uint)bb));

                    stateBeforeRem = aa;
                    switchCase = r;
                    stack.Push(r);

                    continue;
                }

                if (code == Code.Switch)
                    return true;

                return false;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static int FindUpdateStart(MethodDef method, int edge, int stateHint)
    {
        try
        {
            var instrs = method.Body.Instructions;
            int s = edge - 1;

            while (s >= 0 && instrs[s].OpCode.Code == Code.Nop)
                s--;

            while (s >= 0)
            {
                var code = instrs[s].OpCode.Code;

                if (code == Code.Nop) { s--; continue; }

                if (code == Code.Br || code == Code.Br_S)
                {
                    var target = instrs[s].Operand as Instruction;
                    int ti = instrs.IndexOf(target);
                    int ni = s + 1;

                    while (ni < instrs.Count && instrs[ni].OpCode.Code == Code.Nop)
                        ni++;

                    if (ti == s + 1 || ti == ni) { s--; continue; }

                    break;
                }

                if (IlHelpers.IsLdcI4(instrs[s], out _)) { s--; continue; }
                if (code == Code.Dup) { s--; continue; }

                if (code == Code.Add || code == Code.Sub || code == Code.Xor || code == Code.And || code == Code.Or || code == Code.Mul
                    || code == Code.Shl || code == Code.Shr || code == Code.Shr_Un || code == Code.Div || code == Code.Div_Un || code == Code.Rem || code == Code.Rem_Un
                    || code == Code.Neg || code == Code.Not) { s--; continue; }

                if (code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0 || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3)
                {
                    if (stateHint >= 0)
                    {
                        int li = -1;

                        try { li = IlHelpers.GetLocalIndex(instrs[s], method.Body); } catch { }

                        if (li != stateHint)
                            break;
                    }

                    s--;
                    continue;
                }

                if (code == Code.Stloc || code == Code.Stloc_S || code == Code.Stloc_0 || code == Code.Stloc_1 || code == Code.Stloc_2 || code == Code.Stloc_3)
                    break;

                break;
            }

            return s + 1;
        }
        catch
        {
            return edge;
        }
    }

    public static bool TryComputeNext(MethodDef method, int start, int edge, int current, int stateVar, out int next)
    {
        next = 0;

        try
        {
            var instrs = method.Body.Instructions;
            var stack = new Stack<int>();
            var locals = new Dictionary<int, int>();

            if (stateVar >= 0)
                locals[stateVar] = current;

            for (int i = start; i < edge; i++)
            {
                var ins = instrs[i];
                var code = ins.OpCode.Code;

                if (code == Code.Nop)
                    continue;

                if (code == Code.Br || code == Code.Br_S)
                    continue;

                if (IlHelpers.IsLdcI4(ins, out int lv)) { stack.Push(lv); continue; }
                if (code == Code.Dup) { if (stack.Count < 1) return false; stack.Push(stack.Peek()); continue; }
                if (code == Code.Pop) { if (stack.Count < 1) return false; stack.Pop(); continue; }

                if (code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0 || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3)
                {
                    int li = IlHelpers.GetLocalIndex(ins, method.Body);

                    if (li == stateVar && locals.TryGetValue(li, out int sv))
                        stack.Push(sv);
                    else if (locals.TryGetValue(li, out int vv))
                        stack.Push(vv);
                    else
                        return false;

                    continue;
                }

                if (code == Code.Add) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(unchecked(aa + bb)); continue; }
                if (code == Code.Sub) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(unchecked(aa - bb)); continue; }
                if (code == Code.Xor) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa ^ bb); continue; }
                if (code == Code.And) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa & bb); continue; }
                if (code == Code.Or) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa | bb); continue; }
                if (code == Code.Mul) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(unchecked(aa * bb)); continue; }
                if (code == Code.Shl) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa << (bb & 31)); continue; }
                if (code == Code.Shr) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(aa >> (bb & 31)); continue; }
                if (code == Code.Shr_Un) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); stack.Push(unchecked((int)((uint)aa >> (bb & 31)))); continue; }
                if (code == Code.Div) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); if (bb == 0) return false; stack.Push(aa / bb); continue; }
                if (code == Code.Div_Un) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); if (bb == 0) return false; stack.Push(unchecked((int)((uint)aa / (uint)bb))); continue; }
                if (code == Code.Rem) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); if (bb == 0) return false; stack.Push(aa % bb); continue; }
                if (code == Code.Rem_Un) { if (stack.Count < 2) return false; int bb = stack.Pop(), aa = stack.Pop(); if (bb == 0) return false; stack.Push(unchecked((int)((uint)aa % (uint)bb))); continue; }
                if (code == Code.Neg) { if (stack.Count < 1) return false; stack.Push(unchecked(-stack.Pop())); continue; }
                if (code == Code.Not) { if (stack.Count < 1) return false; stack.Push(~stack.Pop()); continue; }

                return false;
            }

            if (stack.Count != 1)
                return false;

            next = stack.Pop();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Skip(DeobfuscationContext context, string reason, MethodDef? method)
    {
        try
        {
            if (context.DispatcherFailReasons.TryGetValue(reason, out int n))
                context.DispatcherFailReasons[reason] = n + 1;
            else
                context.DispatcherFailReasons[reason] = 1;

            if (method != null && context.DispatcherFailSamples.Count < 400 && (reason == "prop-empty" || reason == "coverage-missing" || reason.StartsWith("prop-") || reason == "verify-mismatch" || reason == "verify-nosite" || reason == "eh-region" || reason == "no-init" || reason == "no-header" || reason == "statevar-reused"))
                context.DispatcherFailSamples.Add($"{reason}: {method.DeclaringType.Name}::{method.Name}");
        }
        catch { }
    }

    private static void DumpToFile(MethodDef method, DeobfuscationContext context, string why, int header, int switchIndex, int count, int stateVar, int initState, List<int>? missing)
    {
        try
        {
            if (context.DispatcherFileDumps >= 40)
                return;

            context.DispatcherFileDumps++;

            var instrs = method.Body.Instructions;
            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"=== {why} {method.DeclaringType.FullName}::{method.Name} h={header} sw={switchIndex} N={count} svar={stateVar} init={initState} count={instrs.Count}");

            int lo = Math.Max(0, header - 6);
            int hi = Math.Min(instrs.Count, switchIndex + 3);

            sb.AppendLine("-- header --");

            for (int i = lo; i < hi; i++)
                sb.AppendLine($"  H[{i}] {instrs[i].OpCode.Code} {IlHelpers.ShortOperand(instrs[i], instrs)}");

            if (missing != null)
            {
                foreach (int edge in missing.Take(4))
                {
                    sb.AppendLine($"-- missing {edge} --");

                    int lo2 = Math.Max(0, edge - 10);
                    int hi2 = Math.Min(instrs.Count, edge + 1);

                    for (int i = lo2; i < hi2; i++)
                        sb.AppendLine($"  B[{i}] {instrs[i].OpCode.Code} {IlHelpers.ShortOperand(instrs[i], instrs)}");
                }
            }

            System.IO.File.AppendAllText("dispfail.txt", sb.ToString());
        }
        catch { }
    }
}
