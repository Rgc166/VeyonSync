using System.IO.Compression;
using System.Security;
using System.Text;

namespace VeyonSync.Shared;

public sealed class ReportTable
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<string> Headers { get; set; } = new();
    public List<double> Widths { get; set; } = new(); // proporções
    public List<List<string>> Rows { get; set; } = new();
}

/// <summary>Exportadores sem dependências externas: CSV, TXT, PDF (texto, Helvetica) e DOCX (OOXML mínimo).</summary>
public static class Exporters
{
    public static void Csv(ReportTable t, string path)
    {
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", t.Headers.Select(Q)));
        foreach (var r in t.Rows) sb.AppendLine(string.Join(";", r.Select(Q)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM: Excel abre acentos corretamente
    }

    public static void Txt(ReportTable t, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine(t.Title);
        if (t.Subtitle != "") sb.AppendLine(t.Subtitle);
        sb.AppendLine(new string('=', Math.Max(20, t.Title.Length)));
        foreach (var r in t.Rows)
        {
            for (int i = 0; i < t.Headers.Count && i < r.Count; i++)
                if (!string.IsNullOrEmpty(r[i])) sb.AppendLine($"{t.Headers[i]}: {r[i]}");
            sb.AppendLine(new string('-', 40));
        }
        sb.AppendLine($"Total: {t.Rows.Count} registro(s).");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // ---------------- PDF ----------------
    public static void Pdf(ReportTable t, string path)
    {
        const double W = 842, H = 595, M = 36, FontSize = 7.5, Lead = 9.5;
        var widths = NormalizedWidths(t, W - 2 * M);
        // Largura média de caractere da Helvetica ~0,5 em; margem conservadora.
        int CharsFor(double w) => Math.Max(4, (int)(w / (FontSize * 0.52)));

        var pages = new List<StringBuilder>();
        StringBuilder? page = null;
        double y = 0;

        void NewPage()
        {
            page = new StringBuilder();
            pages.Add(page);
            y = H - M;
            Text(page, M, y, 12, t.Title, bold: true);
            y -= 15;
            if (t.Subtitle != "") { Text(page, M, y, 8, t.Subtitle); y -= 12; }
            DrawRow(t.Headers, true);
            page.AppendLine(FormattableString.Invariant($"{M} {y + Lead - 2:0.##} m {W - M} {y + Lead - 2:0.##} l 0.5 w S"));
        }

        void DrawRow(List<string> cells, bool bold)
        {
            var wrapped = new List<List<string>>();
            int lines = 1;
            for (int i = 0; i < widths.Count; i++)
            {
                var w = Wrap(i < cells.Count ? cells[i] : "", CharsFor(widths[i] - 4));
                wrapped.Add(w);
                lines = Math.Max(lines, w.Count);
            }
            lines = Math.Min(lines, 12);
            if (y - lines * Lead < M) NewPageCore();
            double x = M;
            for (int i = 0; i < widths.Count; i++)
            {
                for (int l = 0; l < Math.Min(lines, wrapped[i].Count); l++)
                    Text(page!, x, y - l * Lead, FontSize, wrapped[i][l], bold);
                x += widths[i];
            }
            y -= lines * Lead + 2;
        }

        void NewPageCore() => NewPage();

        NewPage();
        foreach (var r in t.Rows)
        {
            if (y - Lead < M) NewPage();
            DrawRow(r, false);
        }
        if (t.Rows.Count == 0) Text(page!, M, y, FontSize, "Nenhum registro.");

        // Montagem do arquivo
        int fontId = 3, fontBoldId = 4;
        var pageIds = new List<int>();
        int next = 5;
        var pageObjs = new List<(int pageId, int contentId, byte[] content)>();
        foreach (var p in pages)
        {
            var content = Encoding.Latin1.GetBytes(p.ToString());
            pageObjs.Add((next, next + 1, content));
            pageIds.Add(next);
            next += 2;
        }
        using var ms = new MemoryStream();
        var offsets = new SortedDictionary<int, long>();
        void Obj(int id, string body) { offsets[id] = ms.Position; var b = Encoding.Latin1.GetBytes($"{id} 0 obj\n{body}\nendobj\n"); ms.Write(b); }
        ms.Write(Encoding.Latin1.GetBytes("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"));
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>");
        Obj(fontId, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj(fontBoldId, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        foreach (var (pid, cid, content) in pageObjs)
        {
            Obj(pid, FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Resources << /Font << /F1 {fontId} 0 R /F2 {fontBoldId} 0 R >> >> /Contents {cid} 0 R >>"));
            offsets[cid] = ms.Position;
            ms.Write(Encoding.Latin1.GetBytes($"{cid} 0 obj\n<< /Length {content.Length} >>\nstream\n"));
            ms.Write(content);
            ms.Write(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
        }
        var xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {next}\n0000000000 65535 f \n");
        for (int i = 1; i < next; i++) sb.Append($"{offsets[i]:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {next} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(Encoding.Latin1.GetBytes(sb.ToString()));
        File.WriteAllBytes(path, ms.ToArray());
    }

    static void Text(StringBuilder sb, double x, double y, double size, string s, bool bold = false) =>
        sb.AppendLine(FormattableString.Invariant($"BT /{(bold ? "F2" : "F1")} {size} Tf {x:0.##} {y:0.##} Td ({PdfEscape(s)}) Tj ET"));

    static string PdfEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            var c = ch switch { '—' => '-', '–' => '-', '→' => '>', '‘' or '’' => '\'', '“' or '”' => '"', _ => ch };
            if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append(' ');
            else if (c > 255) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static List<double> NormalizedWidths(ReportTable t, double total)
    {
        var n = t.Headers.Count;
        var w = t.Widths.Count == n ? t.Widths : Enumerable.Repeat(1.0, n).ToList();
        var sum = w.Sum();
        return w.Select(x => x / sum * total).ToList();
    }

    static List<string> Wrap(string s, int width)
    {
        var result = new List<string>();
        foreach (var para in (s ?? "").Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in para.Split(' '))
            {
                var wd = word;
                while (wd.Length > width) { if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); } result.Add(wd[..width]); wd = wd[width..]; }
                if (line.Length + wd.Length + (line.Length > 0 ? 1 : 0) > width) { result.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(wd);
            }
            result.Add(line.ToString());
        }
        return result.Count == 0 ? new List<string> { "" } : result;
    }

    // ---------------- DOCX ----------------
    public static void Docx(ReportTable t, string path)
    {
        static string X(string s) => SecurityElement.Escape(s ?? "") ?? "";
        static string P(string s, bool bold = false, int sizeHalfPt = 18) =>
            $"<w:p><w:r><w:rPr>{(bold ? "<w:b/>" : "")}<w:sz w:val=\"{sizeHalfPt}\"/></w:rPr><w:t xml:space=\"preserve\">{X(s)}</w:t></w:r></w:p>";

        var total = 15840 - 1440; // largura útil em twips (paisagem Carta/A4 aproximada)
        var sum = t.Widths.Count == t.Headers.Count ? t.Widths.Sum() : t.Headers.Count;
        var widths = t.Headers.Select((_, i) => (int)((t.Widths.Count == t.Headers.Count ? t.Widths[i] : 1) / sum * total)).ToList();

        string Cell(string s, int w, bool bold) =>
            $"<w:tc><w:tcPr><w:tcW w:w=\"{w}\" w:type=\"dxa\"/></w:tcPr>{P(s, bold, 16)}</w:tc>";

        var body = new StringBuilder();
        body.Append(P(t.Title, true, 28));
        if (t.Subtitle != "") body.Append(P(t.Subtitle, false, 18));
        body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>");
        foreach (var b in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            body.Append($"<w:{b} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"999999\"/>");
        body.Append("</w:tblBorders></w:tblPr><w:tblGrid>");
        foreach (var w in widths) body.Append($"<w:gridCol w:w=\"{w}\"/>");
        body.Append("</w:tblGrid>");
        body.Append("<w:tr><w:trPr><w:tblHeader/></w:trPr>");
        for (int i = 0; i < t.Headers.Count; i++) body.Append(Cell(t.Headers[i], widths[i], true));
        body.Append("</w:tr>");
        foreach (var r in t.Rows)
        {
            body.Append("<w:tr>");
            for (int i = 0; i < t.Headers.Count; i++) body.Append(Cell(i < r.Count ? r[i] : "", widths[i], false));
            body.Append("</w:tr>");
        }
        body.Append("</w:tbl>");
        body.Append(P($"Total: {t.Rows.Count} registro(s)."));
        body.Append("<w:sectPr><w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/><w:pgMar w:top=\"720\" w:right=\"720\" w:bottom=\"720\" w:left=\"720\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>");

        var doc = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                  "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                  body + "</w:body></w:document>";
        const string types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>";
        const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        if (File.Exists(path)) File.Delete(path);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var e = z.CreateEntry(name, CompressionLevel.Optimal);
            using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
            s.Write(content);
        }
        Add("[Content_Types].xml", types);
        Add("_rels/.rels", rels);
        Add("word/document.xml", doc);
    }

    public static void Write(ReportTable t, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csv": Csv(t, path); break;
            case ".pdf": Pdf(t, path); break;
            case ".docx": Docx(t, path); break;
            default: Txt(t, path); break;
        }
    }
}
