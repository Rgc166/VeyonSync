using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VeyonSync.Student;

public record Snapshot(string? Ip, string? Mac, string? User, string? SessionNote);

/// <summary>
/// Coleta local. Usuário = sessão interativa ativa do console (não a conta do serviço nem o administrador
/// que abriu a interface). Com várias sessões, o console tem prioridade e as demais aparecem só na observação.
/// </summary>
public static class Collector
{
    [DllImport("kernel32")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode)] static extern bool WTSQuery(IntPtr h, uint sid, int cls, out IntPtr buf, out uint len);
    [DllImport("wtsapi32")] static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32", EntryPoint = "WTSEnumerateSessionsW", CharSet = CharSet.Unicode)] static extern bool WTSEnumerateSessions(IntPtr h, int reserved, int version, out IntPtr info, out int count);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTS_SESSION_INFO { public uint SessionId; public IntPtr WinStationName; public int State; }
    const int WTSActive = 0;

    static string? Wts(uint sid, int cls)
    {
        if (!WTSQuery(IntPtr.Zero, sid, cls, out var p, out _)) return null;
        try { var s = Marshal.PtrToStringUni(p); return string.IsNullOrEmpty(s) ? null : s; } finally { WTSFreeMemory(p); }
    }

    static string? UserOf(uint sid)
    {
        var u = Wts(sid, 5); if (u == null) return null;
        var d = Wts(sid, 7); return d == null ? u : $"{d}\\{u}";
    }

    // Regra: sessão ativa do console. Sem sessão/usuário => null ("sem usuário conectado").
    public static string? InteractiveUser()
    {
        var sid = WTSGetActiveConsoleSessionId(); if (sid == 0xFFFFFFFF) return null;
        return UserOf(sid);
    }

    static string? OtherSessions(string? consoleUser)
    {
        try
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var info, out var count)) return null;
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                var others = new List<string>();
                var console = WTSGetActiveConsoleSessionId();
                for (int i = 0; i < count; i++)
                {
                    var s = Marshal.PtrToStructure<WTS_SESSION_INFO>(info + i * size);
                    if (s.SessionId == 0 || s.SessionId == console || s.State != WTSActive) continue;
                    var u = UserOf(s.SessionId);
                    if (u != null) others.Add(u);
                }
                if (others.Count == 0) return null;
                return (consoleUser == null ? "Sem usuário no console; " : "") + $"outras sessões ativas: {string.Join(", ", others.Distinct())}";
            }
            finally { WTSFreeMemory(info); }
        }
        catch (Exception) { return null; }
    }

    public static Snapshot Collect(string teacherUrl)
    {
        string? ip = null, mac = null;
        try
        {
            var uri = new Uri(teacherUrl);
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(uri.Host, uri.Port); // UDP: só escolhe a rota/interface, não envia pacotes
            var a = ((IPEndPoint)s.LocalEndPoint!).Address; ip = a.ToString();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.GetIPProperties().UnicastAddresses.Any(x => x.Address.Equals(a)))
                { mac = string.Join("-", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))); break; }
        }
        catch { }
        var user = InteractiveUser();
        return new Snapshot(ip, string.IsNullOrEmpty(mac) ? null : mac, user, OtherSessions(user));
    }
}
