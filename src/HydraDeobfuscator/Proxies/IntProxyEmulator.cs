using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using dnlib.DotNet;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Proxies;

internal static class IntProxyEmulator
{
    public static void Execute(DeobfuscationContext context)
    {
        var all = context.Module.GetTypes().SelectMany(t => t.Methods).ToList();

        context.TotalMethods = all.Count;

        int attempted = 0;
        int succeeded = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (context.IntProxyValues.ContainsKey(method))
                    continue;

                if (!method.HasBody)
                    continue;

                if (method.Parameters.Count != 0)
                    continue;

                if (method.ReturnType?.FullName != "System.Int32")
                    continue;

                if (!method.IsStatic)
                    continue;

                attempted++;

                if (IntEmulator.TryEmulate(method, context, new HashSet<MethodDef>(), out int value, 0))
                {
                    context.IntProxyValues[method] = value;
                    succeeded++;
                }
            }
        }

        HydraLogger.Success($"Emulated ints attempted={attempted} succeeded={succeeded} total={context.IntProxyValues.Count}");

        Audit(context);
    }

    private static void Audit(DeobfuscationContext context)
    {
        int checkedCount = 0;
        int match = 0;
        int mismatch = 0;

        foreach (var pair in context.IntProxyValues)
        {
            string name = pair.Key.Name.String;
            int cut = name.LastIndexOf('_');

            if (cut < 0 || cut == name.Length - 1)
                continue;

            if (!int.TryParse(name.Substring(cut + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int expected))
                continue;

            checkedCount++;

            if (pair.Value == expected)
                match++;
            else
            {
                mismatch++;

                if (mismatch <= 15)
                    HydraLogger.Detail($"[proxy-mismatch] {pair.Key.DeclaringType.Name}::{name} emulated={pair.Value} expected={expected}");
            }
        }

        HydraLogger.Stats(("checked", checkedCount.ToString()), ("match", match.ToString()), ("mismatch", mismatch.ToString()));
    }
}
