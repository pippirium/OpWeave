#nullable enable

using System;
using System.Reflection;
using System.Reflection.Emit;
using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Computes the "Waitable implements Poll" metadata flag stage 6 reports on
    /// every Operation candidate (SPEC §C3 rule 3; consumed by
    /// <see cref="OpWeave.Core.Registry.RegistrySnapshotBuilder"/>, T-W1.5-04):
    /// <see cref="IOperation.Poll"/> is an interface member, so every class
    /// physically declares it — the flag distinguishes a real body from the
    /// "not implemented" placeholder the SPEC means by
    /// <c>Poll 未実装</c>: a method whose IL does nothing but throw a
    /// <see cref="NotSupportedException"/> or
    /// <see cref="NotImplementedException"/> (the documented shape Immediate
    /// Operations give <c>Poll</c> — "never polled").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check is metadata-only: the class is never constructed and the
    /// method never invoked (SPEC §C3 steps 2–3 discipline still holds at
    /// stage 6 for anything the eligibility screen rejected). The IL is
    /// decoded instruction by instruction; a body counts as the placeholder
    /// only when it is strictly linear — argument/local/constant loads,
    /// <c>newobj</c> of the two "not implemented" exception constructors and
    /// a final <c>throw</c> — with at least one such <c>newobj</c>. Any other
    /// content, an unreadable body, or an interface map the runtime cannot
    /// produce makes the flag <see langword="true"/>: only a body provably
    /// doing nothing but throwing the placeholder exceptions is reported as
    /// "not implemented", so a real Poll is never misreported and a genuinely
    /// stubbed Waitable is caught by OW-OP-003.
    /// </para>
    /// </remarks>
    internal static class PollImplementation
    {
        private static readonly OpCode?[] s_singleByte = new OpCode?[0x100];
        private static readonly OpCode?[] s_prefixed = new OpCode?[0x100];

        static PollImplementation()
        {
            foreach (var field in typeof(OpCodes).GetFields(
                BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode opCode)
                {
                    continue;
                }
                var value = unchecked((ushort)opCode.Value);
                if (value < 0x100)
                {
                    s_singleByte[value] = opCode;
                }
                else if ((value & 0xff00) == 0xfe00)
                {
                    s_prefixed[value & 0xff] = opCode;
                }
            }
        }

        /// <summary>
        /// True when <paramref name="operationType"/>'s <see cref="IOperation.Poll"/>
        /// is a real implementation; false when its body is the
        /// <see cref="NotSupportedException"/>/<see cref="NotImplementedException"/>
        /// placeholder described above.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="operationType"/> is null.</exception>
        internal static bool HasRealImplementation(Type operationType)
        {
            if (operationType is null)
            {
                throw new ArgumentNullException(nameof(operationType));
            }

            var poll = PollMethod(operationType);
            if (poll is null)
            {
                return true;
            }
            var il = poll.GetMethodBody()?.GetILAsByteArray();
            if (il is null || il.Length == 0)
            {
                return true;
            }
            return !IsPlaceholderBody(poll, operationType, il);
        }

        // The concrete method that implements IOperation.Poll on the class —
        // the interface map also reaches explicit interface implementations.
        private static MethodInfo? PollMethod(Type operationType)
        {
            InterfaceMapping map;
            try
            {
                map = operationType.GetInterfaceMap(typeof(IOperation));
            }
            catch (ArgumentException)
            {
                return null;
            }
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (string.Equals(
                    map.InterfaceMethods[i].Name, nameof(IOperation.Poll), StringComparison.Ordinal))
                {
                    return map.TargetMethods[i];
                }
            }
            return null;
        }

        // True only when the body is provably a placeholder: every instruction
        // is a load/Nop/Dup/Pop, a newobj of a "not implemented" exception
        // constructor, or the final throw — no branches, calls or returns —
        // and at least one such newobj is present. Anything ambiguous (a
        // token that will not resolve, a foreign newobj, malformed trailing
        // bytes, a real body) returns false.
        private static bool IsPlaceholderBody(MethodInfo method, Type operationType, byte[] il)
        {
            var sawThrow = false;
            var sawPlaceholderNewobj = false;
            var index = 0;
            while (index < il.Length)
            {
                OpCode opCode;
                var first = il[index++];
                if (first == 0xfe)
                {
                    if (index >= il.Length || s_prefixed[il[index]] is not { } prefixed)
                    {
                        return false;
                    }
                    index++;
                    opCode = prefixed;
                }
                else if (s_singleByte[first] is { } single)
                {
                    opCode = single;
                }
                else
                {
                    return false;
                }

                if (opCode == OpCodes.Throw)
                {
                    sawThrow = true;
                }
                else if (opCode == OpCodes.Newobj)
                {
                    if (!IsPlaceholderConstructor(
                        method, operationType, ReadOperand(il, index)))
                    {
                        return false;
                    }
                    sawPlaceholderNewobj = true;
                }
                else if (opCode.FlowControl != FlowControl.Next || !IsLoadOrBookkeeping(opCode))
                {
                    return false;
                }

                var operand = OperandSize(opCode, il, index);
                if (operand < 0)
                {
                    return false;
                }
                index += operand;
            }
            return sawThrow && sawPlaceholderNewobj;
        }

        // The opcode set a placeholder's linear prologue may contain beyond
        // newobj/throw: argument, local and constant loads, Nop, Dup and Pop.
        private static bool IsLoadOrBookkeeping(OpCode opCode) =>
            opCode == OpCodes.Nop || opCode == OpCodes.Dup || opCode == OpCodes.Pop ||
            opCode == OpCodes.Ldnull || opCode == OpCodes.Ldstr ||
            opCode == OpCodes.Ldarg || opCode == OpCodes.Ldarg_S ||
            opCode == OpCodes.Ldarg_0 || opCode == OpCodes.Ldarg_1 ||
            opCode == OpCodes.Ldarg_2 || opCode == OpCodes.Ldarg_3 ||
            opCode == OpCodes.Ldloc || opCode == OpCodes.Ldloc_S ||
            opCode == OpCodes.Ldloc_0 || opCode == OpCodes.Ldloc_1 ||
            opCode == OpCodes.Ldloc_2 || opCode == OpCodes.Ldloc_3 ||
            opCode == OpCodes.Stloc || opCode == OpCodes.Stloc_S ||
            opCode == OpCodes.Stloc_0 || opCode == OpCodes.Stloc_1 ||
            opCode == OpCodes.Stloc_2 || opCode == OpCodes.Stloc_3 ||
            opCode == OpCodes.Ldc_I4 || opCode == OpCodes.Ldc_I4_S ||
            opCode == OpCodes.Ldc_I4_0 || opCode == OpCodes.Ldc_I4_1 ||
            opCode == OpCodes.Ldc_I4_2 || opCode == OpCodes.Ldc_I4_3 ||
            opCode == OpCodes.Ldc_I4_4 || opCode == OpCodes.Ldc_I4_5 ||
            opCode == OpCodes.Ldc_I4_6 || opCode == OpCodes.Ldc_I4_7 ||
            opCode == OpCodes.Ldc_I4_8 || opCode == OpCodes.Ldc_I4_M1 ||
            opCode == OpCodes.Ldc_I8 || opCode == OpCodes.Ldc_R4 || opCode == OpCodes.Ldc_R8;

        // The newobj operand's 4-byte metadata token at the operand offset.
        private static int ReadOperand(byte[] il, int index)
        {
            if (index + 4 > il.Length)
            {
                return -1;
            }
            return il[index] | (il[index + 1] << 8) | (il[index + 2] << 16) | (il[index + 3] << 24);
        }

        // True when the metadata token resolves to a constructor of
        // NotSupportedException or NotImplementedException; unresolvable or
        // foreign members return false.
        private static bool IsPlaceholderConstructor(MethodInfo method, Type operationType, int token)
        {
            if (token < 0)
            {
                return false;
            }
            try
            {
                var typeArguments = operationType.IsGenericType
                    ? operationType.GetGenericArguments()
                    : null;
                var methodArguments = method.IsGenericMethod
                    ? method.GetGenericArguments()
                    : null;
                return method.Module.ResolveMember(token, typeArguments, methodArguments)
                    is ConstructorInfo constructor &&
                    (constructor.DeclaringType == typeof(NotSupportedException) ||
                        constructor.DeclaringType == typeof(NotImplementedException));
            }
            catch (Exception)
            {
                // An unresolvable token can never prove a placeholder; the
                // flag stays "implemented" — the conservative direction.
                return false;
            }
        }

        // The operand byte count at <paramref name="index"/>, or -1 when the
        // operand would run past the IL (malformed input).
        private static int OperandSize(OpCode opCode, byte[] il, int index)
        {
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineVar:
                    return index + 1 <= il.Length ? 1 : -1;
                case OperandType.InlineVar:
                    return index + 2 <= il.Length ? 2 : -1;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    return index + 4 <= il.Length ? 4 : -1;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return index + 8 <= il.Length ? 8 : -1;
                case OperandType.InlineSwitch:
                    if (index + 4 > il.Length)
                    {
                        return -1;
                    }
                    var count = ReadOperand(il, index);
                    if (count < 0 || (long)index + 4 + ((long)count * 4) > il.Length)
                    {
                        return -1;
                    }
                    return 4 + (count * 4);
                default:
                    return -1;
            }
        }
    }
}
