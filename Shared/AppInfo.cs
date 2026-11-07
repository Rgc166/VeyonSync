namespace VeyonSync.Shared;

public static class AppInfo
{
    /// <summary>Versão do produto (Directory.Build.props), ex.: "2.1.0" ou "2.2.0-beta.1".</summary>
    public static readonly string Version = ReadVersion();

    static string ReadVersion()
    {
        var a = typeof(AppInfo).Assembly;
        var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(a, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        var v = info?.InformationalVersion ?? a.GetName().Version?.ToString(3) ?? "0.0.0";
        return v.Split('+')[0];
    }
    public const string TeacherDisplayName = "Veyon Sync THC — Professor";
    public const string StudentDisplayName = "Veyon Sync STD — Aluno";
    public const string TeacherShort = "Veyon Sync THC";
    public const string StudentShort = "Veyon Sync STD";

    // Nomes internos mantidos da Etapa 1 para não quebrar instalações existentes.
    public const string TeacherService = "VeyonSyncTeacher";
    public const string StudentService = "VeyonSyncStudent";
    public const string TeacherPipe = "VeyonSync.Teacher.v2";
    public const string StudentPipe = "VeyonSync.Student.v2";

    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VeyonSync");
    public static string TeacherData => Path.Combine(DataRoot, "Teacher");
    public static string StudentData => Path.Combine(DataRoot, "Student");
    public static string ProgramRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VeyonSync");

    /// <summary>
    /// Compara versões X.Y.Z[-pre]. Uma versão de teste (com sufixo) é menor que a final de mesmo número.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static (Version v, string pre) P(string s)
        {
            s = (s ?? "").Trim().TrimStart('v', 'V').Split('+')[0];
            var dash = s.IndexOf('-');
            var core = dash >= 0 ? s[..dash] : s;
            var pre = dash >= 0 ? s[(dash + 1)..] : "";
            var v = System.Version.TryParse(core, out var x) ? x : new Version(0, 0);
            return (new Version(v.Major, v.Minor, Math.Max(0, v.Build)), pre);
        }
        var (va, pa) = P(a); var (vb, pb) = P(b);
        var c = va.CompareTo(vb);
        if (c != 0) return c;
        if (pa == pb) return 0;
        if (pa == "") return 1;
        if (pb == "") return -1;
        return string.CompareOrdinal(pa, pb);
    }

    public static bool IsPrerelease(string v) => v.Split('+')[0].Contains('-');
}
