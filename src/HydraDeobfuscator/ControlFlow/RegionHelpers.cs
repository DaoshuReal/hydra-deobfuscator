using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HydraDeobfuscator.ControlFlow;

internal static class RegionHelpers
{
    public static string Key(MethodDef method, int index)
    {
        try
        {
            var instrs = method.Body.Instructions;
            uint offset = instrs[index].Offset;
            ExceptionHandler? best = null;
            uint bestSize = uint.MaxValue;
            bool bestIsHandler = false;
            var handlers = method.Body.ExceptionHandlers;

            for (int e = 0; e < handlers.Count; e++)
            {
                var eh = handlers[e];
                uint start = eh.TryStart?.Offset ?? uint.MaxValue;
                uint end = eh.TryEnd?.Offset ?? uint.MaxValue;

                if (eh.TryStart != null && offset >= start && offset < end)
                {
                    uint size = end - start;

                    if (size < bestSize)
                    {
                        bestSize = size;
                        best = eh;
                        bestIsHandler = false;
                    }
                }

                uint hstart = eh.HandlerStart?.Offset ?? uint.MaxValue;
                uint hend = eh.HandlerEnd?.Offset ?? uint.MaxValue;

                if (eh.HandlerStart != null && offset >= hstart && offset < hend)
                {
                    uint size = hend - hstart;

                    if (size < bestSize)
                    {
                        bestSize = size;
                        best = eh;
                        bestIsHandler = true;
                    }
                }
            }

            if (best == null)
                return "O";

            int ei = handlers.IndexOf(best);

            return (bestIsHandler ? "H" : "T") + ei;
        }
        catch
        {
            return "O?";
        }
    }

    public static bool SameRegion(MethodDef method, int header, int switchIndex, List<int> targetIndex)
    {
        try
        {
            var instrs = method.Body.Instructions;
            int after = switchIndex + 1;

            while (after < instrs.Count && instrs[after].OpCode.Code == Code.Nop)
                after++;

            var points = new List<int> { header, switchIndex };

            points.AddRange(targetIndex);

            if (after < instrs.Count)
                points.Add(after);

            string? key = null;

            foreach (int p in points)
            {
                if (p < 0 || p >= instrs.Count)
                    return false;

                string k = Key(method, p);

                if (k == "O?")
                    return false;

                if (k.StartsWith("H"))
                    return false;

                if (key == null)
                    key = k;
                else if (key != k)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static HashSet<int> PlainReachable(MethodDef method)
    {
        var reachable = new HashSet<int>();

        try
        {
            var instrs = method.Body.Instructions;
            var index = new Dictionary<uint, int>();

            for (int i = 0; i < instrs.Count; i++)
                index[instrs[i].Offset] = i;

            var work = new Stack<int>();

            work.Push(0);

            foreach (var eh in method.Body.ExceptionHandlers)
            {
                if (eh.HandlerStart != null && index.TryGetValue(eh.HandlerStart.Offset, out int h))
                    work.Push(h);
            }

            while (work.Count > 0)
            {
                int pc = work.Pop();

                if (pc < 0 || pc >= instrs.Count)
                    continue;

                if (!reachable.Add(pc))
                    continue;

                var ins = instrs[pc];
                var code = ins.OpCode.Code;

                if (code == Code.Br || code == Code.Br_S || code == Code.Leave || code == Code.Leave_S)
                {
                    if (ins.Operand is Instruction target && index.TryGetValue(target.Offset, out int ni))
                        work.Push(ni);
                }
                else if (code == Code.Switch)
                {
                    if (ins.Operand is IList<Instruction> list)
                    {
                        foreach (var item in list)
                        {
                            if (index.TryGetValue(item.Offset, out int ni))
                                work.Push(ni);
                        }
                    }

                    if (pc + 1 < instrs.Count)
                        work.Push(pc + 1);
                }
                else if (code == Code.Ret || code == Code.Throw || code == Code.Rethrow || code == Code.Endfinally) { }
                else
                {
                    if (Il.IlHelpers.IsRealBranch(code))
                    {
                        if (ins.Operand is Instruction target && index.TryGetValue(target.Offset, out int ni))
                            work.Push(ni);

                        if (pc + 1 < instrs.Count)
                            work.Push(pc + 1);
                    }
                    else if (pc + 1 < instrs.Count)
                        work.Push(pc + 1);
                }
            }
        }
        catch { }

        return reachable;
    }

    public static HashSet<int> ReachableBranchesToHeader(MethodDef method, int header)
    {
        var result = new HashSet<int>();

        try
        {
            var instrs = method.Body.Instructions;
            Instruction headerIns = instrs[header];
            var reachable = PlainReachable(method);

            for (int i = 0; i < instrs.Count; i++)
            {
                if (!reachable.Contains(i))
                    continue;

                var code = instrs[i].OpCode.Code;

                if ((code == Code.Br || code == Code.Br_S) && (instrs[i].Operand as Instruction) == headerIns)
                    result.Add(i);
            }
        }
        catch { }

        return result;
    }
}
