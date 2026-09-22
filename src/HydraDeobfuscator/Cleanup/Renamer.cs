using System.Linq;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Cleanup;

internal static class Renamer
{
    public static void Execute(DeobfuscationContext context)
    {
        int renamed = 0;

        if (context.CentralType != null)
        {
            HydraLogger.Detail($"Renaming central {context.CentralType.Name} -> AntiDebug");

            context.CentralType.Name = "AntiDebug";
            context.CentralType.Namespace = "OldSecurity.Helpers";
            renamed++;

            foreach (var method in context.CentralType.Methods)
            {
                string name = method.Name.String;
                string? next = null;

                try
                {
                    string ret = method.ReturnType?.FullName ?? "";
                    int count = method.Parameters.Count;

                    if (ret == "System.String" && count == 1) next = "DecryptBase64";
                    else if (ret == "System.String" && count == 6) next = "DecryptCaesar";
                    else if (ret == "System.Byte[]" && count == 0) next = "GetXorKey";
                }
                catch { }

                if (next != null)
                {
                    string baseName = next;
                    int suffix = 1;

                    while (context.CentralType.Methods.Any(x => x != method && x.Name.String == next))
                    {
                        next = baseName + suffix;
                        suffix++;
                    }

                    method.Name = next;
                    renamed++;

                    continue;
                }

                if (name.Contains("QT8"))
                {
                    method.Name = "InitAntiDebug";
                    renamed++;
                }
                else if (name.StartsWith("<") && name.Contains(">") && !IlHelpers.IsCompilerGeneratedName(method, name))
                {
                    method.Name = "AntiDebugInit_" + renamed;
                    renamed++;
                }
                else if (name.StartsWith("_") && name.Length > 20)
                {
                    method.Name = "Helper_" + renamed;
                    renamed++;
                }
                else if (method.ReturnType?.FullName == "System.String" && method.Parameters.Count == 0)
                {
                    bool nonAscii = false;

                    foreach (char c in name)
                    {
                        if (c > 127) { nonAscii = true; break; }
                    }

                    if (nonAscii)
                    {
                        method.Name = "GetString_" + renamed;
                        renamed++;
                    }
                }
            }
        }

        foreach (var type in context.Module.GetTypes())
        {
            if (type == context.CentralType)
                continue;

            foreach (var method in type.Methods)
            {
                string name = method.Name.String;

                if (name.StartsWith("<") && name.Contains(">") && !IlHelpers.IsCompilerGeneratedName(method, name))
                {
                    bool sigOk = false;

                    try { sigOk = method.IsStatic && method.Parameters.Count == 0 && method.ReturnType?.FullName == "System.Void"; } catch { }

                    if (!sigOk)
                        continue;

                    method.Name = "AntiTamperCheck";
                    renamed++;
                }
            }
        }

        foreach (var type in context.Module.GetTypes())
        {
            if (type.Name.String != "Config")
                continue;

            foreach (var field in type.Fields)
            {
                if (field.Name.String == "penis")
                {
                    field.Name = "MainConfig";
                    renamed++;
                }
            }
        }

        HydraLogger.Success($"Renamed {renamed} items");
    }
}
