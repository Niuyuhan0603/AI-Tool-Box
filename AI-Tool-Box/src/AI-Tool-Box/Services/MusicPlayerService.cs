using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AI_Tool_Box.Services;

/// <summary>一首歌曲的信息（借鉴 MusicPlayer2 的 SongInfo 结构，精简为展示所需字段）。</summary>
public sealed class SongItem
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>列表显示主标题（有标签用标签，否则用文件名）。</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(FileName) : Title;

    /// <summary>列表副标题（艺术家 - 专辑，缺失时回退文件名）。</summary>
    public string DisplaySubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Artist) && !string.IsNullOrWhiteSpace(Album)) return $"{Artist} · {Album}";
            if (!string.IsNullOrWhiteSpace(Artist)) return Artist;
            return string.IsNullOrWhiteSpace(Title) ? FileName : string.Empty;
        }
    }

    public string DurationText
    {
        get
        {
            if (Duration <= TimeSpan.Zero) return "--:--";
            return $"{(int)Duration.TotalMinutes:00}:{Duration.Seconds:00}";
        }
    }

    public string SearchKey => (DisplayTitle + " " + Artist + " " + Album).ToLowerInvariant();

    /// <summary>收藏图标显示（♥ 已收藏 / ♡ 未收藏）。</summary>
    public string FavDisplay => IsFavorite ? "♥" : "♡";
}

/// <summary>一行歌词（借鉴 MusicPlayer2 的 CLyrics::Lyric 结构：时间标签 + 文本）。</summary>
public sealed class LyricLine
{
    public TimeSpan Time { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// 音乐播放器核心服务（借鉴 MusicPlayer2 源码思路，纯 C# 实现，不依赖其窗口/exe）：
///   • 文件夹扫描 + 标签/时长读取（Windows.Storage MusicProperties）
///   • 专辑封面读取（系统 Shell 缩略图，自动提取内嵌曲绘）
///   • LRC 歌词解析（多时间标签 / offset / 翻译行）
///   • 收藏持久化（%LOCALAPPDATA%\AI-Tool-Box\music_favorites.json）
/// 播放使用 Windows.Media.Playback.MediaPlayer（系统 Media Foundation 解码）。
/// </summary>
public static class MusicPlayerService
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(MusicPlayerService));

