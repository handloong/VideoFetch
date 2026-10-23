using System;
using System.IO;
using System.Linq;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 下载引擎的离线测试。
///
/// 这块是全项目最核心又最没保障的部分：419 行、原来 0 测试。这里覆盖
/// 纯解析逻辑（播放列表 → 分段 / 选最优子列表）和容器判定，不需要联网。
/// </summary>
internal static class DownloadEngineTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[download-engine]");
        var before = _failed;

        Safe("parse-segments", ParseSegments);
        Safe("best-sub-playlist", BestSubPlaylist);
        Safe("container", ContainerDetection);
        Safe("unique-path", UniquePath);
        Safe("init-segment", InitSegment);
        Safe("encryption", EncryptionDetection);

        return _failed == before;
    }

    private static void Safe(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static readonly Uri Base = new("https://cdn.test/abc==,123/video/0/hls.m3u8");

    // ══════════════════════════════════════════════════════════
    //  分段解析
    // ══════════════════════════════════════════════════════════

    private static void ParseSegments()
    {
        var playlist = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #EXT-X-MEDIA-SEQUENCE:0
            #EXTINF:10.000000,
            hls-720p-c97ca0.ts
            #EXTINF:10.000000,
            hls-720p-c97ca1.ts
            #EXTINF:4.133333,
            https://other.cdn/abs-segment.ts
            #EXT-X-ENDLIST
            """;

        var segs = DownloadEngine.ParseSegments(playlist, Base);

        Check("segments: 解析出 3 段", segs.Count == 3, segs.Count.ToString());
        Check("segments: 相对地址补全为绝对地址",
            segs[0] == "https://cdn.test/abc==,123/video/0/hls-720p-c97ca0.ts", segs[0]);
        Check("segments: 保持原始顺序", segs[1].EndsWith("hls-720p-c97ca1.ts"), segs[1]);
        Check("segments: 绝对地址原样保留", segs[2] == "https://other.cdn/abs-segment.ts", segs[2]);
        Check("segments: 空播放列表返回空", DownloadEngine.ParseSegments("#EXTM3U\n", Base).Count == 0);
    }

    // ══════════════════════════════════════════════════════════
    //  主播放列表 → 最优子列表
    // ══════════════════════════════════════════════════════════

    private static void BestSubPlaylist()
    {
        // 故意把 480p 写在最前面，确认不是"取第一条"
        var master = """
            #EXTM3U
            #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=763904,RESOLUTION=854x480,NAME="480p"
            hls-480p.m3u8
            #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=1327104,RESOLUTION=1280x720,NAME="720p"
            hls-720p.m3u8
            #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=155648,RESOLUTION=444x250,NAME="250p"
            hls-250p.m3u8
            """;

        var best = DownloadEngine.ParseBestSubPlaylist(master, Base);
        Check("sub-playlist: 取码率最高的那档",
            best == "https://cdn.test/abc==,123/video/0/hls-720p.m3u8", best);

        // 绝对地址的子列表
        var absolute = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=100,RESOLUTION=320x180
            bad.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=1280x720
            https://other.cdn/720.m3u8
            """;
        Check("sub-playlist: 绝对地址子列表",
            DownloadEngine.ParseBestSubPlaylist(absolute, Base) == "https://other.cdn/720.m3u8");

        // 没有 STREAM-INF（已经是媒体播放列表）→ 抛异常，让调用方知道走错了
        var media = "#EXTM3U\n#EXTINF:10,\nseg1.ts\n";
        var threw = false;
        try { DownloadEngine.ParseBestSubPlaylist(media, Base); }
        catch (InvalidDataException) { threw = true; }
        Check("sub-playlist: 媒体播放列表抛 InvalidDataException", threw);
    }

    // ══════════════════════════════════════════════════════════
    //  容器判定（决定 .ts 要不要改成 .mp4）
    // ══════════════════════════════════════════════════════════

    private static void ContainerDetection()
    {
        // fMP4/CMAF：前 4 字节是 box 长度，跟着 "ftyp"
        var ftyp = new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                                (byte)'i', (byte)'s', (byte)'o', (byte)'m' };
        Check("container: ftyp 识别为 MP4", DownloadEngine.IsFragmentedMp4Header(ftyp));

        // MPEG-TS：0x47 同步字节
        var ts = new byte[] { 0x47, 0x40, 0x11, 0x10, 0x00, 0x42, 0xF0, 0x25,
                              0x00, 0x01, 0xC1, 0x00 };
        Check("container: TS 不会被误判成 MP4", !DownloadEngine.IsFragmentedMp4Header(ts));

        Check("container: 长度不足时返回 false",
            !DownloadEngine.IsFragmentedMp4Header(new byte[] { 0, 0, 0, 0x18, (byte)'f' }));
        Check("container: 空数组返回 false", !DownloadEngine.IsFragmentedMp4Header(Array.Empty<byte>()));
    }

    // ══════════════════════════════════════════════════════════
    //  重封装后的目标路径
    // ══════════════════════════════════════════════════════════

    private static void UniquePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vf-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var ts = Path.Combine(dir, "video.ts");
            var mp4 = Path.Combine(dir, "video.mp4");

            Check("unique-path: 不存在时直接用同名 mp4",
                DownloadEngine.UniqueMp4Path(ts) == mp4);

            File.WriteAllText(mp4, "x");
            Check("unique-path: 已存在时加 (1)",
                DownloadEngine.UniqueMp4Path(ts) == Path.Combine(dir, "video (1).mp4"),
                DownloadEngine.UniqueMp4Path(ts));

            File.WriteAllText(Path.Combine(dir, "video (1).mp4"), "x");
            Check("unique-path: 继续递增到 (2)",
                DownloadEngine.UniqueMp4Path(ts) == Path.Combine(dir, "video (2).mp4"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  fMP4 初始化段 / AES-128 加密
    // ══════════════════════════════════════════════════════════

    private static void InitSegment()
    {
        string M3u8(params string[] lines) => string.Join("\n", lines);

        var fmp4 = M3u8(
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            "#EXT-X-MAP:URI=\"init.mp4\"",
            "#EXTINF:4.0,",
            "seg1.m4s");

        var init = DownloadEngine.ParseInitSegment(fmp4, Base);
        Check("init-segment: 解析出初始化段",
            init == "https://cdn.test/abc==,123/video/0/init.mp4", init);

        var withRange = M3u8(
            "#EXTM3U",
            "#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"720@0\"",
            "#EXTINF:4.0,",
            "seg1.m4s");
        Check("init-segment: 带 BYTERANGE 也能解析",
            DownloadEngine.ParseInitSegment(withRange, Base)?.EndsWith("/init.mp4") == true,
            DownloadEngine.ParseInitSegment(withRange, Base));

        var absolute = M3u8(
            "#EXTM3U",
            "#EXT-X-MAP:URI=\"https://other.cdn/init.mp4\"");
        Check("init-segment: 绝对地址原样返回",
            DownloadEngine.ParseInitSegment(absolute, Base) == "https://other.cdn/init.mp4");

        var ts = M3u8("#EXTM3U", "#EXTINF:10,", "seg1.ts");
        Check("init-segment: TS 列表返回 null", DownloadEngine.ParseInitSegment(ts, Base) == null);
    }

    private static void EncryptionDetection()
    {
        string M3u8(params string[] lines) => string.Join("\n", lines);

        Check("encryption: 明文流不误判",
            !DownloadEngine.IsEncrypted(M3u8("#EXTM3U", "#EXT-X-KEY:METHOD=NONE", "#EXTINF:10,", "a.ts")));

        Check("encryption: 识别 AES-128",
            DownloadEngine.IsEncrypted(M3u8("#EXTM3U", "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"", "a.ts")));

        Check("encryption: 识别 SAMPLE-AES",
            DownloadEngine.IsEncrypted("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"k.bin\""));

        Check("encryption: 无 KEY 行不误判",
            !DownloadEngine.IsEncrypted(M3u8("#EXTM3U", "#EXTINF:10,", "a.ts")));
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}