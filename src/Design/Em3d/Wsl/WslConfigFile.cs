using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-97 R-em3d97-1 — the user's <c>.wslconfig</c>, read and edited ONE KEY at a time: <c>memory=</c>
/// under <c>[wsl2]</c>. Everything else in the file is kept byte for byte — other keys and sections, comments,
/// blank lines, the line endings, the encoding and a BOM — because the file is machine-wide and is very often
/// written by someone else too (Docker Desktop's WSL backend, the user, another tool).
///
/// <para><b>Not a circuitRF setting.</b> circuitRF keeps no copy of the value: it reads this file every time
/// it is asked and writes it only when the user says to (<see cref="WslMemory.Apply"/>).</para>
/// </summary>
/// <param name="Path">The file, under the user's profile (<see cref="WslPalace.WslConfigPath"/>).</param>
/// <param name="Exists">Whether the file was there. A missing file is the ordinary case: WSL then uses its defaults.</param>
/// <param name="Text">The decoded text, without its BOM; empty when the file does not exist.</param>
/// <param name="ReadError">The operating system's message when the file exists and could not be read.</param>
public sealed record WslConfigFile(string Path, bool Exists, string Text, Encoding Encoding, bool Bom, string? ReadError)
{
    /// <summary>The one previous copy a write keeps beside the file (D3), overwritten on each later write.</summary>
    public const string BackupSuffix = ".circuitrf-backup";

    public string BackupPath => Path + BackupSuffix;

