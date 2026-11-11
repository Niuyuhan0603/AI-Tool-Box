using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AI_Tool_Box.Services;

/// <summary>NCM 转换结果。</summary>
public sealed record NcmConvertResult(string OutputPath, string? Title, string? Artist, string Format);

/// <summary>
/// 网易云音乐 .ncm 加密音频转换器（个人格式转换，本地离线处理）。
/// 文件结构：CTENFDAM 头 + 两段 AES-128-ECB（密钥段/元数据段）+ RC4-like keyBox 还原音频流；
/// 按内嵌 meta 的 format 输出 .mp3 / .flac，并尽力为 MP3 写入 ID3v2.3 标签（歌名/艺术家/专辑/封面）。
/// </summary>
public static class NcmConverter
{
    private static ReadOnlySpan<byte> MagicHeader => "CTENFDAM"u8;
    private const string CoreKey = "hzHRAmso5kInbaxW";    // 密钥段 AES-128-ECB 密钥（NCM 格式公开固定常量）
    private const string MetaKey = "#14ljk_!\\]&0U<'(";   // 元数据段 AES-128-ECB 密钥（同上）
    private const string KeyPrefix = "neteasecloudmusic";
    private const string MetaPrefix = "163 key(Don't modify):";
    private const string MetaJsonPrefix = "music:";
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private const int ChunkSize = 1 << 20;

