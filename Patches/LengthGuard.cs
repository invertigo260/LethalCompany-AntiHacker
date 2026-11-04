using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using Unity.Netcode;

namespace AntiHacker.Patches
{
    /// <summary>
    /// Each RPC handler gets a check in front of its array, string, and NativeArray reads (adapted from LobbyGuard).
    /// A length that can't fit in what's left of the message drops the message, auto-bans and disconnects the sender.
    /// </summary>
    internal static class LengthGuard
    {
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly List<ILHook> hooks = new List<ILHook>();

        private static readonly MethodInfo check = typeof(LengthGuard).GetMethod(nameof(Check), Statics);

        public static void HookAll()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Hook(e.LoadedAssembly);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Hook(assembly);
            }
        }

        public static bool Fits(int count, int size, int remaining) => count >= 0 && (long)count * size <= remaining;

        private static unsafe void Check(ref FastBufferReader reader, int size, __RpcParams rpc)
        {
            int remaining = reader.Length - reader.Position - sizeof(int);
            // Too short for a length at all: NGO throws on its own.
            if (remaining < 0 || Fits(*(int*)reader.GetUnsafePtrAtCurrentPosition(), size, remaining))
            {
                return;
            }
            ulong sender = rpc.Server.Receive.SenderClientId;
            NetworkManager? manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer && sender != NetworkManager.ServerClientId)
            {
                Security.Punish(sender, "RPC array longer than its message");
            }
            throw new OverflowException("RPC array longer than its message");
        }

        private static void Hook(Assembly assembly)
        {
            try
            {
                HookTypes(assembly);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Skipped RPC length checks in {assembly.GetName().Name}: {e.Message}");
            }
        }

        private static void HookTypes(Assembly assembly)
        {
            if (assembly.IsDynamic || assembly == typeof(NetworkBehaviour).Assembly || !References(assembly))
            {
                return;
            }
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            foreach (Type? type in types)
            {
                if (type == null || !Derives(type, typeof(NetworkBehaviour)))
                {
                    continue;
                }
                try
                {
                    hooks.AddRange(type.GetMethods(Statics | BindingFlags.DeclaredOnly)
                        .Where(handler => handler.Name.StartsWith("__rpc_handler_", StringComparison.Ordinal) && ReadsLength(handler))
                        .Select(handler => new ILHook(handler, Guard)));
                }
                catch (Exception e) when (Missing(e))
                {
                }
            }
        }

        internal static bool Derives(Type type, Type from)
        {
            try
            {
                return from.IsAssignableFrom(type);
            }
            catch (Exception e) when (Missing(e))
            {
                return false;
            }
        }

        private static bool Missing(Exception e) => e is TypeLoadException or System.IO.FileNotFoundException or System.IO.FileLoadException;

        private static bool References(Assembly assembly) =>
            assembly.GetReferencedAssemblies().Any(a => a.Name == typeof(NetworkBehaviour).Assembly.GetName().Name);

        private static bool ReadsLength(MethodInfo handler)
        {
            byte[]? il = handler.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                return false;
            }
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28)
                {
                    continue;
                }
                try
                {
                    if (ElementSize(handler.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1))) != null)
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            return false;
        }

        private static void Guard(ILContext il)
        {
            ParameterDefinition reader = il.Method.Parameters[1];
            ParameterDefinition rpc = il.Method.Parameters[2];
            var cursor = new ILCursor(il);
            while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Call && i.Operand is MethodReference))
            {
                MethodBase? read = null;
                try
                {
                    read = ((MethodReference)cursor.Next.Operand).ResolveReflection();
                }
                catch (Exception)
                {
                }
                if (ElementSize(read) is int size)
                {
                    cursor.Emit(OpCodes.Ldarga, reader);
                    cursor.Emit(OpCodes.Ldc_I4, size);
                    cursor.Emit(OpCodes.Ldarg, rpc);
                    cursor.Emit(OpCodes.Call, check);
                }
                cursor.Index++;
            }
        }

        private static int? ElementSize(MethodBase? read)
        {
            if (read?.DeclaringType != typeof(FastBufferReader) || !read.Name.StartsWith("Read", StringComparison.Ordinal))
            {
                return null;
            }
            ParameterInfo[] parameters = read.GetParameters();
            if (parameters.Length == 0 || !parameters[0].ParameterType.IsByRef)
            {
                return null;
            }
            Type target = parameters[0].ParameterType.GetElementType()!;
            if (target == typeof(string))
            {
                return 1;
            }
            Type? element = target.IsArray ? target.GetElementType()
                : target.IsGenericType && target.GetGenericTypeDefinition().FullName == "Unity.Collections.NativeArray`1"
                    ? target.GetGenericArguments()[0]
                    : null;
            if (element == null)
            {
                return null;
            }
            if (!element.IsValueType || typeof(INetworkSerializable).IsAssignableFrom(element))
            {
                return 1;
            }
            return (int)typeof(LengthGuard).GetMethod(nameof(SizeOf), Statics).MakeGenericMethod(element).Invoke(null, null);
        }

        private static unsafe int SizeOf<T>() where T : unmanaged => sizeof(T);
    }
}

