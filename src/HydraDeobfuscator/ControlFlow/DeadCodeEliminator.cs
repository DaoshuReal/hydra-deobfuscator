using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.ControlFlow;

internal static class DeadCodeEliminator
{
    public static void Execute(DeobfuscationContext context)
    {
        int removed = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                var instrs = method.Body.Instructions;

                if (instrs.Count == 0)
                    continue;

                var index = IlHelpers.BuildOffsetIndex(instrs);
                var reachable = new HashSet<int>();
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

                    if (reachable.Contains(pc))
                        continue;

                    reachable.Add(pc);

                    var ins = instrs[pc];
                    var code = ins.OpCode.Code;

                    if (code == Code.Br || code == Code.Br_S || code == Code.Leave || code == Code.Leave_S)
                    {
                        var target = ins.Operand as Instruction;

                        if (target != null && index.TryGetValue(target.Offset, out int ni))
                            work.Push(ni);
                    }
                    else if (code == Code.Switch)
                    {
                        var list = ins.Operand as IList<Instruction>;

                        if (list != null)
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
                        if (IlHelpers.IsRealBranch(code))
                        {
                            var target = ins.Operand as Instruction;

                            if (target != null && index.TryGetValue(target.Offset, out int ni))
                                work.Push(ni);

                            if (pc + 1 < instrs.Count)
                                work.Push(pc + 1);
                        }
                        else
                        {
                            if (pc + 1 < instrs.Count)
                                work.Push(pc + 1);
                        }
                    }
                }

                var toDelete = new List<Instruction>();

                for (int i = 0; i < instrs.Count; i++)
                {
                    if (!reachable.Contains(i))
                        toDelete.Add(instrs[i]);
                }

                foreach (var item in toDelete)
                {
                    instrs.Remove(item);
                    removed++;
                }
            }
        }

        HydraLogger.Success($"Dead code removed={removed}");
    }

    public static void RemoveUnreachable(MethodDef method)
    {
        try
        {
            var instrs = method.Body.Instructions;

            if (instrs.Count == 0)
                return;

            var index = IlHelpers.BuildOffsetIndex(instrs);
            var reachable = new HashSet<int>();
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
                    if (IlHelpers.IsRealBranch(code))
                    {
                        if (ins.Operand is Instruction target && index.TryGetValue(target.Offset, out int ni))
                            work.Push(ni);

                        if (pc + 1 < instrs.Count)
                            work.Push(pc + 1);
                    }
                    else
                    {
                        if (pc + 1 < instrs.Count)
                            work.Push(pc + 1);
                    }
                }
            }

            var toDelete = new List<Instruction>();

            for (int i = 0; i < instrs.Count; i++)
            {
                if (!reachable.Contains(i))
                    toDelete.Add(instrs[i]);
            }

            var deleted = new HashSet<Instruction>(toDelete);

            foreach (var ins in instrs)
            {
                if (deleted.Contains(ins))
                    continue;

                if (ins.Operand is Instruction target && deleted.Contains(target))
                    return;

                if (ins.Operand is IList<Instruction> list && System.Linq.Enumerable.Any(list, x => deleted.Contains(x)))
                    return;
            }

            foreach (var item in toDelete)
                instrs.Remove(item);
        }
        catch { }
    }
}
