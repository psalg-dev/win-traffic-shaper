using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ShapeTraffic.Infrastructure.Services;

public interface IConnectionOwnerResolver
{
    bool TryResolveOwner(string localAddress, ushort localPort, string remoteAddress, ushort remotePort, byte protocol, out int processId);
}

internal static class ConnectionKeyFormatter
{
    public static readonly string IPv4Any = FromIpAddress(IPAddress.Any);
    public static readonly string IPv6Any = FromIpAddress(IPAddress.IPv6Any);

    public static string FromIpAddress(IPAddress address)
    {
        var normalized = address.MapToIPv6();
        Span<byte> bytes = stackalloc byte[16];
        normalized.TryWriteBytes(bytes, out _);
        return Convert.ToHexString(bytes);
    }
}

internal sealed class WindowsConnectionOwnerResolver : IConnectionOwnerResolver
{
    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private ConnectionOwnerSnapshot _snapshot = ConnectionOwnerSnapshot.Empty;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.MinValue;

    public bool TryResolveOwner(string localAddress, ushort localPort, string remoteAddress, ushort remotePort, byte protocol, out int processId)
    {
        EnsureSnapshot(DateTimeOffset.UtcNow);

        lock (_gate)
        {
            return _snapshot.TryResolve(localAddress, localPort, remoteAddress, remotePort, protocol, out processId);
        }
    }

