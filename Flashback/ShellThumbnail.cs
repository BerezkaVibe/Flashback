using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Flashback;

internal static class ShellThumbnail
{
    private static readonly SemaphoreSlim gate = new(1);
    private static readonly Dictionary<string, BitmapSource?> cache = new();
    // Only visible list items request thumbnails. Windows supplies the same thumbnail provider as Explorer.
    internal static async Task<BitmapSource?> GetAsync(string path, Func<bool>? stillNeeded = null)
    {
        await gate.WaitAsync();
        try
        {
            if (stillNeeded != null && !stillNeeded()) return null;
            string key = path + File.GetLastWriteTimeUtc(path).Ticks;
            if (cache.TryGetValue(key, out var cached)) return cached;
            var image = await Task.Run(() => Read(path));
            if (cache.Count >= 24) { using var keys = cache.Keys.GetEnumerator(); if (keys.MoveNext()) cache.Remove(keys.Current); }
            cache[key] = image;
            return image;
        }
        catch { return null; }
        finally { gate.Release(); }
    }
    private static BitmapSource? Read(string path)
    {
        object? item = null; IntPtr bitmap = IntPtr.Zero;
        int initialized = CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item) < 0) return null;
            var factory = (IShellItemImageFactory)item;
            var size = new NativeSize { Width = 168, Height = 94 };
            // Cache first; if Explorer has not created one yet, extract once off the UI thread.
            if (factory.GetImage(size, 0x18, out bitmap) < 0 && factory.GetImage(size, 0x08, out bitmap) < 0) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); return source;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            if (initialized >= 0) CoUninitialize();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}

public partial class MainWindow
{
    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e) => await LoadThumbnailAsync((System.Windows.Controls.Image)sender);
    private async void Thumbnail_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (((System.Windows.Controls.Image)sender).IsLoaded) await LoadThumbnailAsync((System.Windows.Controls.Image)sender); }
    private async void Thumbnail_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if ((bool)e.NewValue) await LoadThumbnailAsync((System.Windows.Controls.Image)sender); }
    private static async Task LoadThumbnailAsync(System.Windows.Controls.Image image)
    {
        image.Source = null;
        if (!image.IsVisible) return;
        if (image.DataContext is not LibraryClip clip) return;
        var thumbnail = await ShellThumbnail.GetAsync(clip.Path, () => image.IsVisible && ReferenceEquals(image.DataContext, clip) && Window.GetWindow(image)?.WindowState != WindowState.Minimized);
        if (image.IsLoaded && ReferenceEquals(image.DataContext, clip)) image.Source = thumbnail;
    }
}