    /// <summary>转换单个 .ncm 文件；输出目录缺省为源文件所在目录。</summary>
    public static async Task<NcmConvertResult> ConvertAsync(string ncmPath, string? outputDir = null, CancellationToken ct = default)
    {
        if (!File.Exists(ncmPath)) throw new FileNotFoundException("NCM 文件不存在：" + ncmPath);
        outputDir ??= Path.GetDirectoryName(ncmPath);
        if (string.IsNullOrWhiteSpace(outputDir)) throw new ArgumentException("无法确定输出目录。", nameof(outputDir));
        Directory.CreateDirectory(outputDir);

        string tempPath = Path.Combine(outputDir, Path.GetRandomFileName() + ".tmp");
        // 作用域提升到 try 外：最终文件写出阶段（await using 块之外）仍需使用
        string? format = null, title = null, artist = null, album = null;
        byte[]? cover = null;
        try
        {
            await using var fs = new FileStream(ncmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using (var audio = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                // ===== 头部 =====
                byte[] header = new byte[8];
                ReadExactly(fs, header);
                if (!header.AsSpan().SequenceEqual(MagicHeader))
                    throw new InvalidDataException("不是有效的 NCM 文件（缺少 CTENFDAM 标识）。");
                fs.Seek(2, SeekOrigin.Current); // 保留段

                // ===== 密钥段：XOR 0x64 → AES-ECB(coreKey) → 去掉固定前缀得到 RC4-like key =====
                byte[] keyPlain = AesEcbDecrypt(
                    Encoding.ASCII.GetBytes(CoreKey),
                    Xor(ReadBlock(fs), 0x64));
                if (keyPlain.Length <= KeyPrefix.Length ||
                    !Encoding.ASCII.GetString(keyPlain, 0, KeyPrefix.Length).Equals(KeyPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException("NCM 密钥段解密失败（文件可能已损坏）。");
                byte[] keyBox = BuildKeyBox(keyPlain[KeyPrefix.Length..]);

                // ===== 元数据段：XOR 0x63 → 去前缀 → Base64 → AES-ECB(metaKey) → "music:{json}" =====
                string? metaJson = null;
                int metaLen = ReadInt32(fs);
                if (metaLen > 0)
                {
                    try
                    {
                        byte[] meta = Xor(ReadBlock(fs), 0x63);
                        if (meta.Length > MetaPrefix.Length)
                        {
                            var encrypted = Convert.FromBase64String(Encoding.ASCII.GetString(meta[MetaPrefix.Length..]));
                            byte[] plain = AesEcbDecrypt(Encoding.ASCII.GetBytes(MetaKey), encrypted);
                            if (plain.Length > MetaJsonPrefix.Length &&
                                Encoding.ASCII.GetString(plain, 0, MetaJsonPrefix.Length) == MetaJsonPrefix)
                            {
                                metaJson = Encoding.UTF8.GetString(plain[MetaJsonPrefix.Length..]);
                            }
                        }
                    }
                    catch
                    {
                        metaJson = null; // meta 解析失败不阻断音频还原
                    }
                }
                else
                {
                    metaJson = null;
                }

                (format, title, artist, album) = ParseMeta(metaJson);

                // ===== CRC32（忽略）+ 保留段 5 字节 =====
                _ = ReadInt32(fs);
                fs.Seek(5, SeekOrigin.Current);

                // ===== 封面图（仅用于 ID3 APIC） =====
                int imgLen = ReadInt32(fs);
                if (imgLen < 0 || imgLen > MaxImageBytes) throw new InvalidDataException("封面段长度异常（文件可能已损坏）。");
                if (imgLen > 0) cover = ReadBlock(fs, imgLen);
                fs.Seek(1, SeekOrigin.Current);

                // ===== 音频流：keyBox[(i+1) & 0xFF] 异或还原，先嗅探格式再落盘 =====
                byte[] head = new byte[8];
                int headRead = await ReadAudioChunkAsync(fs, audio, head, keyBox, ct);
                if (format is null)
                {
                    format = SniffFormat(head.AsSpan(0, headRead));
                }
                format = format == "flac" ? "flac" : "mp3";

                long absolute = headRead;
                byte[] chunk = new byte[ChunkSize];
                int n;
                while ((n = await fs.ReadAsync(chunk.AsMemory(), ct)) > 0)
                {
                    XorKeyBox(chunk.AsSpan(0, n), keyBox, absolute);
                    await audio.WriteAsync(chunk.AsMemory(0, n), ct);
                    absolute += n;
                }
            }

            // ===== 写出最终文件（MP3 前置 ID3v2.3；FLAC 原样输出） =====
            string ext = "." + format;
            string baseName = BuildOutputName(title, artist, ncmPath);
            string outPath = UniquePath(Path.Combine(outputDir, baseName + ext));
            if (format == "mp3")
            {
                byte[] tag = BuildId3v23(title, artist, album, cover);
                await using var src = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                await using var dst = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await dst.WriteAsync(tag, ct);
                await src.CopyToAsync(dst, ct);
            }
            else
            {
                File.Move(tempPath, outPath); // FLAC 不写 ID3（规范外），保持音频原样
            }

            return new NcmConvertResult(outPath, title, artist, format);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    // ========== meta JSON ==========

    private static (string? Format, string? Title, string? Artist, string? Album) ParseMeta(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return (null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null, null);

            string? format = GetStr(root, "format")?.ToLowerInvariant();
            string? title = GetStr(root, "musicName");
            string? album = GetStr(root, "album");

            string? artist = null;
            if (root.TryGetProperty("artist", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2 &&
                        item[1].ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(item[1].GetString()))
                    {
                        names.Add(item[1].GetString()!);
                    }
                }
                if (names.Count > 0) artist = string.Join("/", names);
            }
            return (format, title, artist, album);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static string? GetStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    // ========== 音频格式嗅探 ==========

    private static string SniffFormat(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 0x66 && head[1] == 0x4C && head[2] == 0x61 && head[3] == 0x43) return "flac"; // fLaC
        if (head.Length >= 3 && head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33) return "mp3"; // ID3
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "mp3";           // MPEG sync
        return "mp3";
    }

    // ========== 密钥盒（RC4-like KSA 变体）与音频异或 ==========

    private static byte[] BuildKeyBox(byte[] key)
    {
        if (key.Length == 0) throw new InvalidDataException("NCM 密钥为空。");
        var box = new byte[256];
        for (int i = 0; i < 256; i++) box[i] = (byte)i;
        int c = 0, lastByte = 0;
        for (int i = 0; i < 256; i++)
        {
            int keyOffset = (box[i] + lastByte + key[c]) & 0xFF;
            c = (c + 1) % key.Length;
            (box[i], box[keyOffset]) = (box[keyOffset], box[i]);
            lastByte = keyOffset;
        }
        return box;
    }

    private static void XorKeyBox(Span<byte> data, byte[] box, long absoluteOffset)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= box[(int)((absoluteOffset + i + 1) & 0xFF)];
        }
    }

    private static byte[] Xor(byte[] data, byte mask)
    {
        for (int i = 0; i < data.Length; i++) data[i] ^= mask;
        return data;
    }

    // ========== AES-128-ECB ==========

    private static byte[] AesEcbDecrypt(byte[] key, byte[] cipher)
    {
        if (cipher.Length == 0 || cipher.Length % 16 != 0)
            throw new InvalidDataException("AES 密文长度不是 16 的倍数。");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        byte[] plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
        return Unpad(plain);
    }

    /// <summary>先按 PKCS7 尝试去填充，失败则回退为去除尾部 0x00。</summary>
    private static byte[] Unpad(byte[] data)
    {
        if (data.Length > 0)
        {
            byte last = data[^1];
            if (last is >= 1 and <= 16 && data.Length >= last)
            {
                bool consistent = true;
                for (int i = data.Length - last; i < data.Length; i++)
                {
                    if (data[i] != last) { consistent = false; break; }
                }
                if (consistent) return data[..^last];
            }
        }
        int end = data.Length;
        while (end > 0 && data[end - 1] == 0) end--;
        return data[..end];
    }

    // ========== ID3v2.3 标签 ==========

    private static byte[] BuildId3v23(string? title, string? artist, string? album, byte[]? cover)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            void TextFrame(string id, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                // 编码 0x01 = UTF-16 带 BOM（ID3v2.3 对中文最稳妥）
                byte[] text = Encoding.Unicode.GetBytes(value.Trim());
                var body = new byte[1 + text.Length + 2];
                body[0] = 0x01;
                body[1] = 0xFF; body[2] = 0xFE; // BOM
                text.CopyTo(body, 3);
                WriteFrame(w, id, body);
            }

            TextFrame("TIT2", title);
            TextFrame("TPE1", artist);
            TextFrame("TALB", album);

            if (cover is { Length: > 0 } && cover.Length <= MaxImageBytes)
            {
                string mime = cover.Length >= 4 && cover[0] == 0x89 && cover[1] == 0x50 ? "image/png" : "image/jpeg";
                var body = new List<byte>
                {
                    0x00, // 编码 ISO-8859-1
                };
                body.AddRange(Encoding.ASCII.GetBytes(mime));
                body.Add(0x00);
                body.Add(0x03); // 封面（front cover）
                body.Add(0x00); // 空描述
                body.AddRange(cover);
                WriteFrame(w, "APIC", body.ToArray());
            }
        }

        byte[] frames = ms.ToArray();
        var header = new byte[10];
        header[0] = (byte)'I'; header[1] = (byte)'D'; header[2] = (byte)'3';
        header[3] = 0x03; header[4] = 0x00; // v2.3
        header[5] = 0x00;                   // flags
        WriteSyncSafe(header.AsSpan(6), frames.Length);
        return header.Concat(frames).ToArray();
    }