    private void EnsureSnapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _lastRefreshAt < RefreshInterval)
            {
                return;
            }

            _snapshot = ConnectionOwnerSnapshot.Create();
            _lastRefreshAt = now;
        }
    }

    private static IEnumerable<MibTcpRowOwnerPid> ReadTcpRows(bool ipv6)
    {
        return ReadTable(
            (IntPtr buffer, ref int size) => IpHelperNative.GetExtendedTcpTable(buffer, ref size, false, ipv6 ? AddressFamilyInet6 : AddressFamilyInet, TcpTableClass.OwnerPidAll, 0),
            ipv6 ? Marshal.SizeOf<MibTcpRowOwnerPidV6>() : Marshal.SizeOf<MibTcpRowOwnerPidV4>(),
            static (buffer, offset, isIpv6) => isIpv6
                ? new MibTcpRowOwnerPid(Marshal.PtrToStructure<MibTcpRowOwnerPidV6>(IntPtr.Add(buffer, offset)))
                : new MibTcpRowOwnerPid(Marshal.PtrToStructure<MibTcpRowOwnerPidV4>(IntPtr.Add(buffer, offset))),
            ipv6);
    }

    private static IEnumerable<MibUdpRowOwnerPid> ReadUdpRows(bool ipv6)
    {
        return ReadTable(
            (IntPtr buffer, ref int size) => IpHelperNative.GetExtendedUdpTable(buffer, ref size, false, ipv6 ? AddressFamilyInet6 : AddressFamilyInet, UdpTableClass.OwnerPid, 0),
            ipv6 ? Marshal.SizeOf<MibUdpRowOwnerPidV6>() : Marshal.SizeOf<MibUdpRowOwnerPidV4>(),
            static (buffer, offset, isIpv6) => isIpv6
                ? new MibUdpRowOwnerPid(Marshal.PtrToStructure<MibUdpRowOwnerPidV6>(IntPtr.Add(buffer, offset)))
                : new MibUdpRowOwnerPid(Marshal.PtrToStructure<MibUdpRowOwnerPidV4>(IntPtr.Add(buffer, offset))),
            ipv6);
    }

    private static IEnumerable<TRow> ReadTable<TRow>(TableReader reader, int rowSize, Func<IntPtr, int, bool, TRow> projector, bool ipv6)
    {
        var size = 0;
        var result = reader(IntPtr.Zero, ref size);
        if (result != ErrorInsufficientBuffer || size <= 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = reader(buffer, ref size);
            if (result != 0)
            {
                yield break;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var offset = sizeof(int);
            for (var index = 0; index < rowCount; index++)
            {
                yield return projector(buffer, offset, ipv6);
                offset += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate uint TableReader(IntPtr buffer, ref int size);

    private enum TcpTableClass
    {
        OwnerPidAll = 5,
    }

    private enum UdpTableClass
    {
        OwnerPid = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPidV4
    {
        public uint State;
        public uint LocalAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LocalPort;
        public uint RemoteAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPidV6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPidV4
    {
        public uint LocalAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPidV6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LocalPort;
        public uint OwningPid;
    }

    private readonly record struct MibTcpRowOwnerPid(string LocalAddress, ushort LocalPort, string RemoteAddress, ushort RemotePort, int ProcessId)
    {
        public MibTcpRowOwnerPid(MibTcpRowOwnerPidV4 row)
            : this(
                ConnectionKeyFormatter.FromIpAddress(new IPAddress(row.LocalAddr)),
                ReadPort(row.LocalPort),
                ConnectionKeyFormatter.FromIpAddress(new IPAddress(row.RemoteAddr)),
                ReadPort(row.RemotePort),
                unchecked((int)row.OwningPid))
        {
        }

        public MibTcpRowOwnerPid(MibTcpRowOwnerPidV6 row)
            : this(
                ConnectionKeyFormatter.FromIpAddress(new IPAddress(row.LocalAddr, row.LocalScopeId)),
                ReadPort(row.LocalPort),
                ConnectionKeyFormatter.FromIpAddress(new IPAddress(row.RemoteAddr, row.RemoteScopeId)),
                ReadPort(row.RemotePort),
                unchecked((int)row.OwningPid))
        {
        }
    }

    private readonly record struct MibUdpRowOwnerPid(string LocalAddress, ushort LocalPort, int ProcessId)
    {
        public MibUdpRowOwnerPid(MibUdpRowOwnerPidV4 row)
            : this(
                ConnectionKeyFormatter.FromIpAddress(new IPAddress(row.LocalAddr)),
                ReadPort(row.LocalPort),
                unchecked((int)row.OwningPid))
        {
        }

        public MibUdpRowOwnerPid(MibUdpRowOwnerPidV6 row)
            : this(
                ConnectionKeyFormatter.FromIpAddress(new IPAddress(row.LocalAddr, row.LocalScopeId)),
                ReadPort(row.LocalPort),
                unchecked((int)row.OwningPid))
        {
        }
    }

    private readonly record struct TcpLookupKey(string LocalAddress, ushort LocalPort, string RemoteAddress, ushort RemotePort);

    private readonly record struct UdpLookupKey(string LocalAddress, ushort LocalPort);

    private sealed class ConnectionOwnerSnapshot
    {
        public static readonly ConnectionOwnerSnapshot Empty = new(new Dictionary<TcpLookupKey, int>(), new Dictionary<UdpLookupKey, int>());

        private readonly IReadOnlyDictionary<TcpLookupKey, int> _tcpOwners;
        private readonly IReadOnlyDictionary<UdpLookupKey, int> _udpOwners;

        private ConnectionOwnerSnapshot(IReadOnlyDictionary<TcpLookupKey, int> tcpOwners, IReadOnlyDictionary<UdpLookupKey, int> udpOwners)
        {
            _tcpOwners = tcpOwners;
            _udpOwners = udpOwners;
        }

        public static ConnectionOwnerSnapshot Create()
        {
            var tcpOwners = new Dictionary<TcpLookupKey, int>();
            var udpOwners = new Dictionary<UdpLookupKey, int>();

            foreach (var row in ReadTcpRows(ipv6: false).Concat(ReadTcpRows(ipv6: true)))
            {
                tcpOwners[new TcpLookupKey(row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort)] = row.ProcessId;
            }

            foreach (var row in ReadUdpRows(ipv6: false).Concat(ReadUdpRows(ipv6: true)))
            {
                udpOwners[new UdpLookupKey(row.LocalAddress, row.LocalPort)] = row.ProcessId;
            }

            return new ConnectionOwnerSnapshot(tcpOwners, udpOwners);
        }

        public bool TryResolve(string localAddress, ushort localPort, string remoteAddress, ushort remotePort, byte protocol, out int processId)
        {
            if (protocol == 6)
            {
                return TryResolveTcp(localAddress, localPort, remoteAddress, remotePort, out processId);
            }

            if (protocol == 17)
            {
                return TryResolveUdp(localAddress, localPort, out processId);
            }

            processId = 0;
            return false;
        }

        private bool TryResolveTcp(string localAddress, ushort localPort, string remoteAddress, ushort remotePort, out int processId)
        {
            if (_tcpOwners.TryGetValue(new TcpLookupKey(localAddress, localPort, remoteAddress, remotePort), out processId))
            {
                return true;
            }

            if (_tcpOwners.TryGetValue(new TcpLookupKey(ConnectionKeyFormatter.IPv4Any, localPort, remoteAddress, remotePort), out processId))
            {
                return true;
            }

            if (_tcpOwners.TryGetValue(new TcpLookupKey(ConnectionKeyFormatter.IPv6Any, localPort, remoteAddress, remotePort), out processId))
            {
                return true;
            }

            processId = 0;
            return false;
        }

        private bool TryResolveUdp(string localAddress, ushort localPort, out int processId)
        {
            if (_udpOwners.TryGetValue(new UdpLookupKey(localAddress, localPort), out processId))
            {
                return true;
            }

            if (_udpOwners.TryGetValue(new UdpLookupKey(ConnectionKeyFormatter.IPv4Any, localPort), out processId))
            {
                return true;
            }

            if (_udpOwners.TryGetValue(new UdpLookupKey(ConnectionKeyFormatter.IPv6Any, localPort), out processId))
            {
                return true;
            }

            processId = 0;
            return false;
        }
    }

    private static ushort ReadPort(byte[] bytes)
    {
        return bytes.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2)) : (ushort)0;
    }

    private static class IpHelperNative
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int tcpTableLength, bool sort, int ipVersion, TcpTableClass tcpTableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedUdpTable(IntPtr udpTable, ref int udpTableLength, bool sort, int ipVersion, UdpTableClass udpTableClass, uint reserved);
    }
}