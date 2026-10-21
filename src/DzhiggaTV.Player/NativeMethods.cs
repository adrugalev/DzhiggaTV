using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;

namespace DzhiggaTV.Player;

internal static class NativeMethods
{
    private const string Library = "DzhiggaTV.libmpv";
    static NativeMethods() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        // Load only the trusted application directory, never a working directory or a playlist path.
        foreach (var directory in new[] { Path.Combine(AppContext.BaseDirectory, "native"), AppContext.BaseDirectory })
            foreach (var filename in new[] { "mpv-2.dll", "libmpv-2.dll", "mpv-1.dll", "libmpv-1.dll" })
            {
                var candidate = Path.Combine(directory, filename);
                if (!File.Exists(candidate)) continue;
                if (NativeLibrary.TryLoad(candidate, assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories, out var handle))
                    return handle;
            }
        throw new DllNotFoundException("Не найдена 64-разрядная библиотека libmpv. Поместите mpv-2.dll в папку native рядом с DzhiggaTV.exe.");
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mpv_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_initialize(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_terminate_destroy(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_option_string(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_set_property_string(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mpv_get_property_string(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_get_property(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out MpvNode value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")] internal static extern int GetInt64(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")] internal static extern int GetDouble(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_observe_property(IntPtr handle, ulong userdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_get_property_async(IntPtr handle, ulong userdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_command_async(IntPtr handle, ulong userdata, IntPtr args);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_abort_async_command(IntPtr handle, ulong userdata);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mpv_wait_event(IntPtr handle, double timeout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_wakeup(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free(IntPtr data);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void mpv_free_node_contents(ref MpvNode node);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int mpv_request_log_messages(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    internal static int Command(IntPtr handle, ulong id, params string[] arguments)
    {
        var strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var vector = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
        try
        {
            for (int i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(vector, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(vector, strings.Length * IntPtr.Size, IntPtr.Zero);
            return mpv_command_async(handle, id, vector);
        }
        finally
        {
            foreach (var str in strings) Marshal.FreeCoTaskMem(str);
            Marshal.FreeHGlobal(vector);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public int EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEndFile
{
    public int Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvPropertyEvent
{
    public IntPtr Name;
    public int Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct MpvNode
{
    [FieldOffset(0)] public IntPtr Pointer;
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public int Flag;
    [FieldOffset(0)] public double Double;
    [FieldOffset(8)] public int Format;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNodeList
{
    public int Count;
    public IntPtr Values;
    public IntPtr Keys;
}
