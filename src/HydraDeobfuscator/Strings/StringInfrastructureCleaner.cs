using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.ControlFlow;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Strings;

internal static class StringInfrastructureCleaner
{
    public static void Execute(DeobfuscationContext context)
    {
        int linearized = 0;
        int deleted = 0;
        int kept = 0;

        var infra = new List<MethodDef>();

        if (context.DecryptCaesarDef != null)
            infra.Add(context.DecryptCaesarDef);

        if (context.DecryptBase64Def != null)
            infra.Add(context.DecryptBase64Def);

        if (context.GetXorKeyDef != null)
            infra.Add(context.GetXorKeyDef);

        foreach (var method in infra)
        {
            try
            {
                int count = 0;

                for (int iter = 0; iter < 4; iter++)
                {
                    if (!StateDispatcherRemover.TryRemoveOne(method, context, out int n))
                        break;

                    count += n;

                    try { method.Body.SimplifyBranches(); method.Body.OptimizeBranches(); } catch { }
                }

                if (count > 0)
                {
                    linearized++;
                    HydraLogger.Detail($"[infra-clean] {method.Name} edges={count}");
                }
            }
            catch { }
        }

        var callers = new HashSet<MethodDef>();

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                        continue;

                    MethodDef? target = null;

                    try { target = (ins.Operand as IMethod)?.ResolveMethodDef(); } catch { }

                    if (target != null)
                        callers.Add(target);
                }
            }
        }

        foreach (var payload in context.DynamicPayloads.Keys.ToList())
        {
            try
            {
                if (callers.Contains(payload))
                {
                    kept++;
                    continue;
                }

                if (payload.DeclaringType != null && payload.DeclaringType.Methods.Contains(payload))
                {
                    payload.DeclaringType.Methods.Remove(payload);
                    deleted++;
                }
            }
            catch
            {
                kept++;
            }
        }

        HydraLogger.Success($"Infra clean linearized={linearized} deleted={deleted} kept={kept}");
    }
}
