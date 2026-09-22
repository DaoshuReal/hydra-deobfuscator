using System;
using dnlib.DotNet;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Logging;

namespace HydraDeobfuscator.Analysis;

internal static class HailKeyExtractor
{
    private static readonly byte[] Fallback = new byte[] { 0xAF, 0x6F, 0x0A, 0xEE, 0x1C, 0xBC, 0x5C, 0xF6, 0x0A, 0xCF, 0x42, 0x7E, 0xEE, 0x37, 0xFB, 0x53, 0x75, 0x29, 0x0D, 0x96, 0x23, 0xA8, 0x89, 0xA4, 0x8D, 0xFB, 0x39, 0x6F, 0x7F, 0x68, 0xFF, 0xF6 };

    public static void Execute(DeobfuscationContext context)
    {
        try
        {
            foreach (var resource in context.Module.Resources)
            {
                if (resource.Name != "HailHydra")
                    continue;

                if (resource is EmbeddedResource embedded)
                {
                    byte[] data = embedded.CreateReader().ReadBytes(32);

                    if (data == null || data.Length < 32)
                        throw new Exception("bad data len=" + (data == null ? -1 : data.Length));

                    HydraLogger.Detail($"HailHydra resource len={data.Length}");

                    for (int i = 0; i < data.Length && i < 32; i++)
                        context.HailKey[i] = (byte)(data[i] ^ 0xAA);

                    HydraLogger.Success($"Hail key {BitConverter.ToString(context.HailKey)}");

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            HydraLogger.Warn($"Hail key extraction failed: {ex.Message}");
        }

        context.HailKey = (byte[])Fallback.Clone();

        HydraLogger.Warn("Using fallback Hail key");
    }
}
