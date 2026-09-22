using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Analysis;

internal static class CentralTypeFinder
{
    public static void Execute(DeobfuscationContext context)
    {
        TypeDef? best = null;
        int bestScore = 0;

        foreach (var type in context.Module.GetTypes())
        {
            int score = ScoreCentralCandidate(type);

            if (score > bestScore)
            {
                bestScore = score;
                best = type;
            }
        }

        if (best != null)
        {
            context.CentralType = best;

            HydraLogger.Success($"Central type {best.FullName}");
            HydraLogger.Stats(("methods", best.Methods.Count.ToString()), ("fields", best.Fields.Count.ToString()));

            return;
        }

        HydraLogger.Warn("Central type not found");
    }

    private static int ScoreCentralCandidate(TypeDef type)
    {
        int maxStores = 0;
        int intGetters = 0;
        bool hasCaesar = false;
        bool hasBase64 = false;
        bool hasXorKey = false;

        foreach (var method in type.Methods)
        {
            if (method.HasBody)
            {
                int stores = 0;

                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code == Code.Stsfld)
                        stores++;
                }

                if (stores > maxStores)
                    maxStores = stores;
            }

            if (!method.IsStatic)
                continue;

            string ret = "";

            try { ret = method.ReturnType?.FullName ?? ""; } catch { continue; }

            int count = method.Parameters.Count;

            if (ret == "System.Int32" && count == 0 && method.HasBody)
            {
                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code == Code.Ldsfld)
                    {
                        intGetters++;
                        break;
                    }
                }
            }
            else if (ret == "System.String" && count == 6)
            {
                hasCaesar = true;
            }
            else if (ret == "System.String" && count == 1)
            {
                hasBase64 = true;
            }
            else if (ret == "System.Byte[]" && count == 0)
            {
                hasXorKey = true;
            }
        }

        if (maxStores < 20)
            return 0;

        int score = maxStores;

        score += intGetters * 2;

        if (hasCaesar)
            score += 25;

        if (hasBase64)
            score += 25;

        if (hasXorKey)
            score += 15;

        if (type.Fields.Count >= 20)
            score += 10;

        return score;
    }
}
