using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HydraDeobfuscator.Core;
using HydraDeobfuscator.Il;

namespace HydraDeobfuscator.ControlFlow;

internal struct StateValue
{
    public bool Known;
    public int V;

    public static StateValue Unknown() => new StateValue { Known = false };
    public static StateValue Concrete(int v) => new StateValue { Known = true, V = v };
}

internal static class StatePropagator
{
    private const long UnknownState = long.MinValue;

    public static bool IsLenientFailure(string reason)
    {
        if (reason.StartsWith("walk-underflow")) return true;
        if (reason == "brH-empty-stack") return true;
        if (reason.StartsWith("brH-unknown-top")) return true;
        if (reason == "header-range") return true;
        if (reason.StartsWith("walk-call-effect:") && reason.Contains(":underflow:")) return true;

        return false;
    }

    public static bool Propagate(MethodDef method, int header, int switchIndex, IList<Instruction> targets, int count, int stateVar, int initState, bool hasInit, DeobfuscationContext context, out Dictionary<int, int> transitions, out Dictionary<int, int> siteStates, out string fail)
    {
        transitions = new Dictionary<int, int>();
        siteStates = new Dictionary<int, int>();
        fail = "unknown";

        try
        {
            var instrs = method.Body.Instructions;
            var positions = new Dictionary<Instruction, int>(instrs.Count);

            for (int i = 0; i < instrs.Count; i++)
                positions[instrs[i]] = i;

            Instruction headerIns = instrs[header];
            int afterDefault = switchIndex + 1;

            while (afterDefault < instrs.Count && instrs[afterDefault].OpCode.Code == Code.Nop)
                afterDefault++;

            if (afterDefault >= instrs.Count) { fail = "no-default"; return false; }

            int firstCase = (int)((uint)initState % (uint)count);
            int firstIndex = (firstCase >= 0 && firstCase < targets.Count && positions.TryGetValue(targets[firstCase], out int fi)) ? fi : afterDefault;

            var visited = new Dictionary<(int, long), List<StateValue>>();
            var indexStateCount = new Dictionary<int, int>();
            var queue = new Queue<(int index, bool known, int state, List<StateValue> stack)>();
            bool failed = false;
            string failReason = "";

            void Fail(string r) { failed = true; failReason = r; }

            bool Enqueue(int index, bool known, int state, List<StateValue> stack)
            {
                if (failed) return false;
                if (index < 0 || index >= instrs.Count) return true;
                if (stack.Count > 8) { Fail("cap-stack-depth"); return false; }

                long key = known ? (long)state : UnknownState;
                var id = (index, key);

                if (visited.TryGetValue(id, out var old))
                {
                    if (old.Count != stack.Count) { Fail("join-depth-mismatch"); return false; }

                    bool changed = false;

                    for (int k = 0; k < old.Count; k++)
                    {
                        if (old[k].Known && stack[k].Known && old[k].V == stack[k].V) continue;
                        if (!old[k].Known) continue;

                        old[k] = StateValue.Unknown();
                        changed = true;
                    }

                    if (changed)
                        queue.Enqueue((index, known, state, new List<StateValue>(old)));

                    return true;
                }

                int c = 0;

                indexStateCount.TryGetValue(index, out c);

                if (c >= 32) { Fail("cap-states-per-idx"); return false; }

                indexStateCount[index] = c + 1;
                visited[id] = new List<StateValue>(stack);
                queue.Enqueue((index, known, state, new List<StateValue>(stack)));

                return true;
            }

            if (hasInit) { if (!Enqueue(firstIndex, true, initState, new List<StateValue>())) { fail = failReason; return false; } }
            if (header != 0) { if (!Enqueue(0, false, 0, new List<StateValue>())) { fail = failReason; return false; } }
            if (!hasInit && header == 0) { fail = "no-init"; return false; }

            int steps = 0;
            const int maxSteps = 300000;

            while (queue.Count > 0)
            {
                if (failed) { fail = failReason; return false; }
                if (++steps > maxSteps) { fail = "cap-steps"; return false; }

                var (startIndex, startKnown, startState, startStack) = queue.Dequeue();
                var stack = new List<StateValue>(startStack);
                bool currentKnown = startKnown;
                int current = startState;
                int pc = startIndex;
                int guard = 0;

                while (true)
                {
                    if (failed) break;
                    if (++guard > 20000) { if (!startKnown && IsLenientFailure("cap-path")) { context.DemotedPathCount++; break; } fail = "cap-path"; return false; }
                    if (pc < 0 || pc >= instrs.Count) { if (!startKnown && IsLenientFailure("walk-range")) { context.DemotedPathCount++; break; } fail = "walk-range"; return false; }

                    if (pc == header)
                    {
                        if (stack.Count == 1 && stack[0].Known)
                        {
                            int ec = (int)((uint)stack[0].V % (uint)count);
                            int dest = (ec >= 0 && ec < targets.Count && positions.TryGetValue(targets[ec], out int di)) ? di : afterDefault;

                            if (!Enqueue(dest, true, stack[0].V, new List<StateValue>())) break;
                        }

                        break;
                    }

                    if (pc > header && pc <= switchIndex) { if (!startKnown && IsLenientFailure("header-range")) { context.DemotedPathCount++; break; } fail = "header-range"; return false; }

                    var ins = instrs[pc];
                    var code = ins.OpCode.Code;

                    if (ins.OpCode.OpCodeType == OpCodeType.Prefix || code == Code.Nop) { pc++; continue; }
                    if (IlHelpers.IsLdcI4(ins, out int lv)) { stack.Add(StateValue.Concrete(lv)); pc++; continue; }

                    if (code == Code.Ldc_R4 || code == Code.Ldc_R8 || code == Code.Ldc_I8)
                    {
                        stack.Add(StateValue.Unknown());
                        pc++;
                        continue;
                    }

                    if (code == Code.Sizeof)
                    {
                        string name = "";

                        try { name = (ins.Operand as ITypeDefOrRef)?.FullName ?? ""; } catch { }

                        int size = 4;

                        if (name.Contains("Double") || name.Contains("Int64") || name.Contains("UInt64")) size = 8;
                        else if (name.Contains("Byte") || name.Contains("SByte") || name.Contains("Boolean")) size = 1;
                        else if (name.Contains("Char") || name.Contains("Int16") || name.Contains("UInt16")) size = 2;

                        stack.Add(StateValue.Concrete(size));
                        pc++;
                        continue;
                    }

                    if (code == Code.Dup)
                    {
                        if (stack.Count < 1) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        stack.Add(stack[stack.Count - 1]);
                        pc++;
                        continue;
                    }

                    if (code == Code.Pop)
                    {
                        if (stack.Count < 1) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        stack.RemoveAt(stack.Count - 1);
                        pc++;
                        continue;
                    }

                    if (code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0 || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3)
                    {
                        int li = IlHelpers.GetLocalIndex(ins, method.Body);

                        if (li >= 0 && li == stateVar && stateVar >= 0)
                            stack.Add(currentKnown ? StateValue.Concrete(current) : StateValue.Unknown());
                        else
                            stack.Add(StateValue.Unknown());

                        pc++;
                        continue;
                    }

                    if (code == Code.Stloc || code == Code.Stloc_S || code == Code.Stloc_0 || code == Code.Stloc_1 || code == Code.Stloc_2 || code == Code.Stloc_3)
                    {
                        if (stack.Count < 1) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        var v = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        int li = IlHelpers.GetLocalIndex(ins, method.Body);

                        if (li >= 0 && li == stateVar && stateVar >= 0)
                        {
                            if (v.Known) { current = v.V; currentKnown = true; }
                            else { currentKnown = false; current = 0; }
                        }

                        pc++;
                        continue;
                    }

                    if (code == Code.Add || code == Code.Add_Ovf || code == Code.Add_Ovf_Un || code == Code.Sub || code == Code.Sub_Ovf || code == Code.Sub_Ovf_Un
                        || code == Code.Mul || code == Code.Mul_Ovf || code == Code.Mul_Ovf_Un || code == Code.And || code == Code.Or || code == Code.Xor
                        || code == Code.Shl || code == Code.Shr || code == Code.Shr_Un || code == Code.Div || code == Code.Div_Un || code == Code.Rem || code == Code.Rem_Un)
                    {
                        if (stack.Count < 2) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        var b = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        var a = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);

                        if (a.Known && b.Known)
                        {
                            int? r = null;

                            try
                            {
                                switch (code)
                                {
                                    case Code.Add: case Code.Add_Ovf: case Code.Add_Ovf_Un: r = unchecked(a.V + b.V); break;
                                    case Code.Sub: case Code.Sub_Ovf: case Code.Sub_Ovf_Un: r = unchecked(a.V - b.V); break;
                                    case Code.Mul: case Code.Mul_Ovf: case Code.Mul_Ovf_Un: r = unchecked(a.V * b.V); break;
                                    case Code.And: r = a.V & b.V; break;
                                    case Code.Or: r = a.V | b.V; break;
                                    case Code.Xor: r = a.V ^ b.V; break;
                                    case Code.Shl: r = a.V << (b.V & 31); break;
                                    case Code.Shr: r = a.V >> (b.V & 31); break;
                                    case Code.Shr_Un: r = unchecked((int)((uint)a.V >> (b.V & 31))); break;
                                    case Code.Div: case Code.Div_Un: if (b.V != 0) r = a.V / b.V; break;
                                    case Code.Rem: if (b.V != 0) r = a.V % b.V; break;
                                    case Code.Rem_Un: if (b.V != 0) r = unchecked((int)((uint)a.V % (uint)b.V)); break;
                                }
                            }
                            catch { r = null; }

                            stack.Add(r.HasValue ? StateValue.Concrete(r.Value) : StateValue.Unknown());
                        }
                        else
                        {
                            stack.Add(StateValue.Unknown());
                        }

                        pc++;
                        continue;
                    }

                    if (code == Code.Neg || code == Code.Not)
                    {
                        if (stack.Count < 1) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        var a = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(a.Known ? StateValue.Concrete(code == Code.Neg ? unchecked(-a.V) : ~a.V) : StateValue.Unknown());
                        pc++;
                        continue;
                    }

                    if (code == Code.Ceq || code == Code.Cgt || code == Code.Cgt_Un || code == Code.Clt || code == Code.Clt_Un)
                    {
                        if (stack.Count < 2) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        var b = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        var a = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);

                        if (a.Known && b.Known)
                        {
                            bool r = code == Code.Ceq ? a.V == b.V : code == Code.Cgt ? a.V > b.V : code == Code.Clt ? a.V < b.V
                                : code == Code.Cgt_Un ? unchecked((uint)a.V) > unchecked((uint)b.V) : unchecked((uint)a.V) < unchecked((uint)b.V);

                            stack.Add(StateValue.Concrete(r ? 1 : 0));
                        }
                        else
                        {
                            stack.Add(StateValue.Unknown());
                        }

                        pc++;
                        continue;
                    }

                    if (code == Code.Conv_I1 || code == Code.Conv_I2 || code == Code.Conv_I4 || code == Code.Conv_I8 || code == Code.Conv_U1
                        || code == Code.Conv_U2 || code == Code.Conv_U4 || code == Code.Conv_U8 || code == Code.Conv_I || code == Code.Conv_U
                        || code == Code.Conv_Ovf_I1 || code == Code.Conv_Ovf_I2 || code == Code.Conv_Ovf_I4 || code == Code.Conv_Ovf_I8
                        || code == Code.Conv_Ovf_U1 || code == Code.Conv_Ovf_U2 || code == Code.Conv_Ovf_U4 || code == Code.Conv_Ovf_U8
                        || code == Code.Conv_Ovf_I || code == Code.Conv_Ovf_U)
                    {
                        if (stack.Count < 1) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        var a = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);

                        if (!a.Known) { stack.Add(StateValue.Unknown()); pc++; continue; }

                        int r = a.V;

                        if (code == Code.Conv_I1 || code == Code.Conv_Ovf_I1) r = (int)(sbyte)r;
                        else if (code == Code.Conv_I2 || code == Code.Conv_Ovf_I2) r = (int)(short)r;
                        else if (code == Code.Conv_U1 || code == Code.Conv_Ovf_U1) r = r & 0xFF;
                        else if (code == Code.Conv_U2 || code == Code.Conv_Ovf_U2) r = r & 0xFFFF;

                        stack.Add(StateValue.Concrete(r));
                        pc++;
                        continue;
                    }

                    if (code == Code.Conv_R4 || code == Code.Conv_R8 || code == Code.Conv_R_Un)
                    {
                        if (stack.Count < 1) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        stack.RemoveAt(stack.Count - 1);
                        stack.Add(StateValue.Unknown());
                        pc++;
                        continue;
                    }

                    if (code == Code.Br || code == Code.Br_S || code == Code.Leave || code == Code.Leave_S)
                    {
                        var target = ins.Operand as Instruction;

                        if (target == null || !positions.TryGetValue(target, out int ti)) { if (!startKnown && IsLenientFailure("walk-bad-target")) { context.DemotedPathCount++; break; } fail = "walk-bad-target"; return false; }

                        if (target == headerIns)
                        {
                            if (stack.Count < 1) { if (!startKnown && IsLenientFailure("brH-empty-stack")) { context.DemotedPathCount++; break; } fail = "brH-empty-stack"; return false; }

                            var top = stack[stack.Count - 1];

                            if (!top.Known) { if (!startKnown && IsLenientFailure("brH-unknown-top")) { context.DemotedPathCount++; break; } fail = "brH-unknown-top"; return false; }

                            int next = top.V;

                            if (transitions.TryGetValue(pc, out int prev))
                            {
                                if (prev != next) { if (!startKnown && IsLenientFailure("brH-conflict")) { context.DemotedPathCount++; break; } fail = "brH-conflict"; return false; }
                            }
                            else
                            {
                                transitions[pc] = next;
                                siteStates[pc] = currentKnown ? current : 0;
                            }

                            int nc = (int)((uint)next % (uint)count);
                            int dest = (nc >= 0 && nc < targets.Count && positions.TryGetValue(targets[nc], out int di)) ? di : afterDefault;

                            if (!Enqueue(dest, true, next, new List<StateValue>())) break;

                            break;
                        }

                        if (!Enqueue(ti, currentKnown, current, new List<StateValue>(stack))) break;

                        break;
                    }

                    if (IlHelpers.IsRealBranch(code))
                    {
                        bool two = code == Code.Beq || code == Code.Beq_S || code == Code.Bne_Un || code == Code.Bne_Un_S
                            || code == Code.Bge || code == Code.Bge_S || code == Code.Bgt || code == Code.Bgt_S
                            || code == Code.Ble || code == Code.Ble_S || code == Code.Blt || code == Code.Blt_S
                            || code == Code.Bge_Un || code == Code.Bge_Un_S || code == Code.Bgt_Un || code == Code.Bgt_Un_S
                            || code == Code.Ble_Un || code == Code.Ble_Un_S || code == Code.Blt_Un || code == Code.Blt_Un_S;
                        int need = two ? 2 : 1;

                        if (stack.Count < need) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        var vals = new List<StateValue>();

                        for (int k = 0; k < need; k++) { vals.Add(stack[stack.Count - 1]); stack.RemoveAt(stack.Count - 1); }

                        var target = ins.Operand as Instruction;

                        if (target == null || !positions.TryGetValue(target, out int ti)) { if (!startKnown && IsLenientFailure("walk-bad-target")) { context.DemotedPathCount++; break; } fail = "walk-bad-target"; return false; }

                        bool allKnown = vals.All(v => v.Known);

                        if (allKnown)
                        {
                            bool take;

                            if (!two)
                            {
                                bool isTrue = vals[0].V != 0;
                                take = (code == Code.Brtrue || code == Code.Brtrue_S) ? isTrue : !isTrue;
                            }
                            else
                            {
                                take = IlHelpers.EvalBranch(code, vals[1].V, vals[0].V);
                            }

                            if (take) { if (!Enqueue(ti, currentKnown, current, new List<StateValue>(stack))) break; break; }

                            pc++;
                            continue;
                        }

                        if (!Enqueue(ti, currentKnown, current, new List<StateValue>(stack))) break;

                        pc++;
                        continue;
                    }

                    if (code == Code.Switch)
                    {
                        if (stack.Count < 1) { if (!startKnown && IsLenientFailure($"walk-underflow@{pc}:{code}|depth={stack.Count}")) { context.DemotedPathCount++; break; } fail = $"walk-underflow@{pc}:{code}|depth={stack.Count}"; return false; }

                        var k = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        var list = ins.Operand as IList<Instruction>;

                        if (list == null) { if (!startKnown && IsLenientFailure("walk-bad-switch")) { context.DemotedPathCount++; break; } fail = "walk-bad-switch"; return false; }
                        if (list.Count > 64) { if (!startKnown && IsLenientFailure("switch-cap")) { context.DemotedPathCount++; break; } fail = "switch-cap"; return false; }

                        if (k.Known)
                        {
                            int ki = unchecked((int)unchecked((uint)k.V));

                            if (ki >= 0 && ki < list.Count && positions.TryGetValue(list[ki], out int ti)) { if (!Enqueue(ti, currentKnown, current, new List<StateValue>(stack))) break; }
                            else if (!Enqueue(pc + 1, currentKnown, current, new List<StateValue>(stack))) break;
                        }
                        else
                        {
                            foreach (var item in list) { if (positions.TryGetValue(item, out int ti)) { if (!Enqueue(ti, currentKnown, current, new List<StateValue>(stack))) break; } if (failed) break; }

                            if (failed) break;
                            if (!Enqueue(pc + 1, currentKnown, current, new List<StateValue>(stack))) break;
                        }

                        break;
                    }

                    if (code == Code.Ret || code == Code.Throw || code == Code.Rethrow || code == Code.Endfinally || code == Code.Endfilter || code == Code.Jmp) break;

                    if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj || code == Code.Calli)
                    {
                        if (!ApplyCallEffect(ins, code, stack, out string detail))
                        {
                            string called = "?";

                            try { called = (ins.Operand as IMethod)?.Name.String ?? ins.Operand?.GetType().Name ?? "null"; } catch { }

                            string reason = "walk-call-effect:" + (ins.Operand?.GetType().Name ?? "null") + ":" + detail + ":" + called + "|depth=" + stack.Count;

                            if (!startKnown && IsLenientFailure(reason)) { context.DemotedPathCount++; break; }

                            fail = reason;

                            return false;
                        }

                        pc++;
                        continue;
                    }

