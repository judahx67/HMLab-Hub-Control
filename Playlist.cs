// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;

namespace HMLabHub {
    public sealed class PlaybackPacer : IDisposable {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        readonly bool precise; readonly double period; readonly Stopwatch clock=Stopwatch.StartNew(); double due;
        public PlaybackPacer(int fps) { period=1000.0/fps; precise=timeBeginPeriod(1)==0; }
        public bool Wait(CancellationToken token,AutoResetEvent next=null) {
            due+=period; double now=clock.Elapsed.TotalMilliseconds; if(due<now-period) due=now;
            int ms=(int)Math.Ceiling(due-now);
            if(ms>0 && next!=null) { int result=WaitHandle.WaitAny(new WaitHandle[]{token.WaitHandle,next},ms); if(result==0) token.ThrowIfCancellationRequested(); return result==1; }
            if(ms>0 && token.WaitHandle.WaitOne(ms)) token.ThrowIfCancellationRequested(); return false;
        }
        public void Dispose() { if(precise) timeEndPeriod(1); }
    }
    public enum PlaylistOrder { Filename,FilenameDescending,Modified,ModifiedDescending,Shuffle }
    public sealed class PlaylistOptions {
        public string Folder,Port; public PlaylistOrder Order; public double HoldSeconds=30; public int Fps=30; public bool Fill;
    }
    public static class PlaylistFiles {
        public static string[] Scan(string folder,PlaylistOrder order) {
            var files=Directory.GetFiles(folder,"*",SearchOption.TopDirectoryOnly).Where(p=>MediaLibrary.Supports(p) && !Path.GetFileName(p).StartsWith(".gif70-",StringComparison.OrdinalIgnoreCase));
            switch(order) {
                case PlaylistOrder.Modified: return files.OrderBy(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase).ToArray();
                case PlaylistOrder.ModifiedDescending: return files.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase).ToArray();
                case PlaylistOrder.FilenameDescending: return files.OrderByDescending(Path.GetFileName,StringComparer.OrdinalIgnoreCase).ToArray();
                default: return files.OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }
    public sealed class PlaylistDeck {
        readonly HashSet<string> played=new HashSet<string>(StringComparer.OrdinalIgnoreCase); readonly Random random;
        string last;
        public PlaylistDeck(int seed) { random=new Random(seed); }
        public string Next(string[] paths,PlaylistOrder order) {
            if(paths.Length==0) throw new InvalidDataException("The playlist folder has no supported images or GIFs.");
            string chosen;
            if(order==PlaylistOrder.Shuffle) {
                var remaining=paths.Where(p=>!played.Contains(p)).ToArray();
                if(remaining.Length==0) { played.Clear(); remaining=paths.Where(p=>paths.Length==1 || !string.Equals(p,last,StringComparison.OrdinalIgnoreCase)).ToArray(); }
                chosen=remaining[random.Next(remaining.Length)]; played.Add(chosen);
            } else {
                int index=Array.FindIndex(paths,p=>string.Equals(p,last,StringComparison.OrdinalIgnoreCase)); chosen=paths[(index+1)%paths.Length];
            }
            last=chosen; return chosen;
        }
    }
    public sealed class LiveMedia {
        public readonly List<byte[]> Frames=new List<byte[]>();
        public long[] Ends; public long Duration;
        public int IndexAt(long elapsed) {
            long pos=elapsed%Duration; int low=0,high=Ends.Length-1;
            while(low<high) { int mid=(low+high)/2; if(pos<Ends[mid]) high=mid; else low=mid+1; } return low;
        }
        public static LiveMedia Load(string path,bool fill,CancellationToken token) {
            using(var stream=new MemoryStream(File.ReadAllBytes(path))) using(var image=Image.FromStream(stream)) {
                int count=Wallpaper.FrameCount(image); var result=new LiveMedia { Ends=new long[count] }; byte[] delays=null;
                try { delays=image.GetPropertyItem(0x5100).Value; } catch(ArgumentException) { }
                for(int i=0;i<count;i++) {
                    token.ThrowIfCancellationRequested(); if(count>1) image.SelectActiveFrame(FrameDimension.Time,i);
                    using(var bitmap=Wallpaper.RenderAt(image,fill,32,17)) {
                        var data=new byte[32*17*3];
                        for(int y=0;y<17;y++) for(int x=0;x<32;x++) { Color c=bitmap.GetPixel(x,y); int p=(y*32+x)*3; data[p]=c.R; data[p+1]=c.G; data[p+2]=c.B; }
                        result.Frames.Add(data);
                    }
                    int delay=count==1?1000:100;
                    if(delays!=null && delays.Length>=(i+1)*4) delay=(int)Math.Min(655350,Math.Max(20,BitConverter.ToUInt32(delays,i*4)*10L));
                    result.Duration+=delay; result.Ends[i]=result.Duration;
                }
                return result;
            }
        }
        public static Bitmap Preview(byte[] rgb) {
            var bitmap=new Bitmap(32,17,PixelFormat.Format24bppRgb);
            var bits=bitmap.LockBits(new Rectangle(0,0,32,17),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
            try {
                var row=new byte[96]; for(int y=0;y<17;y++) { for(int x=0;x<32;x++) { int p=(y*32+x)*3; row[x*3]=rgb[p+2]; row[x*3+1]=rgb[p+1]; row[x*3+2]=rgb[p]; } Marshal.Copy(row,0,IntPtr.Add(bits.Scan0,y*bits.Stride),96); }
            } finally { bitmap.UnlockBits(bits); } return bitmap;
        }
    }
    public sealed class LiveStatus {
        public string Path; public int FrameCount,Sent; public double Fps,Remaining,WriteMs; public byte[] Rgb;
    }
    public sealed class PlaylistPlayer {
        public void Run(PlaylistOptions options,ILiveSink sink,CancellationToken token,AutoResetEvent next,Action<LiveStatus> update,int maxItems=0) {
            if(options.Fps<1 || options.Fps>60 || options.HoldSeconds<=0) throw new ArgumentException("Choose a positive file duration and 1-60 FPS.");
            var deck=new PlaylistDeck(Environment.TickCount); int completed=0,bad=0;
            while(true) {
                token.ThrowIfCancellationRequested();
                string[] paths=PlaylistFiles.Scan(options.Folder,options.Order); string path=deck.Next(paths,options.Order); LiveMedia media;
                try { media=LiveMedia.Load(path,options.Fill,token); bad=0; }
                catch(OperationCanceledException) { throw; }
                catch(Exception ex) { if(++bad>=paths.Length) throw new InvalidDataException("No playable files in this folder: "+ex.Message,ex); continue; }
                var clock=Stopwatch.StartNew(); int sent=0; long reported=-1000; double writeTotal=0;
                using(var pacing=new PlaybackPacer(options.Fps)) {
                while(clock.Elapsed.TotalSeconds<options.HoldSeconds) {
                    token.ThrowIfCancellationRequested(); if(next.WaitOne(0)) break;
                    long started=clock.ElapsedMilliseconds; int index=media.IndexAt(started); var rgb=media.Frames[index];
                    var write=Stopwatch.StartNew(); sink.Send(rgb,token); writeTotal+=write.Elapsed.TotalMilliseconds; sent++;
                    if(clock.ElapsedMilliseconds-reported>=200) {
                        reported=clock.ElapsedMilliseconds;
                        update(new LiveStatus { Path=path,FrameCount=media.Frames.Count,Sent=sent,Fps=sent/Math.Max(0.001,clock.Elapsed.TotalSeconds),
                            Remaining=Math.Max(0,options.HoldSeconds-clock.Elapsed.TotalSeconds),WriteMs=writeTotal/sent,Rgb=rgb });
                    }
                    if(pacing.Wait(token,next)) break;
                }
                }
                if(maxItems>0 && ++completed>=maxItems) return;
            }
        }
    }
    public static class LiveDisplayMode {
        public static void Run(HubSession hub,string port,string journal,CancellationToken token,Action play) {
            if(File.Exists(journal)) throw new IOException("A previous live display session needs restoration. Click Restore display first.");
            byte[] original=hub.ReadConfig(token); var doc=new XmlDocument(); var root=doc.CreateElement("LiveSession"); doc.AppendChild(root);
            root.SetAttribute("Port",port); root.SetAttribute("Hardware",hub.Info.Hardware.ToString()); root.SetAttribute("Original",Convert.ToBase64String(original));
            Directory.CreateDirectory(Path.GetDirectoryName(journal)); doc.Save(journal);
            try {
                if(original[2]!=3) { var live=(byte[])original.Clone(); live[2]=3; hub.WriteConfigVerified(live,token); }
                token.ThrowIfCancellationRequested(); play();
            } finally {
                // Restore only the page field, preserving any other settings changed during playback.
                byte[] current=hub.ReadConfig(CancellationToken.None);
                if(current[2]!=original[2]) { current[2]=original[2]; hub.WriteConfigVerified(current,CancellationToken.None); }
                File.Delete(journal);
            }
        }
        public static void Restore(string journal,string port,CancellationToken token) {
            var doc=new XmlDocument(); doc.Load(journal); byte[] original=Convert.FromBase64String(doc.DocumentElement.GetAttribute("Original"));
            if(original.Length!=24) throw new InvalidDataException("Invalid saved display settings.");
            using(var hub=new HubSession(new SerialLink(port),token)) {
                if(hub.Info.Hardware.ToString()!=doc.DocumentElement.GetAttribute("Hardware")) throw new InvalidDataException("Saved display settings belong to a different hub model.");
                var current=hub.ReadConfig(token); if(current[2]!=original[2]) { current[2]=original[2]; hub.WriteConfigVerified(current,token); }
            }
            File.Delete(journal);
        }
    }
}
