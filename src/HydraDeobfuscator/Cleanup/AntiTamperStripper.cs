using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Cleanup;

internal static class AntiTamperStripper
{
    public static void Execute(DeobfuscationContext context)
    {
        var tamper = new HashSet<MethodDef>();

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                string name = "";

                try { name = method.Name.String; } catch { continue; }

                if (name != "AntiTamperCheck" && !name.StartsWith("AntiDebugInit_"))
                    continue;

                try
                {
                    if (method.IsStatic && method.Parameters.Count == 0 && method.ReturnType?.FullName == "System.Void")
                        tamper.Add(method);
                }
                catch { }
            }
        }

        int nopped = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                        continue;

                    MethodDef? target = null;

                    try { target = (ins.Operand as IMethod)?.ResolveMethodDef(); } catch { }

                    if (target != null && tamper.Contains(target))
                    {
                        ins.OpCode = OpCodes.Nop;
                        ins.Operand = null;
                        nopped++;
                    }
                }

                if (nopped > 0)
                {
                    try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }
                }
            }
        }

        HydraLogger.Success($"Anti-tamper methods={tamper.Count} calls nopped={nopped}");
    }
}
