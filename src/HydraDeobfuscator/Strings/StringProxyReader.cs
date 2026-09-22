using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Il;

namespace HydraDeobfuscator.Strings;

internal static class StringProxyReader
{
    public static bool TryGetValue(MethodDef method, out string? result)
    {
        result = null;

        try
        {
            var instrs = method.Body.Instructions;

            int lastDecrypt = -1;

            for (int i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];

                if ((ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) && ins.Operand is IMethod inner && inner.Name.String == "Decrypt" && (inner.DeclaringType?.FullName?.Contains("<Module>") ?? false))
                    lastDecrypt = i;
            }

            if (lastDecrypt == -1)
                return false;

            string? encoded = null;
            var keys = new List<int>();

            for (int j = Math.Max(0, lastDecrypt - 20); j < lastDecrypt; j++)
            {
                var ins = instrs[j];

                if (ins.OpCode.Code == Code.Ldstr)
                    encoded = ins.Operand as string;

                if (IlHelpers.IsLdcI4(ins, out int v))
                    keys.Add(v);
            }

            if (encoded == null || keys.Count < 2)
                return false;

            int ldstrIndex = -1;

            for (int j = lastDecrypt - 1; j >= Math.Max(0, lastDecrypt - 20); j--)
            {
                if (instrs[j].OpCode.Code == Code.Ldstr)
                {
                    ldstrIndex = j;
                    encoded = instrs[j].Operand as string;
                    break;
                }
            }

            if (ldstrIndex == -1 || encoded == null)
                return false;

            var after = new List<int>();

            for (int j = ldstrIndex + 1; j < lastDecrypt; j++)
            {
                if (IlHelpers.IsLdcI4(instrs[j], out int v))
                    after.Add(v);
            }

            if (after.Count < 2)
                return false;

            List<int> finalKeys;

            if (after.Count >= 5)
                finalKeys = after.Skip(after.Count - 5).ToList();
            else
                finalKeys = after;

            if (finalKeys.Count < 2)
                return false;

            result = CaesarCipher.DecryptModule(encoded, finalKeys[1]);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
