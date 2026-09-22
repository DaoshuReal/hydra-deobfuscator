using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Analysis;

internal static class HailFieldExtractor
{
    public static void Execute(DeobfuscationContext context)
    {
        if (context.CentralType == null)
            return;

        foreach (var method in context.CentralType.Methods)
        {
            if (!method.HasBody)
                continue;

            int stores = method.Body.Instructions.Count(i => i.OpCode.Code == Code.Stsfld);

            if (stores <= 20)
                continue;

            HydraLogger.Detail($"Hail init {method.Name} stores={stores}");

            var instrs = method.Body.Instructions;

            for (int i = 0; i < instrs.Count - 1; i++)
            {
                if (IlHelpers.IsLdcI4(instrs[i], out int v) && instrs[i + 1].OpCode.Code == Code.Stsfld)
                {
                    var field = instrs[i + 1].Operand as FieldDef;

                    if (field == null)
                        field = (instrs[i + 1].Operand as IField)?.ResolveFieldDef();

                    if (field != null)
                        context.HailFieldValues[field.Name.String] = v;
                }
            }

            if (context.HailFieldValues.Count > 20)
                break;
        }

        HydraLogger.Success($"Hail fields {context.HailFieldValues.Count}");
    }
}
