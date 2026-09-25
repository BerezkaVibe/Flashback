using System;
using System.IO;
using FFmpeg.AutoGen;

namespace Flashback;

// FFmpeg's libraries (avcodec, avformat, avutil, swresample, swscale, avfilter), loaded from the "ffmpeg"
// folder beside the app for the FFmpeg preview player. Exports and recording keep using tools\ffmpeg.exe.
// The folder holds an LGPL shared build (see THIRD-PARTY-NOTICES.md); without it the Windows player is used.
internal static class FfmpegLibrary
{
    private static bool? available;
    internal static string? Error { get; private set; }
    // Where the libraries are: the app's ffmpeg folder, or (for tests elsewhere) a folder named in
    // FLASHBACK_FFMPEG_LIBS.
    internal static string Folder => Environment.GetEnvironmentVariable("FLASHBACK_FFMPEG_LIBS") is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    internal static bool Available
    {
        get
        {
            if (available is bool known) return known;
            try
            {
                ffmpeg.RootPath = Folder;
                // Touching each library loads it; a missing or mismatched one throws here rather than mid-playback.
                _ = ffmpeg.avformat_version(); _ = ffmpeg.avcodec_version(); _ = ffmpeg.avutil_version();
                _ = ffmpeg.swresample_version(); _ = ffmpeg.swscale_version(); _ = ffmpeg.avfilter_version();
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                available = true;
            }
            catch (Exception ex) { Error = ex.Message; available = false; }
            return available.Value;
        }
    }
    // An FFmpeg error code as text.
    internal static unsafe string Describe(int error)
    {
        const int size = 256; var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, size);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"error {error}";
    }
    internal static void Check(int result, string what)
    {
        if (result < 0) throw new IOException($"{what}: {Describe(result)}");
    }
}
