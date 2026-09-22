using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Proxies;

internal static class StringProxyInliner
{
    public static void Execute(DeobfuscationContext context)
    {
        int inlined = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                var instrs = method.Body.Instructions;

                for (int i = 0; i < instrs.Count; i++)
                {
                    var ins = instrs[i];

                    if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                        continue;

                    var target = (ins.Operand as IMethod)?.ResolveMethodDef();

                    if (target == null)
                        continue;

                    if (context.StringProxyValues.TryGetValue(target, out string? value))
                    {
                        ins.OpCode = OpCodes.Ldstr;
                        ins.Operand = value;
                        inlined++;
                    }
                }
            }
        }

        HydraLogger.Success($"Inlined {inlined} string proxy calls");
    }
}
