using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace PerfeBT
{
    // Decides whether running a type's static constructor early (as Harmony patching may do on Mono) is harmless.
    // Safe = the cctor, transitively, only calls BCL code, HBS loggers and constructors whose bodies are safe too,
    // and only touches statics of types that are themselves safe. Any other game/mod method call is treated as
    // depending on runtime state (UnityGameInstance, SimGame, databases) and makes the type ineligible.
    internal static class CctorScan
    {
        private const int MaxDepth = 4;

        private static readonly Dictionary<Type, bool> typeCache = new Dictionary<Type, bool>();
        private static readonly Dictionary<MethodBase, bool> methodCache = new Dictionary<MethodBase, bool>();
        private static readonly Dictionary<short, OpCode> opcodes = BuildOpcodes();

        internal static bool IsSideEffectFree(Type t) => TypeSafe(t, 0);

        private static bool TypeSafe(Type t, int depth)
        {
            if (t == null || IsBcl(t.Assembly))
                return true;
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
                t = t.GetGenericTypeDefinition();
            if (typeCache.TryGetValue(t, out bool cached))
                return cached;
            typeCache[t] = true; // cycle guard: a type referencing itself is judged by the rest of its cctor
            var cctor = t.TypeInitializer;
            bool safe = cctor == null || BodySafe(cctor, depth);
            typeCache[t] = safe;
            return safe;
        }

        private static bool BodySafe(MethodBase m, int depth)
        {
            if (methodCache.TryGetValue(m, out bool cached))
                return cached;
            if (depth > MaxDepth)
                return false;
            methodCache[m] = true; // recursion guard
            bool safe;
            try { safe = ScanIl(m, depth); }
            catch { safe = false; }
            methodCache[m] = safe;
            return safe;
        }

        private static bool ScanIl(MethodBase m, int depth)
        {
            var il = m.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
                return false;
            var module = m.Module;
            Type[] typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = m.IsGenericMethod ? m.GetGenericArguments() : null;

            int pos = 0;
            while (pos < il.Length)
            {
                short value = il[pos] == 0xFE ? (short)(0xFE00 | il[pos + 1]) : il[pos];
                pos += il[pos] == 0xFE ? 2 : 1;
                if (!opcodes.TryGetValue(value, out var op))
                    return false;

                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                    {
                        var callee = module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs);
                        if (!CallSafe(op, callee, depth))
                            return false;
                        break;
                    }
                    case OperandType.InlineField:
                    {
                        var field = module.ResolveField(BitConverter.ToInt32(il, pos), typeArgs, methodArgs);
                        if (field.IsStatic && field.DeclaringType != m.DeclaringType && !TypeSafe(field.DeclaringType, depth + 1))
                            return false;
                        break;
                    }
                }
                pos += OperandSize(op, il, pos);
            }
            return true;
        }

        private static bool CallSafe(OpCode op, MethodBase callee, int depth)
        {
            if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
                return true; // delegate creation does not execute the target
            var owner = callee.DeclaringType;
            if (owner == null)
                return false;
            if (IsBcl(owner.Assembly))
                // A virtual System.Object/interface call can dispatch into game overrides (ToString etc.).
                return !(op == OpCodes.Callvirt && callee.IsVirtual && (owner == typeof(object) || owner.IsInterface));
            if (owner.FullName == "HBS.Logging.Logger" && callee.Name == "GetLogger")
                return true;
            if (callee is ConstructorInfo)
                return TypeSafe(owner, depth + 1) && BodySafe(callee, depth + 1);
            return false;
        }

        private static bool IsBcl(Assembly a)
        {
            string n = a.GetName().Name;
            return n == "mscorlib" || n == "System" || n.StartsWith("System.", StringComparison.Ordinal);
        }

        private static int OperandSize(OpCode op, byte[] il, int pos)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, pos);
                default: return 4;
            }
        }

        private static Dictionary<short, OpCode> BuildOpcodes()
        {
            var map = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is OpCode op)
                    map[op.Value] = op;
            return map;
        }
    }
}