    /// <summary>支持的音频扩展名（等价 CAudioCommon 支持格式的常用子集）。</summary>
    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".ape"
    };

    private static readonly string FavoritesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AI-Tool-Box", "music_favorites.json");

    /// <summary>判断文件是否为受支持的音频文件。</summary>
    public static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path);
        return Array.Exists(AudioExtensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>递归扫描文件夹下所有音频文件，返回按文件名排序的列表。</summary>
    public static List<string> ScanFolder(string rootDir)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
            {
                if (IsAudioFile(file)) result.Add(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "扫描音乐文件夹失败：{Dir}", rootDir);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        Log.Information("扫描音乐文件夹完成：{Dir}，共 {Count} 首", rootDir, result.Count);
        return result;
    }

    /// <summary>读取歌曲标签（标题/艺术家/专辑/时长）。WinRT 异步，可在线程池调用。</summary>
    public static async Task LoadSongMetadataAsync(SongItem song)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(song.Path);
            var props = await file.Properties.GetMusicPropertiesAsync();
            song.Title = props.Title ?? string.Empty;
            song.Artist = props.Artist ?? string.Empty;
            song.Album = props.Album ?? string.Empty;
            if (props.Duration > TimeSpan.Zero) song.Duration = props.Duration;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "读取歌曲标签失败：{Path}", song.Path);
        }
    }

    /// <summary>读取专辑封面：优先解析音频文件内嵌曲绘（mp3 ID3v2 APIC / flac PICTURE / m4a covr），
    /// 解析不到再回退系统 Shell 缩略图（与 MusicPlayer2 读取标签曲绘一致）。无封面返回 null。</summary>
    public static async Task<BitmapImage?> LoadCoverAsync(string path, uint size = 300)
    {
        try
        {
            // 1) 内嵌曲绘（同步解析避免阻塞 UI）
            var embedded = await System.Threading.Tasks.Task.Run(() => ExtractEmbeddedCover(path));
            if (embedded is { Length: > 0 })
            {
                var ms = new MemoryStream(embedded);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                return bmp;
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "解析内嵌曲绘失败：{Path}", path);
        }

        try
        {
            // 2) 兜底：系统 Shell 缩略图
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetScaledImageAsThumbnailAsync(ThumbnailMode.MusicView, size, ThumbnailOptions.UseCurrentScale);
            if (thumb is null) return null;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumb);
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "读取封面失败：{Path}", path);
            return null;
        }
    }

    /// <summary>从音频文件提取内嵌曲绘字节（mp3 / flac / m4a）；无则返回 null。</summary>
    private static byte[]? ExtractEmbeddedCover(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 16) return null;

            // --- MP3: ID3v2 APIC ---
            if (fs.Length > 10)
            {
                fs.Position = 0;
                var id3 = new byte[10];
                if (fs.Read(id3, 0, 10) == 10 && id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
                {
                    int id3Size = (id3[6] << 21) | (id3[7] << 14) | (id3[8] << 7) | id3[9];
                    if (id3Size > 0 && fs.Length > 10 + id3Size)
                    {
                        var apic = ReadMp3Apic(fs, 10, id3Size, id3[3] == 2);
                        if (apic is not null) return apic;
                    }
                }
            }

            // --- FLAC: PICTURE metadata block ---
            fs.Position = 0;
            var flacMagic = new byte[4];
            if (fs.Read(flacMagic, 0, 4) == 4 && flacMagic[0] == 'f' && flacMagic[1] == 'L' && flacMagic[2] == 'a' && flacMagic[3] == 'C')
            {
                var pic = ReadFlacPicture(fs);
                if (pic is not null) return pic;
            }

            // --- M4A/MP4: ilst covr ---
            fs.Position = 0;
            var covr = ReadMp4Covr(fs);
            if (covr is not null) return covr;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "提取内嵌曲绘异常：{Path}", path);
        }
        return null;
    }

    /// <summary>ID3v2 标签内找 APIC/PIC（v2.3/2.4 的 APIC，v2.2 的 PIC）帧，返回图片数据。</summary>
    private static byte[]? ReadMp3Apic(FileStream fs, int tagStart, int tagSize, bool v22)
    {
        try
        {
            fs.Position = tagStart;
            var tagData = new byte[tagSize];
            if (fs.Read(tagData, 0, tagSize) != tagSize) return null;

            int pos = 0;
            while (pos + 10 <= tagSize)
            {
                // v2.3/2.4: ID(4) + Size(4) + Flags(2)；v2.2: ID(3) + Size(3)
                string id;
                int frameSize;
                if (v22)
                {
                    id = Encoding.ASCII.GetString(tagData, pos, 3);
                    frameSize = (tagData[pos + 3] << 16) | (tagData[pos + 4] << 8) | tagData[pos + 5];
                    pos += 6;
                }
                else
                {
                    id = Encoding.ASCII.GetString(tagData, pos, 4);
                    int b0 = tagData[pos + 4], b1 = tagData[pos + 5], b2 = tagData[pos + 6], b3 = tagData[pos + 7];
                    bool syncsafe = (b0 & 0x80) == 0 && (b1 & 0x80) == 0 && (b2 & 0x80) == 0 && (b3 & 0x80) == 0;
                    frameSize = syncsafe
                        ? (b0 << 21) | (b1 << 14) | (b2 << 7) | b3
                        : (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
                    pos += 10; // ID + Size + 2 flags
                }
                if (frameSize <= 0 || pos + frameSize > tagSize) break;

                string wantId = v22 ? "PIC" : "APIC";
                if (id == wantId && frameSize > 12)
                {
                    int p = pos;
                    int enc = tagData[p];
                    p++;
                    // MIME 字符串（以 0 结尾）
                    int mimeEnd = Array.IndexOf(tagData, (byte)0, p, frameSize - (p - pos));
                    if (mimeEnd < 0) { pos += frameSize; continue; }
                    p = mimeEnd + 1;
                    p++; // 图片类型（1B）
                    // 描述字符串（编码相关终止符：UTF-16 为 00 00，其它为 00）
                    int descTerm = enc == 1 || enc == 2 ? 2 : 1;
                    int descStart = p;
                    int descEnd = descStart;
                    while (descEnd + descTerm <= pos + frameSize)
                    {
                        bool hit = true;
                        for (int k = 0; k < descTerm; k++)
                            if (tagData[descEnd + k] != 0) { hit = false; break; }
                        if (hit) break;
                        descEnd++;
                    }
                    p = descEnd + descTerm;
                    if (p < pos + frameSize)
                    {
                        var data = new byte[frameSize - (p - pos)];
                        Array.Copy(tagData, p, data, 0, data.Length);
                        return data;
                    }
                }
                pos += frameSize;
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "解析 APIC 帧失败");
        }
        return null;
    }

    /// <summary>FLAC 元数据块中找 PICTURE（type 6）块，返回图片数据。</summary>
    private static byte[]? ReadFlacPicture(FileStream fs)
    {
        try
        {
            bool last = false;
            while (!last)
            {
                var header = new byte[4];
                if (fs.Read(header, 0, 4) != 4) return null;
                last = (header[0] & 0x80) != 0;
                int type = header[0] & 0x7F;
                int blockSize = (header[1] << 16) | (header[2] << 8) | header[3];
                if (blockSize <= 0 || blockSize > 16 * 1024 * 1024) return null;

                if (type == 6) // PICTURE
                {
                    var block = new byte[blockSize];
                    if (fs.Read(block, 0, blockSize) != blockSize) return null;
                    int p = 0;
                    p += 4; // 图片类型
                    int mimeLen = ReadBeInt32(block, p); p += 4 + mimeLen;
                    if (p > blockSize) return null;
                    int descLen = ReadBeInt32(block, p); p += 4 + descLen;
                    p += 4 + 4 + 4 + 4; // 宽/高/位深/颜色数
                    if (p + 4 > blockSize) return null;
                    int dataLen = ReadBeInt32(block, p); p += 4;
                    if (p + dataLen > blockSize) return null;
                    var data = new byte[dataLen];
                    Array.Copy(block, p, data, 0, dataLen);
                    return data;
                }
                fs.Position += blockSize;
            }
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "解析 FLAC PICTURE 失败");
        }
        return null;
    }

    /// <summary>MP4/M4A 中递归查找 ilst 内的 covr（封面）box，返回图片数据。</summary>
    private static byte[]? ReadMp4Covr(FileStream fs)
    {
        try
        {
            return FindMp4Covr(fs, fs.Length);
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "解析 MP4 covr 失败");
        }
        return null;
    }

    private static byte[]? FindMp4Covr(FileStream fs, long end)
    {
        while (fs.Position + 8 <= end)
        {
            var head = new byte[8];
            if (fs.Read(head, 0, 8) != 8) return null;
            long boxSize = ((long)head[0] << 24) | ((long)head[1] << 16) | ((long)head[2] << 8) | head[3];
            string type = Encoding.ASCII.GetString(head, 4, 4);
            if (boxSize == 1)
            {
                var ext = new byte[8];
                if (fs.Read(ext, 0, 8) != 8) return null;
                boxSize = 0;
                for (int i = 0; i < 8; i++) boxSize = (boxSize << 8) | ext[i];
                boxSize -= 16;
            }
            else if (boxSize == 0)
            {
                boxSize = end - fs.Position;
            }
            else
            {
                boxSize -= 8;
            }
            if (boxSize < 0) return null;

            if (type is "moov" or "udta" or "ilst" or "trak" or "mdia" or "minf" or "stbl")
            {
                var inner = FindMp4Covr(fs, fs.Position + boxSize);
                if (inner is not null) return inner;
                fs.Position += boxSize;
                continue;
            }
            if (type == "meta")
            {
                // meta box 内含 4B version/flags（FullBox），之后才是子 box
                if (boxSize >= 4)
                {
                    fs.Position += 4;
                    var inner = FindMp4Covr(fs, fs.Position + boxSize - 4);
                    if (inner is not null) return inner;
                    fs.Position += boxSize - 4;
                }
                continue;
            }
            if (type == "covr")
            {
                // covr 内含 data box：size(4) + 'data'(4) + type(4) + locale(4) + 图片数据
                if (boxSize >= 16)
                {
                    var dataHead = new byte[16];
                    if (fs.Read(dataHead, 0, 16) != 16) return null;
                    int dataSize = ((int)dataHead[0] << 24) | ((int)dataHead[1] << 16) | ((int)dataHead[2] << 8) | dataHead[3];
                    if (Encoding.ASCII.GetString(dataHead, 4, 4) == "data" && dataSize > 16)
                    {
                        int payload = dataSize - 16;
                        var data = new byte[payload];
                        if (fs.Read(data, 0, payload) == payload) return data;
                    }
                }
                return null;
            }
            fs.Position += boxSize;
        }
        return null;
    }

    private static int ReadBeInt32(byte[] buf, int offset)
    {
        if (offset + 4 > buf.Length) return 0;
        return (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
    }

    /// <summary>查找与音频同名的歌词文件（.lrc，同目录）。</summary>
    public static string? FindLyricFile(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        var lrc = Path.Combine(dir, baseName + ".lrc");
        if (File.Exists(lrc)) return lrc;
        // 兼容 .LRC 大写扩展名
        lrc = Path.Combine(dir, baseName + ".LRC");
        return File.Exists(lrc) ? lrc : null;
    }

    /// <summary>解析 LRC 歌词文本（多时间标签、[offset:±ms]、翻译行）。返回按时间排序的行列表。</summary>
    private static readonly Regex TimeTagRegex = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    public static List<LyricLine> ParseLrc(string lrcText)
    {
        var lines = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrcText)) return lines;

        int offsetMs = 0;
        var offsetMatch = Regex.Match(lrcText, @"\[offset:\s*([+-]?\d+)\]");
        if (offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, out var off)) offsetMs = off;

        foreach (var raw in lrcText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var matches = TimeTagRegex.Matches(line);
            if (matches.Count == 0) continue;

            // 去掉所有时间标签后剩余部分即歌词文本（[ti:][ar:] 等元标签忽略）
            var text = TimeTagRegex.Replace(line, string.Empty).Trim();
            text = text.StartsWith("[") ? Regex.Replace(text, @"^\[[^\]]*\]", string.Empty).Trim() : text;

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    ms = frac.Length switch { 1 => int.Parse(frac) * 100, 2 => int.Parse(frac) * 10, _ => int.Parse(frac) };
                }
                var time = TimeSpan.FromMilliseconds(((min * 60 + sec) * 1000 + ms) + offsetMs);
                if (time < TimeSpan.Zero) time = TimeSpan.Zero;
                lines.Add(new LyricLine { Time = time, Text = text });
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    /// <summary>加载并解析音频对应的 LRC 歌词文件（自动按 UTF-8/GBK/ANSI 识别编码）。</summary>
    public static async Task<List<LyricLine>> LoadLyricsAsync(string audioPath)
    {
        var lrcPath = FindLyricFile(audioPath);
        if (lrcPath is null) return new List<LyricLine>();
        try
        {
            var bytes = await File.ReadAllBytesAsync(lrcPath);
            var text = DecodeLyricText(bytes);
            return ParseLrc(text);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取歌词文件失败：{Path}", lrcPath);
            return new List<LyricLine>();
        }
    }

    private static string DecodeLyricText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // 尝试 UTF-8 严格解码；失败回退 GBK(936)
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8.GetString(bytes);
        }
        catch
        {
            try
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
            catch
            {
                return Encoding.Default.GetString(bytes);
            }
        }
    }

    // ==================== 收藏 ====================

    public static List<string> LoadFavorites()
    {
        try
        {
            if (!File.Exists(FavoritesPath)) return new List<string>();
            var json = File.ReadAllText(FavoritesPath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取收藏列表失败");
            return new List<string>();
        }
    }

    public static void SaveFavorites(IEnumerable<string> paths)
    {
        try
        {
            var dir = Path.GetDirectoryName(FavoritesPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(paths.Distinct().ToList()));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存收藏列表失败");
        }
    }

    /// <summary>把歌曲列表应用到收藏标记（供页面绑定后统一刷新）。</summary>
    public static void ApplyFavorites(IEnumerable<SongItem> songs)
    {
        var favs = LoadFavorites().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in songs) s.IsFavorite = favs.Contains(s.Path);
    }

    public static void ToggleFavorite(SongItem song, ObservableCollection<SongItem> allSongs)
    {
        song.IsFavorite = !song.IsFavorite;
        var favs = allSongs.Where(s => s.IsFavorite).Select(s => s.Path).ToList();
        SaveFavorites(favs);
    }
}
