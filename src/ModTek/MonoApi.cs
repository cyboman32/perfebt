using System;
using System.Runtime.InteropServices;

namespace PerfeBT
{
    internal static class MonoApi
    {
        private const string Dll = "mono-2.0-bdwgc";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mono_domain_get();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mono_class_from_mono_type(IntPtr type);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mono_class_vtable(IntPtr domain, IntPtr klass);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mono_vtable_get_static_field_data(IntPtr vtable);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int mono_field_get_offset(IntPtr field);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint mono_object_get_size(IntPtr obj);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long mono_gc_get_heap_size();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long mono_gc_get_used_size();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void mono_unity_gc_disable();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void mono_unity_gc_enable();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int mono_unity_gc_is_disabled();

        internal static long ObjectSize(IntPtr obj) => mono_object_get_size(obj);
    }
}
