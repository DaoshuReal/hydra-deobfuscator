using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HydraDeobfuscator.Il;

internal static class IlHelpers
{
    public static bool IsLdcI4(Instruction ins, out int value)
    {
        value = 0;

        switch (ins.OpCode.Code)
        {
            case Code.Ldc_I4_M1: value = -1; return true;
            case Code.Ldc_I4_0: value = 0; return true;
            case Code.Ldc_I4_1: value = 1; return true;
            case Code.Ldc_I4_2: value = 2; return true;
            case Code.Ldc_I4_3: value = 3; return true;
            case Code.Ldc_I4_4: value = 4; return true;
            case Code.Ldc_I4_5: value = 5; return true;
            case Code.Ldc_I4_6: value = 6; return true;
            case Code.Ldc_I4_7: value = 7; return true;
            case Code.Ldc_I4_8: value = 8; return true;
            case Code.Ldc_I4_S: value = ins.Operand is sbyte sb ? sb : Convert.ToInt32(ins.Operand); return true;
            case Code.Ldc_I4: value = Convert.ToInt32(ins.Operand); return true;

            default: return false;
        }
    }

    public static int GetLocalIndex(Instruction ins, CilBody body)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Ldloc_0:
            case Code.Stloc_0: return 0;
            case Code.Ldloc_1:
            case Code.Stloc_1: return 1;
            case Code.Ldloc_2:
            case Code.Stloc_2: return 2;
            case Code.Ldloc_3:
            case Code.Stloc_3: return 3;
            case Code.Ldloc_S:
            case Code.Stloc_S:
            case Code.Ldloc:
            case Code.Stloc:
            case Code.Ldloca_S:
            case Code.Ldloca:
                if (ins.Operand is Local l) return l.Index;

