using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FFmpeg.AutoGen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using D3D9Format = Vortice.Direct3D9.Format;
using DxgiFormat = Vortice.DXGI.Format;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using IDirect3DSurface9 = Vortice.Direct3D9.IDirect3DSurface9;

namespace Flashback;

// Puts the FFmpeg player's frames on screen in WPF.
// Graphics-card path: the decoder writes frames into Direct3D 11 textures (FFmpeg's d3d11va, on the same
// device as here); the Direct3D 11 video processor turns each into a BGRA picture on the graphics card, in a
// texture shared with a Direct3D 9Ex surface that WPF shows through a D3DImage. No frame ever comes back to
// the CPU. WPF draws over it as usual, so text, pictures and shapes still sit on the preview.
// CPU path (a codec the graphics card can't decode, or no Direct3D 11 video): frames are converted with
// swscale into a WriteableBitmap.
internal sealed unsafe class FfmpegPresenter : IDisposable
{
    // FFmpeg's AVD3D11VADeviceContext (hwcontext_d3d11va.h): the device it decodes on.
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VADeviceContext
    {
        public IntPtr Device, DeviceContext, VideoDevice, VideoContext, Lock, Unlock, LockContext;
    }
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11VideoDevice? videoDevice;
    private ID3D11VideoContext1? videoContext;
    private ID3D11VideoProcessorEnumerator? enumerator;
    private ID3D11VideoProcessor? processor;
    private ID3D11Texture2D? shared;
    private ID3D11VideoProcessorOutputView? outputView;
    private ID3D11Query? done;
    // A view of each decoder frame slot shown, for the texture array frames come from now (views of an older
    // array are let go, as they'd keep all of it alive).
    private readonly Dictionary<(IntPtr, int), ID3D11VideoProcessorInputView> inputViews = new();
    private IntPtr viewArray;
    internal int CachedViews => inputViews.Count;
    private IDirect3D9Ex? d3d9;
    private IDirect3DDevice9Ex? device9;
    private IDirect3DTexture9? texture9;
    private IDirect3DSurface9? surface9;
    private D3DImage? image;
    private WriteableBitmap? bitmap;
    private SwsContext* scale;
    private AVBufferRef* hardware;
    private int width, height;
    internal ImageSource? Source => (ImageSource?)image ?? bitmap;
    // The decoder's Direct3D 11 device, or null for the CPU path.
    internal AVBufferRef* HardwareDevice => hardware;
    internal bool OnGraphicsCard => hardware != null;
    internal string Path { get; private set; } = "CPU";

