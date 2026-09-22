using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Strings;

internal static class ModuleDecryptor
{
    public static void Execute(DeobfuscationContext context)
    {
        int patched = 0;

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

                    var target = ins.Operand as IMethod;

                    if (target == null || target.Name.String != "Decrypt")
                        continue;

                    if (!(target.DeclaringType?.FullName?.Contains("<Module>") ?? false))
                        continue;

                    int ldstrIndex = -1;
                    string? encoded = null;

                    for (int j = System.Math.Max(0, i - 20); j < i; j++)
                    {
                        if (instrs[j].OpCode.Code == Code.Ldstr)
                        {
                            ldstrIndex = j;
                            encoded = instrs[j].Operand as string;
                        }
                    }

                    if (ldstrIndex == -1 || encoded == null)
                        continue;

                    var keyPositions = new List<int>();
                    var keyValues = new List<int>();

                    for (int j = ldstrIndex + 1; j < i; j++)
                    {
                        if (IlHelpers.IsLdcI4(instrs[j], out int v))
                        {
                            keyPositions.Add(j);
                            keyValues.Add(v);
                        }
                    }

                    if (keyValues.Count < 5)
                    {
                        keyPositions.Clear();
                        keyValues.Clear();

                        for (int j = System.Math.Max(0, i - 12); j < i; j++)
                        {
                            if (IlHelpers.IsLdcI4(instrs[j], out int v))
                            {
                                keyPositions.Add(j);
                                keyValues.Add(v);
                            }
                        }

                        if (keyValues.Count < 5)
                            continue;
                    }

                    var usePositions = keyPositions.Count >= 5 ? keyPositions.GetRange(keyPositions.Count - 5, 5) : new List<int>(keyPositions);
                    var useValues = keyValues.Count >= 5 ? keyValues.GetRange(keyValues.Count - 5, 5) : new List<int>(keyValues);

                    if (useValues.Count < 2)
                        continue;

                    string plain;

                    try { plain = CaesarCipher.DecryptModule(encoded, useValues[1]); } catch { continue; }

                    instrs[ldstrIndex].Operand = plain;

                    foreach (int k in usePositions)
                    {
                        instrs[k].OpCode = OpCodes.Nop;
                        instrs[k].Operand = null;
                    }

                    instrs[i].OpCode = OpCodes.Nop;
                    instrs[i].Operand = null;
                    patched++;
                }
            }
        }

        HydraLogger.Success($"Patched Module.Decrypt {patched}");
    }
}
