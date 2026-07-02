// -----------------------------------------------------------------------------
//  TotalControl — Window screen recorder (→ MP4 via FFmpeg)
//
//  Licensed under the MIT License. See LICENSE in the project root for details.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Records a single window to an H.264 MP4. Frames are captured with the same
//  DWM-aware PrintWindow path the screenshot tools use (so occluded / DWM /
//  Chromium / UWP surfaces still record correctly), then piped as raw BGRA to
//  FFmpeg's stdin, which encodes them to MP4. The recording is cancellable: a
//  user-abort (✕ on the control frame) cancels the token, the loop stops, and
//  the partial MP4 is finalized cleanly.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace TotalControl;

internal static class ScreenRecorder
{
    /// <summary>
    /// Record <paramref name="hwnd"/> for up to <paramref name="durationSeconds"/> at
    /// <paramref name="fps"/> into <paramref name="outputPath"/> (MP4). Returns a summary string.
    /// Honors <paramref name="ct"/> — cancellation stops early and still finalizes the file.
    /// </summary>
    public static string Record(IntPtr hwnd, string title, int durationSeconds, int fps, string outputPath, CancellationToken ct)
    {
        string ffmpeg = FindFfmpeg()
            ?? throw new InvalidOperationException(
                "FFmpeg was not found. record_window needs FFmpeg on PATH (or set the TOTALCONTROL_FFMPEG " +
                "environment variable to ffmpeg.exe). Install it with:  winget install Gyan.FFmpeg");

        // Lock capture dimensions at the start (FFmpeg rawvideo needs a constant frame size).
        var (w, h) = GetWindowSize(hwnd);
        if (w < 2 || h < 2) throw new InvalidOperationException($"Window '{title}' has no capturable area ({w}×{h}).");
        // H.264 requires even dimensions.
        w -= w % 2; h -= h % 2;

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Raw BGRA frames in on stdin → H.264 yuv420p MP4 out. faststart for streamable playback.
        foreach (var a in new[]
        {
            "-y",
            "-f", "rawvideo",
            "-pixel_format", "bgra",
            "-video_size", $"{w}x{h}",
            "-framerate", fps.ToString(),
            "-i", "-",
            "-an",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            outputPath,
        }) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var ffmpegErr = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) ffmpegErr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();

        var stdin = proc.StandardInput.BaseStream;
        int frameStride = w * 4;
        var rowBuffer = new byte[frameStride * h];

        var sw = Stopwatch.StartNew();
        long frameIntervalTicks = TimeSpan.TicksPerSecond / fps;
        int framesWritten = 0;
        bool cancelled = false;

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            long nextFrameTick = 0;
            long totalTicks = (long)durationSeconds * TimeSpan.TicksPerSecond;

            while (sw.Elapsed.Ticks < totalTicks)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }

                CaptureInto(hwnd, bmp, w, h);

                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    // Copy row by row to collapse any stride padding into a tight buffer.
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, rowBuffer, y * frameStride, frameStride);
                }
                finally { bmp.UnlockBits(data); }

                stdin.Write(rowBuffer, 0, rowBuffer.Length);
                framesWritten++;

                // Pace to the target fps.
                nextFrameTick += frameIntervalTicks;
                long waitTicks = nextFrameTick - sw.Elapsed.Ticks;
                if (waitTicks > 0)
                {
                    int ms = (int)(waitTicks / TimeSpan.TicksPerMillisecond);
                    if (ms > 0)
                    {
                        try { Task.Delay(ms, ct).Wait(ct); }
                        catch (OperationCanceledException) { cancelled = true; break; }
                    }
                }
            }
        }
        finally
        {
            try { stdin.Flush(); stdin.Close(); } catch { /* pipe may already be closed */ }
        }

        if (!proc.WaitForExit(15000))
        {
            try { proc.Kill(true); } catch { /* best effort */ }
            throw new InvalidOperationException("FFmpeg did not finalize the MP4 within 15 s and was terminated.");
        }

        if (proc.ExitCode != 0 && !cancelled)
        {
            var tail = Tail(ffmpegErr.ToString(), 6);
            throw new InvalidOperationException($"FFmpeg exited with code {proc.ExitCode}. Last output:\n{tail}");
        }

        double secs = framesWritten / (double)fps;
        long sizeKb = File.Exists(outputPath) ? new FileInfo(outputPath).Length / 1024 : 0;
        string status = cancelled ? "STOPPED EARLY (user abort or cancellation)" : "completed";
        return $"Recording {status}: '{title}' → {outputPath}\n" +
               $"  {framesWritten} frame(s) at {fps} fps (~{secs:0.0}s), {w}×{h}, {sizeKb} KB.";
    }

    /// <summary>Capture the window into an existing bitmap using PrintWindow, with a screen-copy fallback.</summary>
    private static void CaptureInto(IntPtr hwnd, Bitmap bmp, int w, int h)
    {
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        bool ok;
        try { ok = Win32.PrintWindow(hwnd, hdc, Win32.PW_RENDERFULLCONTENT); }
        finally { g.ReleaseHdc(hdc); }

        if (!ok)
        {
            var (x, y) = GetWindowOrigin(hwnd);
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }
    }

    private static (int w, int h) GetWindowSize(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Win32.RECT>()) != 0
            || r.Width <= 0 || r.Height <= 0)
        {
            Win32.GetWindowRect(hwnd, out r);
        }
        return (Math.Max(1, r.Width), Math.Max(1, r.Height));
    }

    private static (int x, int y) GetWindowOrigin(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Win32.RECT>()) != 0
            || r.Width <= 0)
        {
            Win32.GetWindowRect(hwnd, out r);
        }
        return (r.Left, r.Top);
    }

    /// <summary>Locate ffmpeg.exe: explicit env var → PATH → WinGet Links → common install dirs.</summary>
    public static string? FindFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("TOTALCONTROL_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        // PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var cand = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { /* malformed PATH entry */ }
        }

        // Common explicit locations.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    private static string Tail(string s, int lines)
    {
        var arr = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", arr.Skip(Math.Max(0, arr.Length - lines)));
    }
}
