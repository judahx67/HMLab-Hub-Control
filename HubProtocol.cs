// SPDX-License-Identifier: AGPL-3.0-or-later
// Protocol/layout port based on creations/hm-hub; see THIRD-PARTY.md and PROTOCOL.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;

namespace HMLabHub {
    public static class Wire {
        static readonly uint[] Table = MakeTable();
        static uint[] MakeTable() {
            var t = new uint[256];
            for (uint n=0; n<256; n++) { uint c=n; for(int i=0;i<8;i++) c=(c&1)!=0?0xedb88320U^(c>>1):c>>1; t[n]=c; }
            return t;
        }
        public static uint Crc(byte[] data,int offset,int length) {
            uint c=0xffffffff; for(int i=offset;i<offset+length;i++) c=Table[(c^data[i])&255]^(c>>8); return c^0xffffffff;
        }
        public static uint U32(byte[] b,int p) { return BitConverter.ToUInt32(b,p); }
        public static ushort U16(byte[] b,int p) { return BitConverter.ToUInt16(b,p); }
        public static void Put(byte[] b,int p,uint v) { Array.Copy(BitConverter.GetBytes(v),0,b,p,4); }
        public static void Put16(byte[] b,int p,int v) { Array.Copy(BitConverter.GetBytes((ushort)v),0,b,p,2); }
        public static byte[] Packet(byte cmd,byte[] payload) {
            if(payload!=null && payload.Length>251) throw new ArgumentException("Packet payload exceeds 251 bytes.");
            var b=new byte[256]; b[0]=cmd; if(payload!=null) Array.Copy(payload,0,b,1,payload.Length);
            Put(b,252,Crc(b,0,252)); return b;
        }
        public static bool Valid(byte[] b) { return b.Length==256 && U32(b,252)==Crc(b,0,252); }
        public static byte[] Album(byte[] pixels,int frames,int delay,uint capacity) {
            if(frames<1 || frames>65535 || pixels.Length!=frames*320*170*2) throw new InvalidDataException("Invalid frame data.");
            int length=checked(8192+pixels.Length);
            if(length>capacity) throw new InvalidDataException("Selected wallpapers exceed the hub's flash capacity.");
            var b=new byte[length]; Array.Copy(pixels,0,b,8192,pixels.Length);
            Header(b,0,frames,delay,8192,pixels.Length,Crc(pixels,0,pixels.Length)); return b;
        }
        public static void Header(byte[] b,int p,int frames,int delay,int offset,int length,uint crc,int width=320,int height=170) {
            Put(b,p,0xc0190001); Put16(b,p+4,width); Put16(b,p+6,height); Put16(b,p+8,frames); Put16(b,p+10,delay);
            Put(b,p+12,(uint)offset); Put(b,p+16,(uint)length); Put(b,p+20,crc); Put(b,p+24,Crc(b,p,24));
        }
        public static byte[] Combine(IList<PreparedMedia> albums,uint capacity) {
            if(albums.Count<1 || albums.Count>292) throw new InvalidDataException("Select 1 to 292 wallpapers.");
            long length=8192; foreach(var a in albums) length+=a.Pixels.Length;
            if(length>capacity || length>int.MaxValue) throw new InvalidDataException("Selected wallpapers exceed the hub's flash capacity.");
            var b=new byte[(int)length]; int offset=8192;
            for(int i=0;i<albums.Count;i++) {
                var a=albums[i];
                if(a.Width!=320 || a.Height!=170 || a.Frames<1 || (long)a.Width*a.Height*a.Frames*2!=a.Pixels.Length)
                    throw new InvalidDataException("Invalid native wallpaper dimensions or frame data.");
                Array.Copy(a.Pixels,0,b,offset,a.Pixels.Length);
                Header(b,i*28,a.Frames,a.Delay,offset,a.Pixels.Length,Crc(a.Pixels,0,a.Pixels.Length),a.Width,a.Height); offset+=a.Pixels.Length;
            }
            return b;
        }
        public static void ValidateAlbumBackup(byte[] data) {
            if(data.Length<8192) throw new InvalidDataException("Backup is missing the album header area.");
            for(int i=0;i<292;i++) {
                int p=i*28; uint magic=U32(data,p);
                if(magic==0 || magic==0xffffffff) return;
                if(magic!=0xc0190001) throw new InvalidDataException("Backup contains an unknown album header; nothing was replaced.");
                if(U32(data,p+24)!=Crc(data,p,24)) throw new InvalidDataException("Album backup header CRC failed.");
                int width=U16(data,p+4),height=U16(data,p+6),frames=U16(data,p+8);
                uint offset=U32(data,p+12),length=U32(data,p+16);
                if(width<1 || height<1 || frames<1 || (long)width*height*frames*2!=length || offset<8192 || (long)offset+length>data.Length)
                    throw new InvalidDataException("Album backup references missing or invalid frame data.");
                if(U32(data,p+20)!=Crc(data,(int)offset,(int)length)) throw new InvalidDataException("Album backup data CRC failed.");
            }
        }
    }
    public static class ScreenOrientation {
        // Vendor screen_dir values are ordered 0, 180, 90, 270 degrees.
        public static bool IsPortrait(int value) {
            if(value<0 || value>3) throw new InvalidDataException("Unsupported hub orientation: "+value);
            return value>=2;
        }
    }
    public sealed class HubInfo {
        public uint Hardware,Version,Flash;
        public int Capacity { get { return Flash<8192?0:(int)((Flash-8192)/108800); } }
        public string Firmware { get { return string.Format("{0}.{1}.{2}",(Version>>16)&255,(Version>>8)&255,Version&255); } }
        public override string ToString() { return string.Format("HW {0:X8} | firmware {1} | {2:0.##} MiB | {3} frames",Hardware,Firmware,Flash/1048576.0,Capacity); }
    }
    public interface IHubLink : IDisposable {
        void Write(byte[] packet);
        byte[] Read(int timeout,CancellationToken token);
    }
    internal sealed class RawSerialLink : IHubLink {
        readonly SerialPort port;
        readonly List<byte> pending=new List<byte>();
        public RawSerialLink(string name) {
            port=new SerialPort(name,115200,Parity.None,8,StopBits.One);
            // Match Chromium's Windows serial defaults: both DTR and RTS asserted.
            port.ReadTimeout=100; port.WriteTimeout=2000; port.ReadBufferSize=32768; port.DtrEnable=true; port.RtsEnable=true;
            try { port.Open(); port.DiscardInBuffer(); Thread.Sleep(150); } catch { port.Dispose(); throw; }
        }
        public void Write(byte[] b) { port.Write(b,0,b.Length); }
        public byte[] Read(int timeout,CancellationToken token) {
            var timer=Stopwatch.StartNew(); var bytes=new byte[4096]; int received=0,rejected=0;
            while(timer.ElapsedMilliseconds<timeout) {
                token.ThrowIfCancellationRequested();
                while(pending.Count>=256) {
                    var candidate=pending.GetRange(0,256).ToArray();
                    if(Wire.Valid(candidate)) { pending.RemoveRange(0,256); return candidate; }
                    pending.RemoveAt(0); rejected++; // Resynchronize if opening the port caught a partial telemetry packet.
                }
                try { int n=port.Read(bytes,0,Math.Min(bytes.Length,Math.Max(1,port.BytesToRead))); received+=n; for(int i=0;i<n;i++) pending.Add(bytes[i]); }
                catch(TimeoutException) { }
            }
            throw new TimeoutException(string.Format("No valid hub response ({0} bytes received, {1} rejected packet alignments). Close its web driver and check the USB connection.",received,rejected));
        }
        public void Dispose() { port.Dispose(); }
    }
    public sealed class HubSession : IDisposable {
        readonly IHubLink link;
        public HubInfo Info { get; private set; }
        public HubSession(IHubLink link,CancellationToken token) {
            this.link=link;
            try {
                link.Write(Wire.Packet(1,null)); var timer=Stopwatch.StartNew();
                while(timer.ElapsedMilliseconds<5000) {
                    byte[] p;
                    try { p=link.Read(1500,token); }
                    catch(TimeoutException) { if(timer.ElapsedMilliseconds>=4500) throw; link.Write(Wire.Packet(1,null)); continue; }
                    if(p[0]!=1) continue;
                    Info=new HubInfo { Hardware=Wire.U32(p,1),Version=Wire.U32(p,5),Flash=Wire.U32(p,9) };
                    if(Info.Capacity<1 || Info.Flash>256*1024*1024) throw new InvalidDataException("Unrecognized flash capacity in hub response.");
                    return;
                }
                throw new TimeoutException("No HMLab handshake response.");
            } catch { link.Dispose(); throw; }
        }
        public void UploadWallpaper(byte[] album,CancellationToken token,Action<int,string> progress,bool verify=true) {
            Upload(album,token,progress,verify);
            var config=ReadConfig(token);
            if(config[2]!=2) { config[2]=2; WriteConfigVerified(config,token); }
        }
        public void Upload(byte[] album,CancellationToken token,Action<int,string> progress,bool verify=true) {
            if(album.Length>Info.Flash || album.Length<8192) throw new InvalidDataException("Invalid album size.");
            token.ThrowIfCancellationRequested(); var start=new byte[5]; start[0]=1; Wire.Put(start,1,(uint)album.Length);
            link.Write(Wire.Packet(8,start)); progress(0,"Erasing wallpaper storage...");
            var overall=Stopwatch.StartNew(); long lastProgress=0; int highWater=0;
            while(true) {
                token.ThrowIfCancellationRequested();
                if(overall.ElapsedMilliseconds-lastProgress>90000) throw new TimeoutException("Wallpaper transfer stalled.");
                var p=link.Read(65000,token); if(p[0]!=8 || p[1]!=2) continue;
                uint offset=Wire.U32(p,2); int length=Wire.U16(p,6);
                if(length<1 || length>244 || offset>(uint)album.Length || (long)offset+length>album.Length)
                    throw new InvalidDataException("Hub requested an invalid wallpaper chunk.");
                var data=new byte[7+length]; data[0]=2; Wire.Put(data,1,offset); Wire.Put16(data,5,length);
                Array.Copy(album,(int)offset,data,7,length); link.Write(Wire.Packet(8,data));
                highWater=Math.Max(highWater,(int)offset+length); lastProgress=overall.ElapsedMilliseconds;
                progress((int)((long)highWater*50/album.Length),"Uploading wallpaper data...");
                if(highWater==album.Length) break;
            }
            // Vendor 1.3.7 READ BACK is 8/4; 8/3 clears media. Do not use the CLI's readback opcode.
            if(verify) Verify(album,token,progress);
            else progress(100,"Upload sent; readback verification skipped.");
        }
        public byte[] ReadConfig(CancellationToken token) {
            var serial=link as SerialLink; if(serial!=null) serial.DiscardInput(); // Drop old queued replies; status reception remains active.
            link.Write(Wire.Packet(3,new byte[]{1}));
            var parts=new Dictionary<int,byte[]>(); int count=0; var watch=Stopwatch.StartNew();
            while(watch.ElapsedMilliseconds<5000) {
                var p=link.Read(2000,token); if(p[0]!=3 || p[1]!=2) continue;
                int total=p[2],index=p[3],length=Wire.U16(p,4);
                if(total<1 || total>8 || index>=total || length<1 || length>246 || (count!=0 && count!=total)) throw new InvalidDataException("Invalid hub configuration chunk.");
                count=total; var bytes=new byte[length]; Array.Copy(p,6,bytes,0,length); parts[index]=bytes;
                if(parts.Count!=count) continue;
                using(var data=new MemoryStream()) {
                    for(int i=0;i<count;i++) data.Write(parts[i],0,parts[i].Length);
                    var all=data.ToArray();
                    if(all.Length!=28 || Wire.U32(all,24)!=Wire.Crc(all,0,24)) throw new InvalidDataException("Hub configuration length/CRC is unsupported.");
                    var raw=new byte[24]; Array.Copy(all,raw,24); return raw;
                }
            }
            throw new TimeoutException("No hub configuration response.");
        }
        public void SetOrientation(byte orientation,CancellationToken token) {
            ScreenOrientation.IsPortrait(orientation);
            var raw=ReadConfig(token);
            if(raw[3]==orientation) return;
            raw[3]=orientation; WriteConfigVerified(raw,token);
        }
        public void WriteConfigVerified(byte[] raw,CancellationToken token) {
            if(raw.Length!=24) throw new InvalidDataException("Expected exactly 24 configuration bytes.");
            token.ThrowIfCancellationRequested();
            var payload=new byte[35]; payload[0]=2; Wire.Put16(payload,1,1); Wire.Put16(payload,3,0); Wire.Put16(payload,5,28);
            Array.Copy(raw,0,payload,7,24); Wire.Put(payload,31,Wire.Crc(raw,0,24)); link.Write(Wire.Packet(3,payload));
            var actual=ReadConfig(token);
            for(int i=0;i<24;i++) if(actual[i]!=raw[i]) throw new InvalidDataException("Configuration readback differs from the requested settings.");
        }
        public void Verify(byte[] expected,CancellationToken token,Action<int,string> progress) {
            var received=new bool[expected.Length]; int count=0;
            link.Write(Wire.Packet(8,new byte[]{4})); progress(50,"Reading back to verify upload...");
            var total=Stopwatch.StartNew(); long lastData=0;
            while(true) {
                token.ThrowIfCancellationRequested();
                if(total.ElapsedMilliseconds-lastData>15000) throw new TimeoutException("Readback verification stalled; upload is unverified.");
                var p=link.Read(3000,token); if(p[0]!=8) continue;
                if(p[1]==4) {
                    if(count!=expected.Length) throw new InvalidDataException("Readback was incomplete; upload is unverified.");
                    progress(100,"Uploaded and verified against the hub's flash."); return;
                }
                if(p[1]!=3) continue;
                uint offset=Wire.U32(p,2); int length=Wire.U16(p,6);
                if(length<1 || length>244 || (long)offset+length>Info.Flash) throw new InvalidDataException("Invalid readback chunk.");
                for(int i=0;i<length;i++) {
                    long pos=(long)offset+i; if(pos>=expected.Length) break;
                    if(p[8+i]!=expected[(int)pos]) throw new InvalidDataException("Wallpaper readback differs from uploaded data; retry the upload.");
                    if(!received[(int)pos]) { received[(int)pos]=true; count++; }
                }
                lastData=total.ElapsedMilliseconds;
                progress(50+(int)((long)count*49/expected.Length),"Verifying wallpaper data...");
            }
        }
        public byte[] ReadFlash(CancellationToken token,Action<int,string> progress) {
            var data=new byte[(int)Info.Flash]; var seen=new bool[data.Length]; int count=0,highest=0;
            link.Write(Wire.Packet(8,new byte[]{4}));
            var total=Stopwatch.StartNew(); long lastData=0;
            while(true) {
                token.ThrowIfCancellationRequested();
                if(total.ElapsedMilliseconds-lastData>15000) throw new TimeoutException("Wallpaper backup stalled; nothing was replaced.");
                var p=link.Read(3000,token); if(p[0]!=8) continue;
                if(p[1]==4) {
                    // Firmware sends the used album prefix, not necessarily all physical flash.
                    if(count!=highest || highest<8192) throw new InvalidDataException("Wallpaper backup has missing bytes; nothing was replaced.");
                    var prefix=new byte[highest]; Array.Copy(data,prefix,highest); Wire.ValidateAlbumBackup(prefix); return prefix;
                }
                if(p[1]!=3) continue;
                uint offset=Wire.U32(p,2); int length=Wire.U16(p,6);
                if(length<1 || length>244 || (long)offset+length>Info.Flash) throw new InvalidDataException("Invalid backup chunk.");
                for(int i=0;i<length;i++) { int pos=(int)offset+i; data[pos]=p[8+i]; if(!seen[pos]) { seen[pos]=true; count++; } }
                highest=Math.Max(highest,(int)offset+length);
                lastData=total.ElapsedMilliseconds; progress((int)((long)count*100/data.Length),"Backing up the current wallpaper album...");
            }
        }
        public void Dispose() { link.Dispose(); }
    }
}
