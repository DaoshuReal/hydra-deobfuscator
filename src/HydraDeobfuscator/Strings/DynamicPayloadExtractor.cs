using System;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Strings;

internal static class DynamicPayloadExtractor
{
    public static void Execute(DeobfuscationContext context)
    {
        context.DynamicPayloads.Clear();

        if (context.CentralType == null)
            return;

        int ok = 0;
        int fail = 0;

        foreach (var method in context.CentralType.Methods)
        {
            if (!method.HasBody)
                continue;

            if (!method.IsStatic)
                continue;

            string ret = "";

            try { ret = method.ReturnType?.FullName ?? ""; } catch { continue; }

            if (ret != "System.String")
                continue;

            if (method.Parameters.Count != 0)
                continue;

            if (method == context.DecryptCaesarDef || method == context.DecryptBase64Def)
                continue;

            bool hasDynamic = false;

            foreach (var ins in method.Body.Instructions)
            {
                try
                {
                    var operand = ins.Operand;

                    if (operand is IMethod inner && (inner.Name.String.Contains("DynamicMethod") || inner.Name.String.Contains("Emit") || inner.Name.String.Contains("GetILGenerator")))
                    {
                        hasDynamic = true;
                        break;
                    }

                    if (ins.OpCode.Code == Code.Ldtoken)
                    {
                        hasDynamic = true;
                        break;
                    }

                    string text = operand?.ToString() ?? "";

                    if (text.Contains("DynamicMethod") || text.Contains("ILGenerator"))
                    {
                        hasDynamic = true;
                        break;
                    }
                }
                catch { }
            }

            if (!hasDynamic)
                continue;

            string? encoded = TryExtract(method);

            if (encoded != null)
            {
                context.DynamicPayloads[method] = encoded;
                ok++;
            }
            else
            {
                fail++;
            }
        }

        HydraLogger.Success($"Dynamic payloads ok={ok} fail={fail}");

        foreach (var pair in context.DynamicPayloads.Take(5))
            HydraLogger.Detail($"{pair.Key.Name} len={pair.Value.Length} head=\"{IlHelpers.Truncate(pair.Value, 40)}\"");
    }

    private static string? TryExtract(MethodDef method)
    {
        try
        {
            var instrs = method.Body.Instructions;

            for (int i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];

                if ((ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt) && ins.Operand is IMethod inner && inner.Name.String == "Emit")
                {
                    int seen = 0;

                    for (int j = i - 1; j >= 0 && seen < 8; j--)
                    {
                        var current = instrs[j];
                        var code = current.OpCode.Code;

                        if (code == Code.Nop)
                            continue;

                        if (code == Code.Br || code == Code.Br_S)
                            continue;

                        seen++;

                        if (code == Code.Ldstr && current.Operand is string text)
                        {
                            int innerSeen = 0;

                            for (int k = j - 1; k >= 0 && innerSeen < 8; k--)
                            {
                                var prev = instrs[k];
                                var prevCode = prev.OpCode.Code;

                                if (prevCode == Code.Nop)
                                    continue;

                                if (prevCode == Code.Br || prevCode == Code.Br_S)
                                    continue;

                                innerSeen++;

                                if (prevCode == Code.Ldsfld)
                                {
                                    string fieldText = "";

                                    try { fieldText = prev.Operand?.ToString() ?? ""; } catch { }

                                    if (fieldText.Contains("Ldstr"))
                                        return text;
                                }

                                if (innerSeen >= 4)
                                    break;
                            }
                        }

                        if (seen >= 4)
                            break;
                    }
                }
            }

            string? best = null;

            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode.Code == Code.Ldstr && instrs[i].Operand is string text)
                {
                    for (int j = i + 1; j < Math.Min(instrs.Count, i + 5); j++)
                    {
                        var next = instrs[j];

                        if ((next.OpCode.Code == Code.Call || next.OpCode.Code == Code.Callvirt) && next.Operand is IMethod target && target.Name.String == "Emit")
                        {
                            if (best == null || text.Length > best.Length)
                                best = text;

                            break;
                        }
                    }
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }
}
