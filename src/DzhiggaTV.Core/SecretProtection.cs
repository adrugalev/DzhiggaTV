using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DzhiggaTV.Core;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

/// <summary>Windows DPAPI, current-user scope. Private playlist data never falls back to clear text.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:v1:";
    public string Protect(string value) => Prefix + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public string Unprotect(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Unsupported encrypted data.");
        return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value[Prefix.Length..]), false));
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DzhiggaTV private storage requires Windows DPAPI.");
        var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
