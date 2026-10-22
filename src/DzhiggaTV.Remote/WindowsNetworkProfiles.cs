using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DzhiggaTV.Remote;

[SupportedOSPlatform("windows")]
internal static class WindowsNetworkProfiles
{
    // Read-only vtable declarations from the Windows SDK netlistmgr.h. Avoid
    // IDispatch/dynamic enum argument conversion, which fails with E_INVALIDARG.
    public static HashSet<string> PrivateAdapters()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manager = (IManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!)!;
        IConnections? connections = null;
        try
        {
            connections = manager.GetNetworkConnections();
            while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1)
            {
                INetwork? network = null;
                try
                {
                    network = connection.GetNetwork();
                    if (network.GetCategory() is 1 or 2) result.Add(connection.GetAdapterId().ToString());
                }
                finally { if (network is not null) Marshal.ReleaseComObject(network); Marshal.ReleaseComObject(connection); }
            }
        }
        finally { if (connections is not null) Marshal.ReleaseComObject(connections); Marshal.ReleaseComObject(manager); }
        return result;
    }
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        INetwork GetNetwork(Guid id);
        IConnections GetNetworkConnections();
    }
    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNewEnum();
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IConnection connection, out uint fetched);
    }
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IConnection
    {
        INetwork GetNetwork();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }
    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        IConnections GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
        int GetConnectivity();
        int GetCategory();
    }
}
