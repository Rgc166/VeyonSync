using System.Text;
using System.Text.RegularExpressions;

namespace VeyonSync.Shared;

/// <summary>Padrões do nome exibido no Veyon. A identidade interna (AgentId) nunca vira rótulo.</summary>
public static class Naming
{
    public const string WindowsName = "windows";   // nome real do computador
    public const string Pc = "pc";                 // PC 01
    public const string LabPc = "lab-pc";          // LAB-B-PC01
    public const string Aluno = "aluno";           // ALUNO 01

    public static readonly (string Key, string Label)[] Options =
    {
        (WindowsName, "Nome real do computador"),
        (Pc, "PC 01"),
        (LabPc, "LAB-B-PC01"),
        (Aluno, "ALUNO 01")
    };

    static readonly Regex Digits = new(@"(\d+)\s*$");

    /// <summary>Extrai o número do posto ("PC03", "3", "Posto 3" → 3).</summary>
    public static int? PositionNumber(string? position)
    {
        if (string.IsNullOrWhiteSpace(position)) return null;
        var m = Digits.Match(position.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>Normaliza a posição digitada para comparação (PC3, pc03, 3 → PC03).</summary>
    public static string NormalizePosition(string? position)
    {
        var n = PositionNumber(position);
        if (n != null) return $"PC{n:00}";
        return (position ?? "").Trim().ToUpperInvariant();
    }

    public static string Friendly(string pattern, string lab, string? position, string windowsName)
    {
        var n = PositionNumber(position);
        var name = pattern switch
        {
            Pc when n != null => $"PC {n:00}",
            LabPc when n != null => $"LAB-{lab.ToUpperInvariant()}-PC{n:00}",
            Aluno when n != null => $"ALUNO {n:00}",
            _ => windowsName
        };
        return Sanitize(name);
    }

    /// <summary>Remove caracteres que quebrariam o formato de exportação/argumentos do Veyon.</summary>
    public static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim())
            if (!char.IsControl(ch) && ch != ';' && ch != '"' && ch != '%') sb.Append(ch);
        var r = sb.ToString();
        return r.Length > 63 ? r[..63] : r;
    }
}
