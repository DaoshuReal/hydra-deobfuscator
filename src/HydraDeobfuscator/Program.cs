using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

internal static class Program
{
    private static int Main(string[] args)
    {
        HydraLogger.Banner("1.0.0");

        bool verbose = args.Any(a => a == "--verbose" || a == "-v");

        HydraLogger.Verbose = verbose;

        if (args.Any(a => a == "--help" || a == "-h"))
        {
            PrintHelp();

            return 0;
        }

        var positionals = args.Where(a => a != "--verbose" && a != "-v").ToList();

        if (positionals.Count < 2)
        {
            HydraLogger.Error("Missing required arguments.");

            PrintHelp();

            return 1;
        }

        string input = positionals[0];
        string output = positionals[1];

        if (!File.Exists(input))
        {
            HydraLogger.Error($"Input not found: {input}");
            PrintHelp();

            return 1;
        }

        try
        {
            var context = new DeobfuscationContext
            {
                InputPath = input,
                OutputPath = output,
                Module = ModuleDefMD.Load(input)
            };

            DeobfuscationPipeline.Run(context);

            HydraLogger.Phase("Writing");

            using (HydraLogger.TimedStep($"Saving {output}"))
            {
                var options = new dnlib.DotNet.Writer.ModuleWriterOptions(context.Module);

                options.Logger = DummyLogger.NoThrowInstance;
                context.Module.Write(output, options);
            }

            HydraLogger.Footer(context.Module.GetTypes().Count(), context.Module.GetTypes().SelectMany(t => t.Methods).Count(), output);

            return 0;
        }
        catch (Exception ex)
        {
            HydraLogger.Error($"Fatal: {ex.Message}");
            HydraLogger.Detail(ex.ToString());

            return 2;
        }
    }

    private static void PrintHelp()
    {
        HydraLogger.Info("Usage: HydraDeobfuscator <input.dll> <output.dll> [--verbose]");
        HydraLogger.Detail("Example: HydraDeobfuscator mod.dll mod.clean.dll");
        HydraLogger.Detail("Flags: --verbose (-v) detailed logging, --help (-h) this help");
    }
}
