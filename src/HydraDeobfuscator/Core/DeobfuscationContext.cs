using System.Collections.Generic;
using dnlib.DotNet;

namespace HydraDeobfuscator.Core;

internal sealed class DeobfuscationContext
{
    public ModuleDefMD Module { get; set; } = null!;
    public TypeDef? CentralType { get; set; }
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";

    public Dictionary<string, int> HailFieldValues { get; } = new(System.StringComparer.Ordinal);
    public Dictionary<MethodDef, int> HailGetterValues { get; } = new();
    public Dictionary<MethodDef, int> IntProxyValues { get; } = new();
    public Dictionary<MethodDef, string> StringProxyValues { get; } = new();
    public Dictionary<MethodDef, string> DynamicPayloads { get; } = new();

    public MethodDef? DecryptCaesarDef { get; set; }
    public MethodDef? DecryptBase64Def { get; set; }
    public MethodDef? GetXorKeyDef { get; set; }

    public byte[] HailKey { get; set; } = new byte[32];
    public int TotalMethods { get; set; }
    public int DemotedPathCount { get; set; }

    public Dictionary<string, int> DispatcherFailReasons { get; } = new();
    public List<string> DispatcherFailSamples { get; } = new();
    public List<string> DispatcherDoneSamples { get; } = new();
    public int DispatcherDebugDumps { get; set; }
    public int DispatcherFileDumps { get; set; }
}
