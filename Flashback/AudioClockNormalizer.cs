using System;
using System.Buffers.Binary;
using System.IO;

namespace Flashback;

// Raw PCM has no timestamps once it reaches FFmpeg. Keep its sample count on the
// same monotonic clock as video; async resampling downstream cannot recover lost timestamps.
internal sealed class AudioClockNormalizer(int sampleRate,int blockAlign,string format)
{
    internal const string Discontinuity = "Audio clock discontinuity requires reconnection.";
    internal long FramesWritten { get; private set; }
    internal long CorrectedFrames { get; private set; }
    internal long GapFrames { get; private set; }
    internal double ErrorMilliseconds { get; private set; }
    private bool aligned;
    private double filteredError;
    internal readonly record struct Packet(byte[]? Silence,byte[] Samples);
    internal Packet Process(byte[] bytes,double packetTime,double origin,double arrival)
    {
        int frames=bytes.Length/blockAlign;
        bool trusted=double.IsFinite(packetTime) && Math.Abs(packetTime-arrival)<=2;
        if(!trusted) packetTime=arrival-frames/(double)sampleRate;
        long target=(long)Math.Round((packetTime-origin)*sampleRate);
        long delta=target-FramesWritten;
        byte[]? silence=null;
        if(!aligned || trusted)
        {
            ErrorMilliseconds=delta*1000d/sampleRate;
            if(Math.Abs(delta)>sampleRate*2L) throw new IOException(Discontinuity);
            if(!aligned || Math.Abs(delta)>sampleRate*.1)
            {
                if(delta>0) {silence=new byte[checked((int)delta*blockAlign)];FramesWritten+=delta;GapFrames+=delta;}
                else if(delta<0)
                {
                    int skip=(int)Math.Min(-delta,frames);
                    bytes=bytes.AsSpan(skip*blockAlign).ToArray();frames-=skip;CorrectedFrames+=skip;
                }
                filteredError=0;aligned=true;
            }
            else
            {
                filteredError=filteredError*.9+delta*.1;
                // A small deadband avoids chasing timestamp rounding. Spread corrections
                // over a packet instead of dropping/duplicating a sample abruptly.
                if(Math.Abs(filteredError)>sampleRate*.002 && frames>1)
                {
                    int adjust=(int)Math.Clamp(Math.Round(filteredError),-Math.Max(1,frames/100),Math.Max(1,frames/100));
                    bytes=Resample(bytes,frames,frames+adjust);frames+=adjust;
                    CorrectedFrames+=Math.Abs(adjust);filteredError-=adjust;
                }
            }
        }
        FramesWritten+=frames;
        return new(silence,bytes);
    }
    private byte[] Resample(byte[] input,int count,int outputCount)
    {
        int sampleBytes=format=="s16le" ? 2 : format=="s24le" ? 3 : 4,channels=blockAlign/sampleBytes;
        var output=new byte[outputCount*blockAlign];
        for(int i=0;i<outputCount;i++)
        {
            double position=i*(count-1d)/(outputCount-1);int left=(int)position,right=Math.Min(left+1,count-1);double fraction=position-left;
            for(int c=0;c<channels;c++)
            {
                double a=Read(input,left*blockAlign+c*sampleBytes),b=Read(input,right*blockAlign+c*sampleBytes);
                Write(output,i*blockAlign+c*sampleBytes,a+(b-a)*fraction);
            }
        }
        return output;
    }
    private double Read(byte[] b,int i) => format switch {
        "f32le"=>BitConverter.ToSingle(b,i),"s16le"=>BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i,2)),
        "s24le"=>(b[i]|b[i+1]<<8|b[i+2]<<16)<<8>>8,_=>BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(i,4))};
    private void Write(byte[] b,int i,double value)
    {
        if(format=="f32le") {BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i,4),(float)value);return;}
        if(format=="s16le") {BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i,2),(short)Math.Clamp(Math.Round(value),short.MinValue,short.MaxValue));return;}
        if(format=="s24le") {int n=(int)Math.Clamp(Math.Round(value),-8388608,8388607);b[i]=(byte)n;b[i+1]=(byte)(n>>8);b[i+2]=(byte)(n>>16);return;}
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i,4),(int)Math.Clamp(Math.Round(value),int.MinValue,int.MaxValue));
    }
}

internal sealed class EncoderLagMonitor
{
    internal const string Message="The video encoder fell behind real time. Reconnecting the recording session.";
    private double? behindSince;
    internal bool Observe(double uptime,double lag)
    {
        if(uptime<10 || lag<=1.5) {behindSince=null;return false;}
        if(lag>1.5) behindSince ??= uptime;
        return lag>1.5 && behindSince.HasValue && uptime-behindSince.Value>=3;
    }
}