    internal FfmpegPresenter(bool graphicsCard)
    {
        if (graphicsCard)
            try { CreateDevices(); Path = "graphics card (Direct3D 11 decode, zero-copy)"; }
            catch (Exception ex) { ReleaseDevices(); Path = "CPU (" + ex.Message + ")"; }
    }
    private void CreateDevices()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 }, out device, out context).CheckError();
        // The decoder works on another thread; the device is shared with it.
        using (var multithread = device!.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        videoContext = context!.QueryInterface<ID3D11VideoContext1>();
        done = device.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));
        // WPF shows Direct3D 9 surfaces; a 9Ex device opens the shared texture.
        d3d9 = D3D9.Direct3DCreate9Ex();
        var present = new Vortice.Direct3D9.PresentParameters { Windowed = true, SwapEffect = Vortice.Direct3D9.SwapEffect.Discard, DeviceWindowHandle = GetDesktopWindow(), PresentationInterval = PresentInterval.Default };
        device9 = d3d9.CreateDeviceEx(0, DeviceType.Hardware, IntPtr.Zero, CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, present);
        // FFmpeg decodes on this device (it fills in the rest).
        hardware = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        var hw = (D3D11VADeviceContext*)((AVHWDeviceContext*)hardware->data)->hwctx;
        device.AddRef(); hw->Device = device.NativePointer;
        FfmpegLibrary.Check(ffmpeg.av_hwdevice_ctx_init(hardware), "Couldn't start graphics-card decoding");
    }
    // Sets the picture's size (the video's), once per clip.
    internal void Prepare(int videoWidth, int videoHeight, double frameRate)
    {
        width = videoWidth; height = videoHeight;
        ReleasePictures();
        if (hardware != null)
        {
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive, InputFrameRate = new Rational((uint)Math.Round(frameRate), 1), InputWidth = (uint)width, InputHeight = (uint)height,
                OutputFrameRate = new Rational((uint)Math.Round(frameRate), 1), OutputWidth = (uint)width, OutputHeight = (uint)height, Usage = VideoUsage.PlaybackNormal,
            };
            enumerator = videoDevice!.CreateVideoProcessorEnumerator(content);
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
            // Recordings are BT.709 video range; the picture is full-range RGB.
            videoContext!.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.YcbcrStudioG22LeftP709);
            videoContext.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.RgbFullG22NoneP709);
            shared = device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.Shared,
            });
            outputView = videoDevice.CreateVideoProcessorOutputView(shared, enumerator, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
            IntPtr handle;
            using (var resource = shared.QueryInterface<IDXGIResource>()) handle = resource.SharedHandle;
            texture9 = device9!.CreateTexture((uint)width, (uint)height, 1, Vortice.Direct3D9.Usage.RenderTarget, D3D9Format.A8R8G8B8, Pool.Default, ref handle);
            surface9 = texture9.GetSurfaceLevel(0);
            image = new D3DImage();
            image.Lock(); image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface9.NativePointer); image.Unlock();
            // Windows can take the surface away for a moment (locking the screen, a display change); it's put
            // back when it returns, and the next frame fills it.
            var shownImage = image; var surface = surface9;
            image.IsFrontBufferAvailableChanged += (_, _) =>
            {
                if (!shownImage.IsFrontBufferAvailable || surface.NativePointer == IntPtr.Zero) return;
                shownImage.Lock(); shownImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer); shownImage.Unlock();
            };
        }
        else bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
    }
    // Shows a frame (on the UI thread).
    internal void Present(AVFrame* frame)
    {
        if (frame == null) return;
        if (image != null && frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            // data[0] is the decoder's texture array, data[1] the frame's slice of it.
            var key = ((IntPtr)frame->data[0], (int)(IntPtr)frame->data[1]);
            if (key.Item1 != viewArray || inputViews.Count > 128) { ReleaseViews(); viewArray = key.Item1; }
            if (!inputViews.TryGetValue(key, out var input))
            {
                using var texture = new ID3D11Texture2D(key.Item1); texture.AddRef();
                input = videoDevice!.CreateVideoProcessorInputView(texture, enumerator, new VideoProcessorInputViewDescription
                {
                    FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = (uint)key.Item2 },
                });
                inputViews[key] = input;
            }
            videoContext!.VideoProcessorBlt(processor!, outputView!, 0, 1, new[] { new VideoProcessorStream { Enable = true, InputSurface = input } });
            // Wait for the graphics card to finish the picture before WPF reads it (no torn frames).
            context!.End(done!); context.Flush();
            while (!context.IsDataAvailable(done!)) System.Threading.Thread.SpinWait(50);
            image.Lock(); image.AddDirtyRect(new Int32Rect(0, 0, width, height)); image.Unlock();
            return;
        }
        // CPU path, or a frame that came back from the CPU decoder.
        if (bitmap == null) { bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null); image = null; SourceChanged?.Invoke(); }
        scale = ffmpeg.sws_getCachedContext(scale, frame->width, frame->height, (AVPixelFormat)frame->format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, ffmpeg.SWS_BILINEAR, null, null, null);
        bitmap.Lock();
        var destination = new byte_ptrArray4(); destination[0] = (byte*)bitmap.BackBuffer;
        var stride = new int_array4(); stride[0] = bitmap.BackBufferStride;
        ffmpeg.sws_scale(scale, frame->data, frame->linesize, 0, frame->height, destination, stride);
        bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height)); bitmap.Unlock();
    }
    // The picture changed from the graphics-card one to the CPU one (the Image shows Source again).
    internal event Action? SourceChanged;
    private void ReleaseViews()
    {
        foreach (var view in inputViews.Values) view.Dispose();
        inputViews.Clear(); viewArray = IntPtr.Zero;
    }
    private void ReleasePictures()
    {
        ReleaseViews();
        outputView?.Dispose(); outputView = null; processor?.Dispose(); processor = null; enumerator?.Dispose(); enumerator = null;
        surface9?.Dispose(); surface9 = null; texture9?.Dispose(); texture9 = null; shared?.Dispose(); shared = null;
        image = null; bitmap = null;
    }
    private void ReleaseDevices()
    {
        if (hardware != null) { var h = hardware; ffmpeg.av_buffer_unref(&h); hardware = null; }
        done?.Dispose(); done = null; videoContext?.Dispose(); videoContext = null; videoDevice?.Dispose(); videoDevice = null;
        context?.Dispose(); context = null; device?.Dispose(); device = null; device9?.Dispose(); device9 = null; d3d9?.Dispose(); d3d9 = null;
    }
    public void Dispose()
    {
        ReleasePictures(); ReleaseDevices();
        if (scale != null) { ffmpeg.sws_freeContext(scale); scale = null; }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
}