    /// <summary>The <c>memory=</c> the file sets under <c>[wsl2]</c> — the last one, as WSL reads it — or null.</summary>
    public string? Memory => MemoryOf(Text);

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads <paramref name="path"/>. Never throws: an unreadable file comes back with
    /// <see cref="ReadError"/> set and no text.</summary>
    public static WslConfigFile Read(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return new(path, false, "", Utf8Strict, false, null);
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(path, true, "", Utf8Strict, false, e.Message);
        }
        var (encoding, skip) = Detect(bytes);
        bool bom = skip > 0;
        string text;
        try { text = encoding.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException)
        {
            // Not UTF-8 — an ANSI file. Latin-1 maps every byte to one character and back, so whatever the code
            // page, the lines this edit does not touch are written back as the same bytes.
            encoding = Encoding.Latin1;
            text = encoding.GetString(bytes);
        }
        return new(path, true, text, encoding, bom, null);
    }

    /// <summary>The encoding, and the length of the BOM to skip (0: none).</summary>
    private static (Encoding, int Bom) Detect(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (Utf8Strict, 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, true, true), 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, true, true), 2);
        return (Utf8Strict, 0);
    }

    /// <summary>The bytes <paramref name="text"/> is written as: this file's encoding, and its BOM if it had one.</summary>
    public byte[] Encode(string text)
    {
        byte[] body = Encoding.GetBytes(text);
        if (!Bom) return body;
        byte[] preamble = Encoding is UTF8Encoding ? [0xEF, 0xBB, 0xBF] : Encoding.GetPreamble();
        return [.. preamble, .. body];
    }

    /// <summary>
    /// Writes <paramref name="text"/> in this file's encoding. The previous file, when there was one, is first
    /// copied to <see cref="BackupPath"/>; the new text goes to a temporary file beside it that then REPLACES
    /// the original, so a write that fails part-way leaves the original untouched. Throws the operating system's
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>; it never writes anywhere else.
    /// </summary>
    /// <returns>The backup's path, or null when there was no file to back up.</returns>
    public string? Write(string text)
    {
        string? backup = null;
        if (File.Exists(Path))
        {
            File.Copy(Path, BackupPath, overwrite: true);
            backup = BackupPath;
        }
        string temp = Path + ".circuitrf-tmp";
        try
        {
            File.WriteAllBytes(temp, Encode(text));
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return backup;
    }

    // ── the edit ────────────────────────────────────────────────────────────────────────────────

    private static readonly Regex Section = new(@"^\s*\[\s*(?<name>[^\]]*?)\s*\]", RegexOptions.CultureInvariant);

    // The value stops at a trailing comment; the spaces before the comment belong to the comment, so they are kept.
    private static readonly Regex MemoryKey = new(@"^(?<lead>\s*memory\s*=\s*)(?<value>[^#;]*?)(?<tail>\s*(?:[#;].*)?)$",
                                                  RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly record struct Line(string Content, string Ending);

    private static List<Line> Lines(string text)
    {
        var lines = new List<Line>();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0) { lines.Add(new(text[start..], "")); break; }
            int end = nl > start && text[nl - 1] == '\r' ? nl - 1 : nl;
            lines.Add(new(text[start..end], text[end..(nl + 1)]));
            start = nl + 1;
        }
        return lines;
    }

    /// <summary>The file's line ending, by majority (CRLF on a tie, and for a file with none — WSL is Windows's).</summary>
    private static string NewLine(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { if (i > 0 && text[i - 1] == '\r') crlf++; else lf++; }
        return lf > crlf ? "\n" : "\r\n";
    }

    /// <summary>Indices of the <c>[wsl2]</c> headers, and of every <c>memory=</c> line inside such a section.</summary>
    private static (List<int> Headers, List<int> Keys) Scan(List<Line> lines)
    {
        var headers = new List<int>();
        var keys = new List<int>();
        bool inWsl2 = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var section = Section.Match(lines[i].Content);
            if (section.Success)
            {
                inWsl2 = string.Equals(section.Groups["name"].Value, "wsl2", StringComparison.OrdinalIgnoreCase);
                if (inWsl2) headers.Add(i);
            }
            else if (inWsl2 && MemoryKey.IsMatch(lines[i].Content)) keys.Add(i);
        }
        return (headers, keys);
    }

    /// <summary>The value of the last <c>memory=</c> under <c>[wsl2]</c> in <paramref name="text"/>, or null.</summary>
    public static string? MemoryOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = Lines(text);
        var (_, keys) = Scan(lines);
        return keys.Count == 0 ? null : MemoryKey.Match(lines[keys[^1]].Content).Groups["value"].Value.Trim();
    }

    /// <summary>The spelling WSL reads: whole gigabytes.</summary>
    public static string MemoryValue(int gb) => $"{gb}GB";

    /// <summary>
    /// <paramref name="text"/> (null or empty: no file) with <c>memory=</c> under <c>[wsl2]</c> set to
    /// <paramref name="gb"/> GB, and nothing else changed:
    /// no section — one appended at the end, after a blank line; a section without the key — the key inserted
    /// directly after its header; the key present — that line's VALUE replaced, its leading whitespace and any
    /// trailing comment kept; the key repeated — the LAST replaced (WSL reads the last), the rest left as they are.
    /// Section and key names match in any case. An inserted line takes the file's majority line ending.
    /// </summary>
    public static WslConfigEdit WithMemory(string? text, int gb)
    {
        string value = MemoryValue(gb);
        string after = "memory=" + value;
        if (string.IsNullOrEmpty(text)) return new($"[wsl2]\r\n{after}\r\n", null, after, 0);

        string nl = NewLine(text);
        var lines = Lines(text);
        var (headers, keys) = Scan(lines);
        if (keys.Count > 0)
        {
            int i = keys[^1];
            var m = MemoryKey.Match(lines[i].Content);
            string changed = m.Groups["lead"].Value + value + m.Groups["tail"].Value;
            string before = lines[i].Content;
            lines[i] = lines[i] with { Content = changed };
            return new(Join(lines), before.Trim(), changed.Trim(), keys.Count);
        }
        if (headers.Count > 0)
        {
            int h = headers[0];
            if (lines[h].Ending.Length == 0)
            {
                // The header is the file's last line, with no line ending: the key becomes the last line instead.
                lines[h] = lines[h] with { Ending = nl };
                lines.Insert(h + 1, new(after, ""));
            }
            else lines.Insert(h + 1, new(after, nl));
            return new(Join(lines), null, after, 0);
        }
        var sb = new StringBuilder(text);
        if (!text.EndsWith('\n')) sb.Append(nl);
        if (!(text.EndsWith("\n\n") || text.EndsWith("\n\r\n"))) sb.Append(nl);
        sb.Append("[wsl2]").Append(nl).Append(after).Append(nl);
        return new(sb.ToString(), null, after, 0);
    }

    private static string Join(List<Line> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.Content).Append(l.Ending);
        return sb.ToString();
    }
}

/// <summary>What <see cref="WslConfigFile.WithMemory"/> made.</summary>
/// <param name="Text">The whole new text.</param>
/// <param name="Before">The line that was changed, trimmed; null when a line was added.</param>
/// <param name="After">The line as it now reads, trimmed.</param>
/// <param name="Repeats">How many <c>memory=</c> lines <c>[wsl2]</c> held; above 1, only the last was changed.</param>
public sealed record WslConfigEdit(string Text, string? Before, string After, int Repeats);
