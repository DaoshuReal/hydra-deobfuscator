using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Strings;

internal static class CombinedDecryptor
{
    public static void Execute(DeobfuscationContext context)
    {
        int patched = 0;
        int tried = 0;

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

                    var current = ins.Operand as IMethod;

                    if (current == null)
                        continue;

                    string name = current.Name.String;
                    bool isBase64 = name.Contains("_90841") || name == "DecryptBase64";

                    if (!isBase64)
                        continue;

                    int caesarIndex = -1;

                    for (int j = Math.Max(0, i - 8); j < i; j++)
                    {
                        var other = instrs[j].Operand as IMethod;

                        if ((instrs[j].OpCode.Code == Code.Call || instrs[j].OpCode.Code == Code.Callvirt) && other != null && (other.Name.String.Contains("_25982") || other.Name.String == "DecryptCaesar"))
                        {
                            caesarIndex = j;
                            break;
                        }
                    }

                    if (caesarIndex == -1)
                        continue;

                    int ldstrIndex = -1;
                    string? encoded = null;

                    for (int k = Math.Max(0, caesarIndex - 80); k < caesarIndex; k++)
                    {
                        if (instrs[k].OpCode.Code == Code.Ldstr)
                        {
                            ldstrIndex = k;
                            encoded = instrs[k].Operand as string;
                        }
                    }

                    if (ldstrIndex == -1 || encoded == null)
                        continue;

                    var positions = new List<int>();
                    var values = new List<int>();

                    for (int k = ldstrIndex + 1; k < caesarIndex; k++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[k], out int v))
                        {
                            positions.Add(k);
                            values.Add(v);
                        }
                    }

                    bool exact = values.Count == 5;

                    if (values.Count < 5)
                    {
                        positions.Clear();
                        values.Clear();

                        for (int k = Math.Max(0, caesarIndex - 30); k < caesarIndex; k++)
                        {
                            if (IlHelpers.IsLdcI4(instrs[k], out int v))
                            {
                                positions.Add(k);
                                values.Add(v);
                            }
                        }

                        if (values.Count < 5)
                            continue;

                        positions = positions.Skip(positions.Count - 5).ToList();
                        values = values.Skip(values.Count - 5).ToList();
                        exact = false;
                    }
                    else if (values.Count > 5)
                    {
                        positions = positions.Skip(positions.Count - 5).ToList();
                        values = values.Skip(values.Count - 5).ToList();
                        exact = false;
                    }

                    if (values.Count != 5)
                        continue;

                    tried++;

                    if (encoded.Length == 0)
                    {
                        if (!exact)
                            continue;

                        instrs[ldstrIndex].Operand = "";
                        foreach (int k in positions) { instrs[k].OpCode = OpCodes.Nop; instrs[k].Operand = null; }

                        instrs[caesarIndex].OpCode = OpCodes.Nop;
                        instrs[caesarIndex].Operand = null;
                        instrs[i].OpCode = OpCodes.Nop;
                        instrs[i].Operand = null;
                        patched++;

                        continue;
                    }

                    string? best = null;
                    var clean = new List<string>();
                    var order = new List<int> { values[1], values[0], values[2], values[3], values[4] };

                    foreach (int candidate in order.Distinct())
                    {
                        string tmp;

                        try { tmp = CaesarCipher.DecryptModule(encoded, candidate); } catch { continue; }

                        string final;

                        try
                        {
                            var data = Convert.FromBase64String(tmp);

                            for (int p = 0; p < data.Length; p++)
                                data[p] ^= context.HailKey[p % context.HailKey.Length];

                            final = Encoding.UTF8.GetString(data);
                        }
                        catch { continue; }

                        if (final.Any(ch => ch == '�'))
                            continue;

                        clean.Add(final);

                        if (IlHelpers.IsPlausiblePlaintext(final))
                        {
                            best = final;
                            break;
                        }
                    }

                    if (best == null && clean.Count == 1 && IlHelpers.IsShortAcceptable(clean[0]))
                        best = clean[0];

                    if (best == null)
                        continue;

                    instrs[ldstrIndex].Operand = best;

                    foreach (int k in positions) { instrs[k].OpCode = OpCodes.Nop; instrs[k].Operand = null; }

                    instrs[caesarIndex].OpCode = OpCodes.Nop;
                    instrs[caesarIndex].Operand = null;
                    instrs[i].OpCode = OpCodes.Nop;
                    instrs[i].Operand = null;
                    patched++;
                }
            }
        }

        HydraLogger.Success($"Patched combined Caesar/Base64 {patched} (tried {tried})");
    }
}
