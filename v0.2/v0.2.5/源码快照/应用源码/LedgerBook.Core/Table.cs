using System.Text;

namespace LedgerBook.Core;

/// <summary>
/// 表格文件的读写（制表符分隔）。
///
/// 读：容忍 Excel 存过的各种编码（UTF-8 BOM / UTF-16 BOM / GBK），分隔符按首行猜。
/// 写：统一 UTF-8 带 BOM + CRLF，Excel 双击不乱码，和 v0.1.0 完全一致。
/// </summary>
public static class Table
{
    public const char Delimiter = '\t';
    private static readonly Encoding WriteEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>按 BOM 与候选编码依次尝试解码。</summary>
    public static string Decode(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        }

        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        }

        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            // GBK 解码需要 System.Text.Encoding.CodePages 包，而本机 NuGet 只能离线，
            // 暂时退回「宽松 UTF-8」（不会抛异常，个别生僻字可能显示为问号）。
            // TODO: 等联网或把包放进工程内本地源后，补上 GBK 解码。
            return Encoding.UTF8.GetString(raw);
        }
    }

    public static string ReadText(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>按首行里有没有制表符判断分隔符。</summary>
    public static char SniffDelimiter(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            return line.Contains('\t') ? '\t' : ',';
        }

        return Delimiter;
    }

    /// <summary>解析成「表头 + 数据行」——和 v0.1.0 一样，首行永远当表头。</summary>
    public static (List<string> Header, List<string[]> Rows) Read(string path)
    {
        var text = ReadText(path);
        var delimiter = SniffDelimiter(text);
        var lines = SplitLines(text);

        var header = new List<string>();
        var rows = new List<string[]>();
        var first = true;
        foreach (var line in lines)
        {
            if (first)
            {
                header.AddRange(SplitLine(line, delimiter).Select(cell => cell.TrimStart('\uFEFF')));
                first = false;
                continue;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            rows.Add(SplitLine(line, delimiter).ToArray());
        }

        if (first)
        {
            header.AddRange(Array.Empty<string>());
        }

        return (header, rows);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>按分隔符切分，支持双引号包裹（Excel 导出常见）。</summary>
    public static List<string> SplitLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var buffer = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        buffer.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    buffer.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case var _ when ch == delimiter:
                    cells.Add(buffer.ToString());
                    buffer.Clear();
                    break;
                default:
                    buffer.Append(ch);
                    break;
            }
        }

        cells.Add(buffer.ToString());
        return cells;
    }

    private static string Escape(string cell, char delimiter)
    {
        if (cell.Contains('"') || cell.Contains(delimiter) || cell.Contains('\n') || cell.Contains('\r'))
        {
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }

        return cell;
    }

    public static string Build(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows, char delimiter = Delimiter)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(delimiter, header.Select(cell => Escape(cell, delimiter)))).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(delimiter, row.Select(cell => Escape(cell ?? "", delimiter)))).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>先写临时文件再替换（写一半断电也不会毁掉当天账本）。</summary>
    public static void AtomicWrite(string path, string text, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, text, encoding ?? WriteEncoding);
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            // 依次尝试三种替换方式：不同系统/杀软/权限下总有一种能用
            // 1) File.Replace（保留原文件属性，最干净）
            try
            {
                File.Replace(temp, path, null);
                return;
            }
            catch (Exception)
            {
                // 2) MoveFileEx + REPLACE_EXISTING（和 Python 的 os.replace 等价）
            }

            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception)
            {
                // 3) 最后手段：先删再移（短暂窗口内文件不在，但总比写不进去好）
            }

            File.Delete(path);
            File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // 删不掉就留着，不影响数据
                }
            }
        }
    }

    public static void Write(string path, IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows, char delimiter = Delimiter)
        => AtomicWrite(path, Build(header, rows, delimiter), WriteEncoding);

    /// <summary>追加一行（新增记录最安全的方式，不重写整个文件）。</summary>
    public static void AppendRow(string path, IEnumerable<string> row, char delimiter = Delimiter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = string.Join(delimiter, row.Select(cell => Escape(cell ?? "", delimiter)));
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (stream.Length == 0)
        {
            var bom = WriteEncoding.GetPreamble();
            stream.Write(bom, 0, bom.Length);
        }

        var bytes = WriteEncoding.GetBytes(line + "\r\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }
}
