using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;
using HydraDeobfuscator.Strings;

namespace HydraDeobfuscator.Proxies;

internal static class StringProxyCollector
{
    public static void Execute(DeobfuscationContext context)
    {
        int found = 0;
        int skippedDynamic = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (context.StringProxyValues.ContainsKey(method))
                    continue;

                if (!method.HasBody)
                    continue;

                if (method.Parameters.Count != 0)
                    continue;

                if (method.ReturnType?.FullName != "System.String")
                    continue;

                if (!method.IsStatic)
                    continue;

                bool isDynamic = false;

                foreach (var ins in method.Body.Instructions)
                {
                    string text = ins.Operand?.ToString() ?? "";

                    if (text.Contains("DynamicMethod") || text.Contains("ILGenerator") || ins.OpCode.Code == Code.Ldtoken)
                    {
                        isDynamic = true;
                        break;
                    }

                    if (ins.Operand is IMethod inner && (inner.Name.String.Contains("DynamicMethod") || inner.Name.String.Contains("GetILGenerator") || inner.Name.String.Contains("Emit")))
                    {
                        isDynamic = true;
                        break;
                    }
                }

                if (isDynamic)
                {
                    skippedDynamic++;
                    continue;
                }

                if (StringProxyReader.TryGetValue(method, out string? value) && value != null)
                {
                    if (value.Length < 2000)
                    {
                        context.StringProxyValues[method] = value;
                        found++;
                    }
                }
            }
        }

        HydraLogger.Success($"String proxies {found} (skipped {skippedDynamic} dynamic)");

        foreach (var pair in context.StringProxyValues.Take(10))
            HydraLogger.Detail($"{pair.Key.DeclaringType.Name}::{pair.Key.Name} = \"{IlHelpers.Truncate(pair.Value, 80)}\"");
    }
}
