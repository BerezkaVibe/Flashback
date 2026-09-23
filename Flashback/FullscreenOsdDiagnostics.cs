using System;
using System.IO.MemoryMappedFiles;
using System.Text;
namespace Flashback;
internal static class FullscreenOsdDiagnostics
{
    internal static void Run(Action<bool,string> check)
    {
        string name="Flashback.OsdTest."+Guid.NewGuid().ToString("N");
        using var memory=MemoryMappedFile.CreateNew(name,36+4608*4);
        using var view=memory.CreateViewAccessor();
        view.Write(0,0x52545353u);view.Write(4,0x00020007u);view.Write(20,4608u);view.Write(24,36u);view.Write(28,4u);
        var other=Encoding.ASCII.GetBytes("Other owner\0");view.WriteArray(36+4608+256,other,0,other.Length);
        using(var osd=new FullscreenOsd(name))
        {
            check(osd.Show(SaveFeedback.Saved,3),"Fullscreen OSD publishes to a free RTSS slot");
            byte[] text=new byte[256];view.ReadArray(36+4608*2,text,0,256);
            check(Encoding.ASCII.GetString(text).StartsWith("Flashback: clip saved"),"Fullscreen OSD carries the save result");
            view.ReadArray(36+4608+256,text,0,256);
            check(Encoding.ASCII.GetString(text).StartsWith("Other owner"),"RTSS integration preserves other applications' slots");
        }
        check(view.ReadByte(36+4608*2)==0 && view.ReadByte(36+4608*2+256)==0,"Closing fullscreen feedback releases its text and slot");
        view.Write(28,uint.MaxValue);
        using var invalid=new FullscreenOsd(name);
        check(!invalid.Show(SaveFeedback.Saved,3),"Malformed shared-memory bounds are rejected");
    }
}
