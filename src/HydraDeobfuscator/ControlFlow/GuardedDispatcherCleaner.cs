using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.ControlFlow;

internal static class GuardedDispatcherCleaner
{
    public static void Execute(DeobfuscationContext context)
    {
        int patched = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                if (PatchMethod(method))
                    patched++;
            }
        }

        HydraLogger.Success($"Guarded dispatchers patched={patched}");
    }

    private static bool PatchMethod(MethodDef method)
    {
        try
        {
            var instrs = method.Body.Instructions;
            var index = new Dictionary<uint, int>();

            for (int i = 0; i < instrs.Count; i++)
                index[instrs[i].Offset] = i;

            var switches = new List<int>();

            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode.Code != Code.Switch)
                    continue;

                var list = instrs[i].Operand as IList<Instruction>;

                if (list == null || list.Count != 3)
                    continue;

                if (i < 2)
                    continue;

                if (instrs[i - 1].OpCode.Code != Code.Rem_Un)
                    continue;

                if (!IlHelpers.IsLdcI4(instrs[i - 2], out int nv))
                    continue;

                if (nv != 3)
                    continue;

                switches.Add(i);
            }

            if (switches.Count == 0)
                return false;

            bool any = false;

            switches.Sort();
            switches.Reverse();

            foreach (int sw in switches)
            {
                if (PatchOne(method, sw))
                    any = true;
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    private static bool PatchOne(MethodDef method, int switchIndex)
    {
        try
        {
            var instrs = method.Body.Instructions;
            var index = new Dictionary<uint, int>();

            for (int i = 0; i < instrs.Count; i++)
                index[instrs[i].Offset] = i;

            var switchIns = instrs[switchIndex];
            var list = switchIns.Operand as IList<Instruction>;

            if (list == null || list.Count != 3)
                return false;

            if (switchIndex < 2)
                return false;

            if (instrs[switchIndex - 1].OpCode.Code != Code.Rem_Un)
                return false;

            if (!IlHelpers.IsLdcI4(instrs[switchIndex - 2], out int nv))
                return false;

            if (nv != 3)
                return false;

            int[] ti = new int[3];

            for (int k = 0; k < 3; k++)
            {
                if (!index.TryGetValue(list[k].Offset, out ti[k]))
                    return false;
            }

            int selfIndex = -1;
            int realIndex = -1;
            int exitIndex = -1;

            for (int k = 0; k < 3; k++)
            {
                int t = ti[k];

                if (t < 0 || t >= instrs.Count)
                    continue;

                var oc = instrs[t].OpCode.Code;

                if (oc == Code.Ldc_I4 || oc == Code.Ldc_I4_S || oc == Code.Ldc_I4_0 || oc == Code.Ldc_I4_1)
                    selfIndex = t;
                else if (oc == Code.Nop)
                    realIndex = t;
                else if (oc == Code.Leave || oc == Code.Leave_S || oc == Code.Br || oc == Code.Br_S)
                    exitIndex = t;
            }

            HydraLogger.VerboseDetail($"Guarded switch sw={switchIndex} self={selfIndex} real={realIndex} exit={exitIndex} in {method.DeclaringType.Name}::{method.Name}");

            if (realIndex < 0 || exitIndex < 0)
                return false;

            int dispStart = -1;

            for (int k = 0; k < 3; k++)
            {
                int t = ti[k];

                if (t < 0 || t >= instrs.Count)
                    continue;

                var oc = instrs[t].OpCode.Code;

                if (oc == Code.Ldc_I4 || oc == Code.Ldc_I4_S || oc == Code.Ldc_I4_0 || oc == Code.Ldc_I4_1)
                {
                    dispStart = t;
                    break;
                }
            }

            if (dispStart < 0)
                return false;

            instrs[dispStart].OpCode = OpCodes.Br;
            instrs[dispStart].Operand = instrs[realIndex];

            int realEnd = -1;

            for (int i = realIndex; i < instrs.Count && i < realIndex + 40; i++)
            {
                var code = instrs[i].OpCode.Code;

                if ((code == Code.Br || code == Code.Br_S) && instrs[i].Operand is Instruction target)
                {
                    if (index.TryGetValue(target.Offset, out int tni) && tni < switchIndex && tni > switchIndex - 15)
                    {
                        realEnd = i;
                        break;
                    }
                }
            }

            if (realEnd < 0)
                return false;

            int lastStore = -1;

            for (int i = realIndex; i < realEnd; i++)
            {
                var code = instrs[i].OpCode.Code;

                if (code == Code.Stloc_0 || code == Code.Stloc_1 || code == Code.Stloc_2 || code == Code.Stloc_3 || code == Code.Stloc_S || code == Code.Stloc)
                    lastStore = i;
            }

            if (lastStore >= 0)
            {
                for (int i = lastStore + 1; i < realEnd; i++)
                {
                    instrs[i].OpCode = OpCodes.Nop;
                    instrs[i].Operand = null;
                }
            }

            var exitIns = instrs[exitIndex];

            instrs[realEnd].OpCode = OpCodes.Br;
            instrs[realEnd].Operand = exitIns;

            try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
