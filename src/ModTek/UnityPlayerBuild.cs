using System;
using System.Runtime.InteropServices;

namespace PerfeBT
{
    // Engine internals that UnityPlayer.dll does not export (DelayedCallManager) are reached by address. The addresses
    // are for the game's one shipped build (Unity 2018.4, Windows x64 mono player): its PE timestamp and image size are
    // checked, and every caller checks the bytes of each function it reads or hooks before touching anything.
    internal static class UnityPlayerBuild
    {
        internal const string Module = "UnityPlayer.dll";
        private const int TimeDateStamp = 0x5cf7e9ab;
        private const int SizeOfImage = 0x16c0000;

        // DelayedCallManager (GetDelayedCallManager = GetManagerPtrFromContext(8): [manager array + 8 * 8]).
        // std::multiset<Callback> head node at +0x30, size at +0x38 (CancelCallDelayed).
        // Tree node: left +0x00, parent +0x08, right +0x10, isnil byte +0x19, value (Callback) from +0x20:
        // userData +0x38, callback +0x40, cleanup +0x48, object instance ID +0x50 (CancelCallDelayed, Remove).
        internal const int RvaGetManagerPtrFromContext = 0x629450;
        internal const int RvaManagerArray = 0x1503a50;
        internal const int ManagerContextDelayedCall = 8;
        internal const int ManagerHead = 0x30, ManagerSize = 0x38;
        internal const int NodeLeft = 0x00, NodeParent = 0x08, NodeRight = 0x10, NodeIsNil = 0x19;
        internal const int NodeValue = 0x20, NodeUserData = 0x38, NodeCallback = 0x40, NodeObjectId = 0x50;

        // movsxd rax,ecx; lea rcx,[rip+0xeda5f6]; mov rax,[rcx+rax*8]; ret
        private static readonly byte[] GetManagerBytes = { 0x48, 0x63, 0xc1, 0x48, 0x8d, 0x0d, 0xf6, 0xa5, 0xed, 0x00, 0x48, 0x8b, 0x04, 0xc1, 0xc3 };

        private static IntPtr baseAddress;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        // Module base after the build checks; throws when this is not the expected build.
        internal static IntPtr Base()
        {
            if (baseAddress != IntPtr.Zero)
                return baseAddress;
            var b = GetModuleHandle(Module);
            if (b == IntPtr.Zero)
                throw new DllNotFoundException(Module + " not loaded");
            int peOffset = Marshal.ReadInt32(b, 0x3c);
            int stamp = Marshal.ReadInt32(b, peOffset + 8);
            int size = Marshal.ReadInt32(b, peOffset + 0x50);
            if (stamp != TimeDateStamp || size != SizeOfImage)
                throw new NotSupportedException($"{Module} build {stamp:x8}/{size:x} is not the one the addresses are for");
            Check(b, RvaGetManagerPtrFromContext, GetManagerBytes, "GetManagerPtrFromContext");
            return baseAddress = b;
        }

        internal static void Check(IntPtr b, int rva, byte[] expected, string what)
        {
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(b, rva + i) != expected[i])
                    throw new NotSupportedException(what + " bytes differ");
        }

        internal static IntPtr DelayedCallManager(IntPtr b) =>
            Marshal.ReadIntPtr(b, RvaManagerArray + ManagerContextDelayedCall * IntPtr.Size);
    }
}
