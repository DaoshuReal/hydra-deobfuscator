using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Strings;

internal static class StringDecryptorV2
{
    public static void Execute(DeobfuscationContext context)
    {
        if (context.CentralType == null)
            return;

        DecryptorFinder.Execute(context);
        DynamicPayloadExtractor.Execute(context);

        if (context.DecryptCaesarDef == null || context.DecryptBase64Def == null)
        {
            HydraLogger.Warn("V2 decryptors not found, skipping");
            return;
        }

        int patched = 0;
        int tried = 0;
        int implausible = 0;
        int patchedShort = 0;

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                if (method == context.DecryptCaesarDef || method == context.DecryptBase64Def)
                    continue;

                if (context.DynamicPayloads.ContainsKey(method))
                    continue;

                var instrs = method.Body.Instructions;
                var positions = new Dictionary<Instruction, int>();

                for (int i = 0; i < instrs.Count; i++)
                    positions[instrs[i]] = i;

                for (int i = 0; i < instrs.Count; i++)
                {
                    var ins = instrs[i];

                    if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                        continue;

                    var target = (ins.Operand as IMethod)?.ResolveMethodDef();

                    if (target == null)
                        continue;

                    if (!context.DynamicPayloads.TryGetValue(target, out string? encoded))
                        continue;

                    var keys = new List<int>();
                    var keyIndex = new List<int>();
                    int j = i + 1;
                    int guard = 0;

                    while (j < instrs.Count && keys.Count < 5 && guard++ < 30)
                    {
                        var current = instrs[j];

                        if (IsSkippable(current, instrs, positions))
                        {
                            j++;
                            continue;
                        }

                        if (IlHelpers.IsLdcI4(current, out int v))
                        {
                            keys.Add(v);
                            keyIndex.Add(j);
                            j++;

                            continue;
                        }

                        break;
                    }

                    if (keys.Count != 5)
                        continue;

                    int k = j;

                    while (k < instrs.Count && IsSkippable(instrs[k], instrs, positions))
                        k++;

                    if (k >= instrs.Count)
                        continue;

                    var caesarCall = instrs[k];

                    if (caesarCall.OpCode.Code != Code.Call && caesarCall.OpCode.Code != Code.Callvirt)
                        continue;

                    if ((caesarCall.Operand as IMethod)?.ResolveMethodDef() != context.DecryptCaesarDef)
                        continue;

                    int l = k + 1;

                    while (l < instrs.Count && IsSkippable(instrs[l], instrs, positions))
                        l++;

                    if (l >= instrs.Count)
                        continue;

                    var base64Call = instrs[l];

                    if (base64Call.OpCode.Code != Code.Call && base64Call.OpCode.Code != Code.Callvirt)
                        continue;

                    if ((base64Call.Operand as IMethod)?.ResolveMethodDef() != context.DecryptBase64Def)
                        continue;

                    tried++;

                    string? best = null;
                    bool bestIsShort = false;
                    var order = new List<int> { keys[1], keys[0], keys[2], keys[3], keys[4] };

                    if (encoded.Length == 0)
                    {
                        best = "";
                    }
                    else
                    {
                        var clean = new List<(int key, string text)>();

                        foreach (int candidate in order.Distinct())
                        {
                            string tmp;

                            try { tmp = CaesarCipher.DecryptModule(encoded, candidate); } catch { continue; }

                            string? final = CaesarCipher.TryBase64Xor(tmp, context);

                            if (final == null)
                                continue;

                            if (final.Any(ch => ch == '�'))
                                continue;

                            clean.Add((candidate, final));

                            if (IlHelpers.IsPlausiblePlaintext(final))
                            {
                                best = final;
                                break;
                            }
                        }

                        if (best == null && clean.Count == 1 && IlHelpers.IsShortAcceptable(clean[0].text))
                        {
                            best = clean[0].text;
                            bestIsShort = true;
                        }
                    }

                    if (best == null)
                    {
                        implausible++;

                        if (implausible <= 8)
                        {
                            try
                            {
                                var details = new List<string>();

                                foreach (int candidate in order.Distinct())
                                {
                                    string tmp2;

                                    try { tmp2 = CaesarCipher.DecryptModule(encoded, candidate); } catch { details.Add($"{candidate}:caesarFail"); continue; }

                                    bool ascii = tmp2.All(ch => ch < 128);
                                    string decoded;

                                    try
                                    {
                                        var raw = Convert.FromBase64String(tmp2);
                                        var xored = new byte[raw.Length];

                                        for (int p = 0; p < raw.Length; p++)
                                            xored[p] = (byte)(raw[p] ^ context.HailKey[p % context.HailKey.Length]);

                                        string text = System.Text.Encoding.UTF8.GetString(xored);
                                        int repl = text.Count(ch => ch == '�');

                                        decoded = $"b64ok len={raw.Length} repl={repl} head={IlHelpers.Truncate(text, 30).Replace('\r', '?').Replace('\n', '?')}";
                                    }
                                    catch
                                    {
                                        decoded = $"b64fail ascii={ascii} len={tmp2.Length}";
                                    }

                                    details.Add($"{candidate}:{decoded}");
                                }

                                HydraLogger.Detail($"[v2-skip] {method.DeclaringType.Name}::{method.Name} len={encoded.Length} keys=[{string.Join(",", keys)}]");

                                foreach (var d in details)
                                    HydraLogger.Detail($"    {d}");
                            }
                            catch { }
                        }

                        continue;
                    }

                    ins.OpCode = OpCodes.Ldstr;
                    ins.Operand = best;

                    for (int p = i + 1; p <= l; p++)
                    {
                        var current = instrs[p];
                        var code = current.OpCode.Code;

                        if (code == Code.Call || code == Code.Callvirt || IlHelpers.IsLdcI4(current, out _) || code == Code.Nop || code == Code.Br || code == Code.Br_S)
                        {
                            current.OpCode = OpCodes.Nop;
                            current.Operand = null;
                        }
                    }

                    patched++;

                    if (bestIsShort || best!.Length == 0)
                    {
                        patchedShort++;

                        if (patchedShort <= 10)
                            HydraLogger.Detail($"[v2-short] {method.DeclaringType.Name}::{method.Name} = \"{best}\" keys=[{string.Join(",", keys)}]");
                    }
                }

                try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }
            }
        }

        HydraLogger.Success($"V2 patched={patched} short={patchedShort} tried={tried} skipped={implausible}");
    }

    private static bool IsSkippable(Instruction ins, IList<Instruction> instrs, Dictionary<Instruction, int> positions)
    {
        var code = ins.OpCode.Code;

        if (code == Code.Nop)
            return true;

        if (code == Code.Br || code == Code.Br_S)
        {
            if (ins.Operand is Instruction target && positions.TryGetValue(target, out int ti) && positions.TryGetValue(ins, out int si))
            {
                int next = si + 1;

                while (next < instrs.Count && instrs[next].OpCode.Code == Code.Nop)
                    next++;

                if (ti == si + 1 || ti == next)
                    return true;
            }
        }

        return false;
    }
}
