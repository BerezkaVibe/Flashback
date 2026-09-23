using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Flashback;

// Desktop textures are resized and converted by the GPU video processor before readback.
// Only the requested NV12 output size crosses to the existing persistent encoder pipe.
internal sealed class GpuDesktopCapture : IDisposable
{
    private ID3D11Device device = null!;
    private ID3D11DeviceContext context = null!;
    private IDXGIOutputDuplication duplication = null!;
    private ID3D11VideoDevice videoDevice = null!;
    private ID3D11VideoContext videoContext = null!;
    private ID3D11VideoProcessorEnumerator enumerator = null!;
    private ID3D11VideoProcessor processor = null!;
    private ID3D11Texture2D input = null!, output = null!;
    private readonly ID3D11Texture2D?[] staging = new ID3D11Texture2D?[3];
    private int head, pending;
    private bool ownsDesktopFrame;
    private IntPtr cachedCursor;
    private int hotspotX, hotspotY;
    internal long BusyReads, SubmittedFrames;
    private ID3D11VideoProcessorInputView inputView = null!;
    private ID3D11VideoProcessorOutputView outputView = null!;
    private readonly int width, height;
    private readonly uint acquireWaitMs;
    private readonly bool cursor;
    private readonly int left, top;
    
    internal GpuDesktopCapture(CaptureDisplay display, int width, int height, int fps, bool cursor)
    {
        this.width = width; this.height = height; this.cursor = cursor; acquireWaitMs = (uint)Math.Clamp(500 / fps, 1, 8);
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)display.AdapterIndex, out var adapter).CheckError();
            using (adapter)
            {
                D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out device, out context).CheckError();
                adapter.EnumOutputs((uint)display.OutputIndex, out var monitor).CheckError();
                using (monitor)
                using (var monitor1 = monitor.QueryInterface<IDXGIOutput1>())
                {
                    left = monitor.Description.DesktopCoordinates.Left; top = monitor.Description.DesktopCoordinates.Top;
                    if (monitor.Description.Rotation is not ModeRotation.Identity and not ModeRotation.Unspecified)
                        throw new NotSupportedException("Rotated displays use compatibility capture.");
                    duplication = monitor1.DuplicateOutput(device);
                }
            }
            var mode = duplication.Description.ModeDescription;
            videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive, InputWidth = mode.Width, InputHeight = mode.Height,
                OutputWidth = (uint)width, OutputHeight = (uint)height,
                InputFrameRate = new Rational((uint)fps, 1), OutputFrameRate = new Rational((uint)fps, 1), Usage = VideoUsage.PlaybackNormal
            };
            videoDevice.CreateVideoProcessorEnumerator(content, out enumerator).CheckError();
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
            input = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, mode.Width, mode.Height, 1, 1,
                BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, cursor ? ResourceOptionFlags.GdiCompatible : ResourceOptionFlags.None));
            output = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
            for (int i = 0; i < staging.Length; i++) staging[i] = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            videoDevice.CreateVideoProcessorInputView(input, enumerator, new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D }, out inputView).CheckError();
            videoDevice.CreateVideoProcessorOutputView(output, enumerator, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D }, out outputView).CheckError();
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, (int)mode.Width, (int)mode.Height));
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(0, 0, width, height));
            videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, width, height));
        }
        catch { Dispose(); throw; }
    }
    internal byte[]? Read()
    {
        // Read an older completed GPU copy instead of blocking on the frame just submitted.
        byte[]? ready = ReadCompleted();
        try
        {
            // Keep ownership between ticks so Windows coalesces desktop changes instead
            // of spending GPU time copying every game present into an unused surface.
            if (ownsDesktopFrame) { ownsDesktopFrame = false; duplication.ReleaseFrame().CheckError(); }
            var result = duplication.AcquireNextFrame(acquireWaitMs, out var info, out var resource);
            if (result.Code == unchecked((int)0x887A0027)) return ready;
            result.CheckError();
            ownsDesktopFrame = true;
            using (resource)
            {
                if (pending == staging.Length) return ready; // Bounded latency/memory under GPU saturation.
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                context.CopyResource(input, texture);
            }
            if (cursor)
            {
                var ci = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
                if (GetCursorInfo(ref ci) && (ci.Flags & 1) != 0 && ci.Cursor != IntPtr.Zero)
                {
                    if (cachedCursor != ci.Cursor && GetIconInfo(ci.Cursor, out var icon))
                    {
                        hotspotX = (int)icon.XHotspot; hotspotY = (int)icon.YHotspot; cachedCursor = ci.Cursor;
                        if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
                    }
                    using var surface = input.QueryInterface<IDXGISurface1>();
                    var dc = surface.GetDC(false);
                    try
                    {
                        DrawIconEx(dc, ci.X - left - hotspotX, ci.Y - top - hotspotY, ci.Cursor, 0, 0, 0, IntPtr.Zero, 3);
                    }
                    finally { surface.ReleaseDC(null); }
                }
            }
            videoContext.VideoProcessorBlt(processor, outputView, 0, new[] { new VideoProcessorStream { Enable = true, InputSurface = inputView } }).CheckError();
            context.CopyResource(staging[(head+pending)%staging.Length]!, output);
            pending++; SubmittedFrames++;
            context.Flush(); // Submit work; never wait for its completion here.
            return ready ?? ReadCompleted();
        }
        catch { if (ready != null) ArrayPool<byte>.Shared.Return(ready); throw; }
    }
    private byte[]? ReadCompleted()
    {
        if (pending == 0) return null;
        var texture = staging[head]!;
        var result = context.Map(texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped);
        if (result.Code == unchecked((int)0x887A000A)) { BusyReads++; return null; }
        result.CheckError();
        byte[] bytes = ArrayPool<byte>.Shared.Rent(width * height * 3 / 2);
        try
        {
            if(mapped.RowPitch == width) Marshal.Copy(mapped.DataPointer, bytes, 0, width*height*3/2);
            else for (int row = 0; row < height * 3 / 2; row++)
                Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, bytes, row * width, width);
            return bytes;
        }
        catch { ArrayPool<byte>.Shared.Return(bytes); throw; }
        finally { context.Unmap(texture, 0); head=(head+1)%staging.Length; pending--; }
    }
    public void Dispose()
    {
        if (ownsDesktopFrame) { ownsDesktopFrame = false; try { duplication?.ReleaseFrame(); } catch { } }
        outputView?.Dispose(); inputView?.Dispose(); foreach(var texture in staging) texture?.Dispose(); output?.Dispose(); input?.Dispose();
        processor?.Dispose(); enumerator?.Dispose(); videoContext?.Dispose(); videoDevice?.Dispose();
        duplication?.Dispose(); context?.Dispose(); device?.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public IntPtr Cursor; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon; public uint XHotspot, YHotspot; public IntPtr Mask, Color; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}


