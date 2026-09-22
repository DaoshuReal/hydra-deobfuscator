using System.Globalization;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Proxies;

internal static class IntProxySeeder
{
    public static void Execute(DeobfuscationContext context)
    {
        int seeded = 0;

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

                if (!method.IsStatic)
                    continue;

                string ret = "";

                try { ret = method.ReturnType?.FullName ?? ""; } catch { continue; }

                if (ret != "System.Int32")
                    continue;

                string name = method.Name.String;
                int cut = name.LastIndexOf('_');

                if (cut < 0 || cut == name.Length - 1)
                    continue;

                if (!int.TryParse(name.Substring(cut + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    continue;

                if (!name.StartsWith("_"))
                    continue;

                context.IntProxyValues[method] = value;
                seeded++;
            }
        }

        HydraLogger.Success($"Seeded int proxies from names: {seeded}");
    }
}