    private static void WriteFrame(BinaryWriter w, string id, byte[] body)
    {
        w.Write(Encoding.ASCII.GetBytes(id));
        w.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)body.Length)); // v2.3 大端
        w.Write((short)0); // flags
        w.Write(body);
    }

    private static void WriteSyncSafe(Span<byte> dst, int value)
    {
        dst[0] = (byte)((value >> 21) & 0x7F);
        dst[1] = (byte)((value >> 14) & 0x7F);
        dst[2] = (byte)((value >> 7) & 0x7F);
        dst[3] = (byte)(value & 0x7F);
    }

    // ========== 基础 IO ==========

    private static int ReadInt32(Stream s)
    {
        Span<byte> buf = stackalloc byte[4];
        ReadExactly(s, buf);
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    private static byte[] ReadBlock(Stream s, int length)
    {
        if (length < 0 || length > (1 << 26)) throw new InvalidDataException("NCM 段长度异常。");
        var buf = new byte[length];
        ReadExactly(s, buf);
        return buf;
    }

    private static byte[] ReadBlock(Stream s) => ReadBlock(s, ReadInt32(s));

    private static void ReadExactly(Stream s, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf[total..]);
            if (n <= 0) throw new EndOfStreamException("NCM 文件不完整或已损坏。");
            total += n;
        }
    }

    private static async Task<int> ReadAudioChunkAsync(Stream src, Stream dst, byte[] chunk, byte[] box, CancellationToken ct)
    {
        int n = await src.ReadAsync(chunk.AsMemory(), ct);
        XorKeyBox(chunk.AsSpan(0, n), box, 0);
        await dst.WriteAsync(chunk.AsMemory(0, n), ct);
        return n;
    }

    // ========== 输出路径 ==========

    private static string BuildOutputName(string? title, string? artist, string ncmPath)
    {
        string fallback = Path.GetFileNameWithoutExtension(ncmPath);
        if (string.IsNullOrWhiteSpace(title)) return Sanitize(fallback);
        var name = string.IsNullOrWhiteSpace(artist) ? title! : $"{artist} - {title}";
        var sanitized = Sanitize(name);
        return string.IsNullOrWhiteSpace(sanitized) ? Sanitize(fallback) : sanitized;
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
        }
        return sb.ToString().Trim();
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
