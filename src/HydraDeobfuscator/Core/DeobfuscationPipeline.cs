using System.Linq;
using HydraDeobfuscator.Analysis;
using HydraDeobfuscator.Cleanup;
using HydraDeobfuscator.ControlFlow;
using HydraDeobfuscator.Logging;
using HydraDeobfuscator.Proxies;
using HydraDeobfuscator.Strings;

namespace HydraDeobfuscator.Core;

internal static class DeobfuscationPipeline
{
    public static void Run(DeobfuscationContext context)
    {
        HydraLogger.Phase("Analysis");

        using (HydraLogger.TimedStep("Loading metadata"))
        {
            HydraLogger.Info($"Input {context.InputPath}");
            HydraLogger.Stats(("types", context.Module.GetTypes().Count().ToString()), ("methods", context.Module.GetTypes().SelectMany(t => t.Methods).Count().ToString()));
        }

        using (HydraLogger.TimedStep("Resolving central type"))
            CentralTypeFinder.Execute(context);

        using (HydraLogger.TimedStep("Extracting Hail key"))
            HailKeyExtractor.Execute(context);

        using (HydraLogger.TimedStep("Extracting Hail fields"))
            HailFieldExtractor.Execute(context);

        using (HydraLogger.TimedStep("Mapping Hail getters"))
            HailGetterMapper.Execute(context);

        HydraLogger.Phase("Integer proxies");

        using (HydraLogger.TimedStep("Seeding proxies from names"))
            IntProxySeeder.Execute(context);

        using (HydraLogger.TimedStep("Emulating int proxies"))
            IntProxyEmulator.Execute(context);

        using (HydraLogger.TimedStep("Inlining int proxies"))
            IntProxyInliner.Execute(context);

        HydraLogger.Stats(("hailFields", context.HailFieldValues.Count.ToString()), ("intProxies", context.IntProxyValues.Count.ToString()));

        HydraLogger.Phase("Strings round 1");

        using (HydraLogger.TimedStep("Collecting string proxies"))
            StringProxyCollector.Execute(context);

        using (HydraLogger.TimedStep("Inlining string proxies"))
            StringProxyInliner.Execute(context);

        using (HydraLogger.TimedStep("Decrypting Module.Decrypt"))
            ModuleDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting combined calls"))
            CombinedDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting dynamic payloads"))
            StringDecryptorV2.Execute(context);

        using (HydraLogger.TimedStep("Peephole optimize"))
            PeepholeOptimizer.Execute(context);

        HydraLogger.Phase("Strings round 2");

        using (HydraLogger.TimedStep("Decrypting Module.Decrypt"))
            ModuleDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting combined calls"))
            CombinedDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting dynamic payloads"))
            StringDecryptorV2.Execute(context);

        using (HydraLogger.TimedStep("Removing state dispatchers"))
            StateDispatcherRemover.Execute(context);

        using (HydraLogger.TimedStep("Cleaning guarded dispatchers"))
            GuardedDispatcherCleaner.Execute(context);

        using (HydraLogger.TimedStep("Peephole optimize"))
            PeepholeOptimizer.Execute(context);

        using (HydraLogger.TimedStep("Removing dead code"))
            DeadCodeEliminator.Execute(context);

        HydraLogger.Phase("Strings round 3");

        using (HydraLogger.TimedStep("Decrypting Module.Decrypt"))
            ModuleDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting combined calls"))
            CombinedDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting dynamic payloads"))
            StringDecryptorV2.Execute(context);

        using (HydraLogger.TimedStep("Removing state dispatchers"))
            StateDispatcherRemover.Execute(context);

        using (HydraLogger.TimedStep("Peephole optimize"))
            PeepholeOptimizer.Execute(context);

        using (HydraLogger.TimedStep("Removing dead code"))
            DeadCodeEliminator.Execute(context);

        HydraLogger.Phase("Strings round 4");

        using (HydraLogger.TimedStep("Decrypting Module.Decrypt"))
            ModuleDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting combined calls"))
            CombinedDecryptor.Execute(context);

        using (HydraLogger.TimedStep("Decrypting dynamic payloads"))
            StringDecryptorV2.Execute(context);

        using (HydraLogger.TimedStep("Peephole optimize"))
            PeepholeOptimizer.Execute(context);

        using (HydraLogger.TimedStep("Removing dead code"))
            DeadCodeEliminator.Execute(context);

        using (HydraLogger.TimedStep("Collapsing header-only dispatchers"))
            HeaderOnlyCollapser.Execute(context);

        using (HydraLogger.TimedStep("Peephole optimize"))
            PeepholeOptimizer.Execute(context);

        using (HydraLogger.TimedStep("Removing dead code"))
            DeadCodeEliminator.Execute(context);

        HydraLogger.Phase("Final cleanup");

        ObfuscationReporter.Execute(context, "pre-junk");

        using (HydraLogger.TimedStep("Removing junk"))
            JunkRemover.Execute(context);

        using (HydraLogger.TimedStep("Cleaning string infrastructure"))
            StringInfrastructureCleaner.Execute(context);

        ObfuscationReporter.Execute(context, "post-junk");

        using (HydraLogger.TimedStep("Renaming"))
            Renamer.Execute(context);

        using (HydraLogger.TimedStep("Stripping anti-tamper"))
            AntiTamperStripper.Execute(context);
    }
}