                    if (!ApplyGenericEffect(ins, stack, out string genericDetail))
                    {
                        if (!startKnown && IsLenientFailure(genericDetail.StartsWith("underflow") ? $"walk-underflow@{pc}:{code}|depth={stack.Count}" : "walk-exotic-opcode:" + code.ToString())) { context.DemotedPathCount++; break; }

                        fail = genericDetail.StartsWith("underflow") ? $"walk-underflow@{pc}:{code}|depth={stack.Count}" : "walk-exotic-opcode:" + code.ToString();

                        return false;
                    }

                    pc++;
                    continue;
                }
            }

            return true;
        }
        catch { fail = "exception"; return false; }
    }

    public static bool ApplyCallEffect(Instruction ins, Code code, List<StateValue> stack, out string detail)
    {
        detail = "";

        try
        {
            MethodSig? sig = null;

            if (code == Code.Calli)
            {
                if (ins.Operand is MethodSig ms) sig = ms;
                else { detail = "calli-no-sig"; return false; }
            }
            else if (ins.Operand is IMethod mr)
            {
                try { sig = mr.MethodSig; } catch (Exception ex) { detail = "imethod-sig-throw:" + ex.GetType().Name; }
            }

            if (sig == null && ins.Operand is MemberRef mref)
            {
                try { sig = mref.Signature as MethodSig; } catch (Exception ex) { detail = "memberref-sig-throw:" + ex.GetType().Name; }

                if (sig == null && detail == "") detail = "memberref-no-sig";
            }

            if (sig == null && ins.Operand is MethodSpec mspec)
            {
                try { sig = (mspec.Method as IMethod)?.MethodSig; } catch (Exception ex) { detail = "methodspec-sig-throw:" + ex.GetType().Name; }

                if (sig == null && detail == "") detail = "methodspec-no-sig";
            }

            if (sig == null) { if (detail == "") detail = "sig-null:" + (ins.Operand?.GetType().Name ?? "null"); return false; }

            int pops = sig.Params.Count + ((sig.HasThis && code != Code.Newobj) ? 1 : 0);

            if (code == Code.Newobj)
                pops = sig.Params.Count;

            if (stack.Count < pops) { detail = $"underflow:need={pops}:depth={stack.Count}"; return false; }

            for (int k = 0; k < pops; k++)
                stack.RemoveAt(stack.Count - 1);

            bool hasReturn = true;

            try { hasReturn = sig.RetType.ElementType != ElementType.Void; } catch { hasReturn = true; }

            if (code == Code.Newobj)
                hasReturn = true;

            if (hasReturn)
                stack.Add(StateValue.Unknown());

            return true;
        }
        catch (Exception ex) { detail = "throw:" + ex.GetType().Name; return false; }
    }

    public static bool ApplyGenericEffect(Instruction ins, List<StateValue> stack, out string detail)
    {
        detail = "";

        try
        {
            int pops = -1;
            int pushes = -1;

            switch (ins.OpCode.Code)
            {
                case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
                case Code.Ldarg_S: case Code.Ldarg:
                case Code.Ldarga_S: case Code.Ldarga:
                case Code.Ldloca_S: case Code.Ldloca:
                case Code.Ldnull: case Code.Ldstr: case Code.Ldtoken: case Code.Ldftn:
                case Code.Ldsfld: case Code.Ldsflda:
                case Code.Arglist:
                    pops = 0; pushes = 1; break;
                case Code.Starg: case Code.Starg_S:
                    pops = 1; pushes = 0; break;
                case Code.Ldfld: case Code.Ldflda:
                    pops = 1; pushes = 1; break;
                case Code.Stsfld:
                    pops = 1; pushes = 0; break;
                case Code.Stfld:
                    pops = 2; pushes = 0; break;
                case Code.Newarr: case Code.Ldlen:
                    pops = 1; pushes = 1; break;
                case Code.Ldind_I1: case Code.Ldind_U1: case Code.Ldind_I2: case Code.Ldind_U2:
                case Code.Ldind_I4: case Code.Ldind_U4: case Code.Ldind_I8: case Code.Ldind_R4:
                case Code.Ldind_R8: case Code.Ldind_Ref: case Code.Ldind_I:
                    pops = 1; pushes = 1; break;
                case Code.Ldvirtftn:
                    pops = 1; pushes = 1; break;
                case Code.Stind_I1: case Code.Stind_I2: case Code.Stind_I4: case Code.Stind_I8:
                case Code.Stind_R4: case Code.Stind_R8: case Code.Stind_Ref: case Code.Stind_I:
                    pops = 2; pushes = 0; break;
                case Code.Ldelem_I: case Code.Ldelem_I1: case Code.Ldelem_I2: case Code.Ldelem_I4: case Code.Ldelem_I8:
                case Code.Ldelem_R4: case Code.Ldelem_R8: case Code.Ldelem_Ref: case Code.Ldelem_U1:
                case Code.Ldelem_U2: case Code.Ldelem_U4: case Code.Ldelem: case Code.Ldelema:
                    pops = 2; pushes = 1; break;
                case Code.Ldobj:
                case Code.Box: case Code.Unbox: case Code.Unbox_Any:
                case Code.Castclass: case Code.Isinst:
                case Code.Mkrefany: case Code.Refanyval: case Code.Refanytype:
                case Code.Localloc: case Code.Ckfinite:
                    pops = 1; pushes = 1; break;
                case Code.Stobj: case Code.Cpobj:
                    pops = 2; pushes = 0; break;
                case Code.Initobj:
                    pops = 1; pushes = 0; break;
                case Code.Stelem_I: case Code.Stelem_I1: case Code.Stelem_I2: case Code.Stelem_I4:
                case Code.Stelem_I8: case Code.Stelem_R4: case Code.Stelem_R8: case Code.Stelem_Ref:
                case Code.Stelem:
                case Code.Cpblk: case Code.Initblk:
                    pops = 3; pushes = 0; break;
                case Code.Break:
                    pops = 0; pushes = 0; break;

                default:
                    detail = "exotic";
                    return false;
            }

            if (stack.Count < pops) { detail = $"underflow:need={pops}:depth={stack.Count}"; return false; }

            for (int k = 0; k < pops; k++)
                stack.RemoveAt(stack.Count - 1);

            for (int k = 0; k < pushes; k++)
                stack.Add(StateValue.Unknown());

            return true;
        }
        catch (Exception ex) { detail = "throw:" + ex.GetType().Name; return false; }
    }
}