                break;
        }

        return -1;
    }

    public static bool IsRealBranch(Code c)
    {
        return c == Code.Brfalse || c == Code.Brfalse_S || c == Code.Brtrue || c == Code.Brtrue_S
            || c == Code.Beq || c == Code.Beq_S || c == Code.Bne_Un || c == Code.Bne_Un_S
            || c == Code.Bge || c == Code.Bge_S || c == Code.Bgt || c == Code.Bgt_S
            || c == Code.Ble || c == Code.Ble_S || c == Code.Blt || c == Code.Blt_S
            || c == Code.Bge_Un || c == Code.Bge_Un_S || c == Code.Bgt_Un || c == Code.Bgt_Un_S
            || c == Code.Ble_Un || c == Code.Ble_Un_S || c == Code.Blt_Un || c == Code.Blt_Un_S;
    }

    public static bool IsBranch(Instruction ins)
    {
        var c = ins.OpCode.Code;

        return c == Code.Br || c == Code.Br_S || IsRealBranch(c);
    }

    public static int ToInt32(object? o)
    {
        if (o == null) return 0;
        if (o is int i) return i;
        if (o is uint u) return unchecked((int)u);
        if (o is long l) return unchecked((int)l);
        if (o is ulong ul) return unchecked((int)ul);
        if (o is short s) return s;
        if (o is ushort us) return us;
        if (o is byte b) return b;
        if (o is sbyte sb) return sb;
        if (o is double d) return (int)d;
        if (o is float f) return (int)f;
        if (o is bool bl) return bl ? 1 : 0;

        try { return Convert.ToInt32(o); } catch { return 0; }
    }

    public static uint ToUInt32(object? o)
    {
        if (o is uint u) return u;

        return unchecked((uint)ToInt32(o));
    }

    public static int ToPlainInt(object o)
    {
        if (o is int i) return i;
        if (o is uint u) return unchecked((int)u);
        if (o is long l) return unchecked((int)l);
        if (o is bool b) return b ? 1 : 0;
        if (o is double d) return (int)d;

        return 0;
    }

    public static uint ToPlainUInt(object o)
    {
        if (o is uint u) return u;

        return unchecked((uint)ToPlainInt(o));
    }

    public static bool IsTrue(object? o)
    {
        if (o == null) return false;
        if (o is int i) return i != 0;
        if (o is uint u) return u != 0;
        if (o is long l) return l != 0;
        if (o is bool b) return b;
        if (o is double d) return d != 0;

        try { return Convert.ToInt32(o) != 0; } catch { return false; }
    }

    public static bool ValuesEqual(object? a, object? b) => ToInt32(a) == ToInt32(b);

    public static bool EvalBranch(Code code, object? a, object? b)
    {
        int ai = ToInt32(a), bi = ToInt32(b);
        uint au = unchecked((uint)ai), bu = unchecked((uint)bi);

        switch (code)
        {
            case Code.Beq_S:
            case Code.Beq: return ai == bi;
            case Code.Bne_Un_S:
            case Code.Bne_Un: return ai != bi;
            case Code.Bge_S:
            case Code.Bge: return ai >= bi;
            case Code.Bgt_S:
            case Code.Bgt: return ai > bi;
            case Code.Ble_S:
            case Code.Ble: return ai <= bi;
            case Code.Blt_S:
            case Code.Blt: return ai < bi;
            case Code.Bge_Un_S:
            case Code.Bge_Un: return au >= bu;
            case Code.Bgt_Un_S:
            case Code.Bgt_Un: return au > bu;
            case Code.Ble_Un_S:
            case Code.Ble_Un: return au <= bu;
            case Code.Blt_Un_S:
            case Code.Blt_Un: return au < bu;

            default: return false;
        }
    }

    public static string Truncate(string s, int n)
    {
        if (s.Length <= n) return s;

        return s.Substring(0, n) + "...";
    }

    public static bool IsPlausiblePlaintext(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 500 || s.Length < 2) return false;

        int asciiPrintable = 0, total = s.Length;

        foreach (char c in s)
        {
            if (c == '�') return false;

            if (c >= 32 && c <= 126) asciiPrintable++;
            else if (c == '\n' || c == '\r' || c == '\t') asciiPrintable++;
        }

        if ((double)asciiPrintable / total < 0.8) return false;

        int letters = s.Count(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'));

        if (letters < 2) return false;

        return true;
    }

    public static bool IsShortAcceptable(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 8) return false;

        foreach (char c in s)
        {
            if (c == '�') return false;
            if (c >= 32 && c <= 126) continue;
            if (c == '\b' || c == '\t' || c == '\n' || c == '\f' || c == '\r') continue;

            return false;
        }

        return true;
    }

    public static string ShortOperand(Instruction ins, IList<Instruction> instrs)
    {
        try
        {
            if (ins.Operand is Instruction ti) return "->" + instrs.IndexOf(ti);
            if (ins.Operand is IList<Instruction> lst) return "switch[" + string.Join(",", lst.Select(x => instrs.IndexOf(x))) + "]";
            if (ins.Operand == null) return "";

            string s = ins.Operand.ToString() ?? "";

            return s.Length > 60 ? s.Substring(0, 60) : s;
        }
        catch { return ""; }
    }

    public static Dictionary<uint, int> BuildOffsetIndex(IList<Instruction> instrs)
    {
        var map = new Dictionary<uint, int>(instrs.Count);

        for (int i = 0; i < instrs.Count; i++)
            map[instrs[i].Offset] = i;

        return map;
    }

    public static Dictionary<Instruction, int> BuildPositionIndex(IList<Instruction> instrs)
    {
        var map = new Dictionary<Instruction, int>(instrs.Count);

        for (int i = 0; i < instrs.Count; i++)
            map[instrs[i]] = i;

        return map;
    }

    public static HashSet<Instruction> CollectBranchTargets(MethodDef m)
    {
        var targets = new HashSet<Instruction>();

        foreach (var ins in m.Body.Instructions)
        {
            if (ins.Operand is Instruction ti) targets.Add(ti);
            else if (ins.Operand is IList<Instruction> lst) foreach (var x in lst) targets.Add(x);
        }

        foreach (var eh in m.Body.ExceptionHandlers)
        {
            if (eh.TryStart != null) targets.Add(eh.TryStart);
            if (eh.TryEnd != null) targets.Add(eh.TryEnd);
            if (eh.HandlerStart != null) targets.Add(eh.HandlerStart);
            if (eh.HandlerEnd != null) targets.Add(eh.HandlerEnd);
            if (eh.FilterStart != null) targets.Add(eh.FilterStart);
        }

        return targets;
    }

    public static bool IsCompilerGeneratedName(MethodDef m, string n)
    {
        if (n.StartsWith("<>")) return true;
        if (n.Contains("b__") || n.Contains("d__") || n.Contains("c__")) return true;

        string dt = "";

        try { dt = m.DeclaringType?.Name.String ?? ""; } catch { }

        if (dt.StartsWith("<>") || dt.Contains("DisplayClass") || dt.Contains("d__")) return true;

        return false;
    }
}
