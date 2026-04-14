using System.Net;
using System.Runtime.InteropServices;

namespace ShapeTraffic.Infrastructure.Native;

internal static class WinDivertNative
{
    internal const string LibraryName = "WinDivert.dll";
    internal const int ErrorNoData = 232;
    internal const ulong FlagSniff = 1;
    internal const ulong FlagRecvOnly = 4;
    internal const ulong FlagSendOnly = 8;

    internal static readonly IntPtr InvalidHandle = new(-1);

    internal enum Layer : uint
    {
        Network = 0,
        NetworkForward = 1,
        Flow = 2,
        Socket = 3,
        Reflect = 4,
    }

    internal enum Event : uint
    {
        NetworkPacket = 0,
        FlowEstablished = 1,
        FlowDeleted = 2,
        SocketBind = 3,
        SocketConnect = 4,
        SocketListen = 5,
        SocketAccept = 6,
        SocketClose = 7,
        ReflectOpen = 8,
        ReflectClose = 9,
    }

    internal enum ShutdownMode : uint
    {
        Receive = 1,
        Send = 2,
        Both = 3,
    }

    internal enum Param : uint
    {
        QueueLength = 0,
        QueueTime = 1,
        QueueSize = 2,
        VersionMajor = 3,
        VersionMinor = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NetworkData
    {
        public uint IfIdx;
        public uint SubIfIdx;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Address128
    {
        public uint Segment0;
        public uint Segment1;
        public uint Segment2;
        public uint Segment3;

        public readonly uint[] ToArray() => [Segment0, Segment1, Segment2, Segment3];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FlowData
    {
        public ulong Endpoint;
        public ulong ParentEndpoint;
        public uint ProcessId;
        public Address128 LocalAddr;
        public Address128 RemoteAddr;

        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct AddressDataUnion
    {
        [FieldOffset(0)]
        public NetworkData Network;

        [FieldOffset(0)]
        public FlowData Flow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Address
    {
        public long Timestamp;
        private ulong _metadata;
        public AddressDataUnion Data;

        public Layer Layer
        {
            readonly get => (Layer)(_metadata & 0xFF);
            set => _metadata = (_metadata & ~0xFFUL) | (byte)value;
        }

        public readonly Event Event => (Event)((_metadata >> 8) & 0xFF);

        public readonly bool Sniffed => ((_metadata >> 16) & 0x1) != 0;

        public bool Outbound
        {
            readonly get => ((_metadata >> 17) & 0x1) != 0;
            set
            {
                if (value)
                {
                    _metadata |= 1UL << 17;
                }
                else
                {
                    _metadata &= ~(1UL << 17);
                }
            }
        }

        public readonly bool Loopback => ((_metadata >> 18) & 0x1) != 0;

        public readonly bool Impostor => ((_metadata >> 19) & 0x1) != 0;

        public readonly bool IPv6 => ((_metadata >> 20) & 0x1) != 0;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern IntPtr WinDivertOpen(string filter, Layer layer, short priority, ulong flags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, ref Address addr);

    [DllImport(LibraryName, EntryPoint = "WinDivertRecv", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecvEvent(IntPtr handle, IntPtr packet, uint packetLen, out uint recvLen, ref Address addr);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, ref Address addr);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertShutdown(IntPtr handle, ShutdownMode how);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertClose(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSetParam(IntPtr handle, Param param, ulong value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    internal static extern ushort WinDivertHelperNtohs(ushort value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    internal static extern void WinDivertHelperNtohIPv6Address(in Address128 address, out Address128 output);

    internal static IPAddress ToIPAddress(Address128 value)
    {
        WinDivertHelperNtohIPv6Address(value, out var hostOrder);
        var bytes = new byte[16];
        var segments = hostOrder.ToArray();
        for (var index = 0; index < segments.Length; index++)
        {
            var offset = index * 4;
            var segment = segments[index];
            bytes[offset] = (byte)((segment >> 24) & 0xFF);
            bytes[offset + 1] = (byte)((segment >> 16) & 0xFF);
            bytes[offset + 2] = (byte)((segment >> 8) & 0xFF);
            bytes[offset + 3] = (byte)(segment & 0xFF);
        }

        return new IPAddress(bytes);
    }
}
