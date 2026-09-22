using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Analysis;

internal static class HailGetterMapper
{
    public static void Execute(DeobfuscationContext context)
    {
        if (context.CentralType == null)
            return;

        foreach (var method in context.CentralType.Methods)
        {
            if (!method.HasBody)
                continue;

            if (method.ReturnType?.FullName != "System.Int32")
                continue;

            if (method.Parameters.Count != 0)
                continue;

            var instrs = method.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToList();

            foreach (var ins in instrs)
            {
                if (ins.OpCode.Code != Code.Ldsfld)
                    continue;

                var field = (ins.Operand as FieldDef) ?? (ins.Operand as IField)?.ResolveFieldDef();

                if (field != null && context.HailFieldValues.TryGetValue(field.Name.String, out int v))
                {
                    context.HailGetterValues[method] = v;
                    context.IntProxyValues[method] = v;
                }

                break;
            }
        }

        HydraLogger.Success($"Hail getters {context.HailGetterValues.Count}");
    }
}
