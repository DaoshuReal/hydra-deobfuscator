using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Strings;

internal static class DecryptorFinder
{
    public static void Execute(DeobfuscationContext context)
    {
        context.DynamicPayloads.Clear();
        context.DecryptCaesarDef = null;
        context.DecryptBase64Def = null;
        context.GetXorKeyDef = null;

        if (context.CentralType == null)
            return;

        foreach (var method in context.CentralType.Methods)
        {
            if (!method.IsStatic)
                continue;

            string ret = "";

            try { ret = method.ReturnType?.FullName ?? ""; } catch { continue; }

            int count = method.Parameters.Count;

            if (ret == "System.String" && count == 6)
            {
                try
                {
                    if (method.Parameters[0].Type.FullName == "System.String")
                        context.DecryptCaesarDef ??= method;
                }
                catch { context.DecryptCaesarDef ??= method; }
            }
            else if (ret == "System.String" && count == 1)
            {
                try
                {
                    if (method.Parameters[0].Type.FullName == "System.String")
                        context.DecryptBase64Def ??= method;
                }
                catch { context.DecryptBase64Def ??= method; }
            }
            else if (ret == "System.Byte[]" && count == 0)
            {
                context.GetXorKeyDef ??= method;
            }
        }

        HydraLogger.Detail($"Decryptors Caesar={context.DecryptCaesarDef?.Name} Base64={context.DecryptBase64Def?.Name} XorKey={context.GetXorKeyDef?.Name}");
    }
}
