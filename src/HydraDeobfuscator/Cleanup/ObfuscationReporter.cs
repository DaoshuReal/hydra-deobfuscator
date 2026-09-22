using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Cleanup;

internal static class ObfuscationReporter
{
    public static void Execute(DeobfuscationContext context, string phase)
    {
        int withSwitch = 0;
        int withEhSwitch = 0;
        int withStrCall = 0;
        int withSwitchNoEh = 0;

        var switchMethods = new List<string>();
        var stringMethods = new List<string>();
        HashSet<MethodDef> payloads;

        try { payloads = new HashSet<MethodDef>(context.DynamicPayloads.Keys); } catch { payloads = new HashSet<MethodDef>(); }

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                bool hasSwitch = false;
                int n = 0;

                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code == Code.Switch)
                    {
                        hasSwitch = true;

                        var list = ins.Operand as IList<Instruction>;

                        n = list?.Count ?? 0;

                        break;
                    }
                }

                bool hasString = false;

                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                        continue;

                    MethodDef? target = null;

                    try { target = (ins.Operand as IMethod)?.ResolveMethodDef(); } catch { }

                    if (target == null)
                        continue;

                    if (payloads.Contains(target))
                    {
                        hasString = true;
                        break;
                    }

                    try
                    {
                        string name = target.Name.String;

                        if (name == "DecryptCaesar" || name == "DecryptBase64" || name.Contains("_25982") || name.Contains("_90841"))
                        {
                            hasString = true;
                            break;
                        }
                    }
                    catch { }
                }

                if (hasSwitch)
                {
                    withSwitch++;

                    if (method.Body.ExceptionHandlers.Count != 0)
                        withEhSwitch++;
                    else
                        withSwitchNoEh++;

                    if (switchMethods.Count < 40)
                        switchMethods.Add($"{type.Name}::{method.Name} N={n} EH={method.Body.ExceptionHandlers.Count} str={hasString}");
                }

                if (hasString)
                {
                    withStrCall++;

                    if (stringMethods.Count < 40)
                        stringMethods.Add($"{type.Name}::{method.Name}");
                }
            }
        }

        HydraLogger.Info($"[report {phase}] switches={withSwitch} (noEH={withSwitchNoEh} withEH={withEhSwitch}) strings={withStrCall}");

        foreach (var item in switchMethods.Take(12))
            HydraLogger.Sample($"SW: {item}");

        foreach (var item in stringMethods.Take(8))
            HydraLogger.Sample($"STR: {item}");
    }
}
