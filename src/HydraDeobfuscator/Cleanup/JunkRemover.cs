using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Cleanup;

internal static class JunkRemover
{
    public static void Execute(DeobfuscationContext context)
    {
        int removedMethods = 0;
        int removedTypes = 0;

        var toDelete = new HashSet<MethodDef>();

        foreach (var pair in context.IntProxyValues)
            toDelete.Add(pair.Key);

        foreach (var pair in context.StringProxyValues)
            toDelete.Add(pair.Key);

        var proxyTypes = new HashSet<TypeDef>();

        foreach (var method in toDelete)
        {
            if (method.DeclaringType != null)
                proxyTypes.Add(method.DeclaringType);
        }

        var junkTypes = new System.Collections.Generic.List<TypeDef>();
        var junkTypeNames = new HashSet<string>();

        void AddJunkType(TypeDef type)
        {
            if (type == null)
                return;

            if (type == context.CentralType)
                return;

            if (type.DeclaringType == context.CentralType)
                return;

            if (junkTypeNames.Contains(type.FullName))
                return;

            junkTypeNames.Add(type.FullName);
            junkTypes.Add(type);
        }

        foreach (var type in proxyTypes)
        {
            if (type == context.CentralType)
                continue;

            if (type.DeclaringType == context.CentralType)
                continue;

            bool allProxies = true;

            foreach (var method in type.Methods)
            {
                if (!toDelete.Contains(method))
                {
                    allProxies = false;
                    break;
                }
            }

            if (allProxies && type.Methods.Count > 0)
                AddJunkType(type);
        }

        var chainMethods = CollectChainMethods(context);

        foreach (var method in chainMethods)
        {
            if (method.DeclaringType != null)
                AddJunkType(method.DeclaringType);
        }

        foreach (var method in toDelete.ToList())
        {
            try
            {
                if (method.DeclaringType != null && method.DeclaringType.Methods.Contains(method))
                {
                    method.DeclaringType.Methods.Remove(method);
                    removedMethods++;
                }
            }
            catch { }
        }

        HydraLogger.Detail($"Removed proxy methods {removedMethods}");

        var removedNames = new HashSet<string>(junkTypeNames);

        foreach (var junk in junkTypes)
        {
            try
            {
                if (junk.DeclaringType != null)
                    junk.DeclaringType.NestedTypes.Remove(junk);
                else
                    context.Module.Types.Remove(junk);

                removedTypes++;
            }
            catch { }
        }

        HydraLogger.Detail($"Removed junk types {removedTypes}");

        var dummy = context.Module.GetTypes().FirstOrDefault(t => t.Name.String.StartsWith("dummy_ptr") || t.Namespace.String.StartsWith("dummy_ptr") || t.FullName.Contains("dummy_ptr"));

        if (dummy != null)
        {
            try
            {
                if (dummy.DeclaringType != null)
                    dummy.DeclaringType.NestedTypes.Remove(dummy);
                else
                    context.Module.Types.Remove(dummy);

                HydraLogger.Detail("Removed dummy_ptr");
            }
            catch { }
        }

        var hydraAttr = context.Module.GetTypes().FirstOrDefault(t => t.Name.String.StartsWith("HydraNoObfuscate"));

        if (hydraAttr != null)
        {
            bool used = false;

            foreach (var type in context.Module.GetTypes())
            {
                if (type.CustomAttributes.Any(a => a.AttributeType.Name.Contains("HydraNoObfuscate"))) { used = true; break; }

                foreach (var method in type.Methods)
                {
                    if (method.CustomAttributes.Any(a => a.AttributeType.Name.Contains("HydraNoObfuscate"))) { used = true; break; }
                }

                if (used)
                    break;
            }

            if (!used)
            {
                try
                {
                    context.Module.Types.Remove(hydraAttr);
                    HydraLogger.Detail("Removed HydraNoObfuscateAttribute");
                }
                catch { }
            }
        }

        foreach (var type in context.Module.GetTypes())
        {
            if (type.Name.String != "<Module>")
                continue;

            foreach (var method in type.Methods)
            {
                if (method.Name.String != ".cctor" || !method.HasBody)
                    continue;

                var instrs = method.Body.Instructions;

                for (int i = 0; i < instrs.Count; i++)
                {
                    var ins = instrs[i];

                    if (ins.OpCode.Code == Code.Call && ins.Operand is IMethod inner)
                    {
                        bool dead = false;

                        try
                        {
                            var resolved = inner.ResolveMethodDef();

                            if (resolved != null && (toDelete.Contains(resolved) || chainMethods.Contains(resolved)))
                                dead = true;
                        }
                        catch { }

                        if (!dead)
                        {
                            string owner = "";

                            try { owner = inner.DeclaringType?.FullName ?? ""; } catch { }

                            if (owner != "" && removedNames.Contains(owner))
                                dead = true;
                        }

                        if (dead)
                        {
                            ins.OpCode = OpCodes.Nop;
                            ins.Operand = null;
                            HydraLogger.Detail($"Nopped module cctor junk call {inner.Name}");
                        }
                    }
                }
            }
        }

        foreach (var type in context.Module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                try
                {
                    method.Body.SimplifyBranches();
                    method.Body.OptimizeBranches();
                }
                catch { }
            }
        }

        HydraLogger.Success($"Junk removed methods={removedMethods} types={removedTypes}");
    }

    private static HashSet<MethodDef> CollectChainMethods(DeobfuscationContext context)
    {
        var chain = new HashSet<MethodDef>();
        var queue = new System.Collections.Generic.Queue<MethodDef>();

        void EnqueueTarget(IMethod? reference)
        {
            if (reference == null)
                return;

            MethodDef? target = null;

            try { target = reference.ResolveMethodDef(); } catch { return; }

            if (target == null)
                return;

            if (!IsChainLink(target, context))
                return;

            if (chain.Add(target))
                queue.Enqueue(target);
        }

        foreach (var type in context.Module.GetTypes())
        {
            if (type.Name.String != "<Module>")
                continue;

            foreach (var method in type.Methods)
            {
                if (method.Name.String != ".cctor" || !method.HasBody)
                    continue;

                foreach (var ins in method.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                        continue;

                    EnqueueTarget(ins.Operand as IMethod);
                }
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!current.HasBody)
                continue;

            foreach (var ins in current.Body.Instructions)
            {
                if (ins.OpCode.Code != Code.Call && ins.OpCode.Code != Code.Callvirt)
                    continue;

                EnqueueTarget(ins.Operand as IMethod);
            }
        }

        return chain;
    }

    private static bool IsChainLink(MethodDef method, DeobfuscationContext context)
    {
        if (method == null)
            return false;

        if (!method.IsStatic)
            return false;

        if (!method.IsPrivateScope)
            return false;

        try
        {
            if (method.ReturnType?.FullName != "System.Void")
                return false;
        }
        catch
        {
            return false;
        }

        if (method.Parameters.Count != 0)
            return false;

        var type = method.DeclaringType;

        if (type == null)
            return false;

        if (type == context.CentralType)
            return false;

        if (type.DeclaringType == context.CentralType)
            return false;

        if (type.Fields.Count != 0)
            return false;

        foreach (var other in type.Methods)
        {
            if (!other.IsStatic)
                return false;

            try
            {
                if (other.ReturnType?.FullName != "System.Void")
                    return false;
            }
            catch
            {
                return false;
            }

            if (other.Parameters.Count != 0)
                return false;
        }

        return true;
    }
}
