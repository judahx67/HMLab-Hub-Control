// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HMLabHub {
    public sealed class SimulatedHub : IHubLink {
        public const int FlashSize=8192+108800*3;
        public byte[] Flash=new byte[FlashSize];
        public bool Erased,ReadBack,Disposed,Corrupt,OmitFirstChunk,RejectConfigAfterUpload;
        public int ReadLength;
        public int Uploads,Readbacks;
        public byte[] Config=new byte[]{1,1,2,0,30,0,0,0,0,0,2,50,1,9,8,7,6,5,0,1,255,0,30,1};
        int mode,offset,total;
        public void Write(byte[] p) {
            if(!Wire.Valid(p)) throw new Exception("Bad host CRC.");
            if(p[0]==1) { mode=1; return; }
            if(p[0]==3) {
                if(p[1]==1) { mode=RejectConfigAfterUpload && Uploads>0?5:4; return; }
                if(p[1]!=2 || Wire.U16(p,2)!=1 || Wire.U16(p,4)!=0 || Wire.U16(p,6)!=28 || Wire.U32(p,32)!=Wire.Crc(p,8,24)) throw new Exception("Incorrect config write framing/CRC.");
                Array.Copy(p,8,Config,0,24); return;
            }
            if(p[0]!=8) throw new Exception("Unexpected host command.");
            if(p[1]==3) throw new Exception("REGRESSION: host sent the vendor CLEAR opcode.");
            if(p[1]==1) { total=(int)Wire.U32(p,2); offset=0; Erased=true; Uploads++; mode=2; Array.Clear(Flash,0,Flash.Length); return; }
            if(p[1]==2) {
                int at=(int)Wire.U32(p,2),n=Wire.U16(p,6);
                if(at!=offset || n!=Math.Min(240,total-offset)) throw new Exception("Bad upload response offset/length.");
                Array.Copy(p,8,Flash,at,n); offset+=n; return;
            }
            if(p[1]==4) { ReadBack=true; Readbacks++; mode=3; offset=OmitFirstChunk?240:0; return; }
            throw new Exception("Unexpected flash subcommand.");
        }
        public byte[] Read(int timeout,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if(mode==5) throw new TimeoutException("No hub command response. Check the USB connection and close other controllers.");
            if(mode==1) {
                mode=0; var info=new byte[12]; Wire.Put(info,0,0x12345678); Wire.Put(info,4,0x010307); Wire.Put(info,8,FlashSize); return Wire.Packet(1,info);
            }
            if(mode==4) { mode=0; var raw=new byte[33]; raw[0]=2; raw[1]=1; raw[2]=0; Wire.Put16(raw,3,28); Array.Copy(Config,0,raw,5,24); Wire.Put(raw,29,Wire.Crc(Config,0,24)); return Wire.Packet(3,raw); }
            if(mode==2 && offset<total) {
                var request=new byte[7]; request[0]=2; Wire.Put(request,1,(uint)offset); Wire.Put16(request,5,Math.Min(240,total-offset)); return Wire.Packet(8,request);
            }
            int readEnd=ReadLength>0?ReadLength:Flash.Length;
            if(mode==3 && offset<readEnd) {
                int n=Math.Min(240,readEnd-offset); var data=new byte[7+n]; data[0]=3; Wire.Put(data,1,(uint)offset); Wire.Put16(data,5,n);
                Array.Copy(Flash,offset,data,7,n); if(Corrupt && offset==0) data[7]^=1; offset+=n; return Wire.Packet(8,data);
            }
            if(mode==3) { mode=0; return Wire.Packet(8,new byte[]{4}); }
            throw new TimeoutException("No simulated response.");
        }
        public void Dispose() { Disposed=true; }
    }
    public static class SelfCheck {
        static void Assert(bool ok,string message) { if(!ok) throw new Exception(message); }
        public static void Run(string directory) {
            if(string.IsNullOrEmpty(directory)) directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".checks");
            Directory.CreateDirectory(directory); var log=new List<string>();
            string gif=CreateGifFixture(directory);
            var telemetry=new TelemetryTestLink();
            using(var serial=new SerialLink(telemetry)) {
                Assert(SpinWait.SpinUntil(()=>telemetry.Reads>=20,1000),"REGRESSION: serial telemetry was left unread while the caller was idle; this freezes hardware playback.");
                var reply=Wire.Packet(3,new byte[]{2,1,0,28,0}); serial.Write(reply);
                Assert(serial.Read(1000,CancellationToken.None).SequenceEqual(reply),"Background telemetry displaced a command response.");
                Assert(serial.TelemetryPackets>=20 && serial.PendingReplies==0,"Status packets accumulated as queued command replies.");
                using(var cancelledRead=new CancellationTokenSource(30)) {
                    bool readStopped=false; var waiting=Stopwatch.StartNew();
                    try { serial.Read(3000,cancelledRead.Token); } catch(OperationCanceledException) { readStopped=true; }
                    Assert(readStopped && waiting.ElapsedMilliseconds<1000,"Cancellation did not wake a waiting command reader.");
                }
                telemetry.Fail=true; bool receiveFailed=false;
                try { serial.Read(1000,CancellationToken.None); } catch(IOException) { receiveFailed=true; }
                Assert(receiveFailed,"Background receive failure was hidden from the command reader.");
            }
            Assert(telemetry.Disposed,"Serial reader did not close its source.");
            log.Add("PASS: serial receiver consumes status while idle, retains command replies, bounds status memory, wakes on cancellation, and propagates receive failures.");
            var check=Encoding.ASCII.GetBytes("123456789"); Assert(Wire.Crc(check,0,check.Length)==0xcbf43926,"CRC32 known vector failed.");
            var packet=Wire.Packet(1,null); Assert(Wire.Valid(packet),"Handshake packet failed."); packet[10]^=1; Assert(!Wire.Valid(packet),"Corrupt packet accepted.");
            log.Add("PASS: CRC32 known vector, packet layout, corrupt-packet rejection.");
            using(var image=new Bitmap(320,170)) {
                using(var g=Graphics.FromImage(image)) g.Clear(Color.Red);
                using(var rendered=Wallpaper.Render(image,false)) { var red=Wallpaper.Encode565(rendered); Assert(red[0]==0xf8 && red[1]==0,"RGB565 byte order failed."); }
                using(var g=Graphics.FromImage(image)) g.Clear(Color.Lime);
                using(var rendered=Wallpaper.Render(image,false)) { var green=Wallpaper.Encode565(rendered); Assert(green[0]==0x07 && green[1]==0xe0,"RGB565 green failed."); }
            }
            log.Add("PASS: native panel rendering and big-endian RGB565 pixels.");
            CheckSidewaysPacking(directory,gif,log);
            var orientationHub=new SimulatedHub(); var originalOrientationConfig=(byte[])orientationHub.Config.Clone();
            using(var hub=new HubSession(orientationHub,CancellationToken.None)) {
                foreach(byte value in new byte[]{1,2,3,0}) {
                    hub.SetOrientation(value,CancellationToken.None);
                    Assert(orientationHub.Config[3]==value && originalOrientationConfig.Where((v,i)=>i!=3).SequenceEqual(orientationHub.Config.Where((v,i)=>i!=3)),"Orientation changed another config byte.");
                    Assert(ScreenOrientation.IsPortrait(value)==(value>=2),"Vendor orientation mapping failed.");
                }
                bool orientationRejected=false; try { hub.SetOrientation(4,CancellationToken.None); } catch(InvalidDataException) { orientationRejected=true; }
                Assert(orientationRejected && !orientationHub.Erased && !orientationHub.ReadBack,"Orientation setting accepted an unknown value or touched wallpaper flash.");
            }
            log.Add("PASS: all four screen orientations read/modify/write only screen_dir, verify settings, reject unknown values and leave wallpaper flash untouched.");
            var pixels=new byte[108800*2]; for(int i=0;i<pixels.Length;i++) pixels[i]=(byte)(i*17);
            var album=Wire.Album(pixels,2,50,SimulatedHub.FlashSize);
            Assert(Wire.U32(album,0)==0xc0190001 && Wire.U16(album,4)==320 && Wire.U16(album,6)==170,"Header dimensions failed.");
            Assert(Wire.U32(album,24)==Wire.Crc(album,0,24) && Wire.U32(album,20)==Wire.Crc(pixels,0,pixels.Length),"Header/data CRC failed.");
            var fake=new SimulatedHub(); int highest=0;
            using(var hub=new HubSession(fake,CancellationToken.None)) {
                Assert(hub.Info.Capacity==3 && hub.Info.Firmware=="1.3.7","Handshake parsing failed.");
                hub.Upload(album,CancellationToken.None,(p,s)=>highest=Math.Max(highest,p));
            }
            Assert(fake.ReadBack && fake.Disposed && highest==100,"Upload did not verify/dispose correctly.");
            for(int i=0;i<album.Length;i++) Assert(album[i]==fake.Flash[i],"Flash content mismatch.");
            log.Add("PASS: simulated device handshake, device-requested upload, vendor 8/4 readback, byte-for-byte verification.");
            var stored=new SimulatedHub(); stored.Config[2]=3; stored.Config[3]=2; var storedConfig=(byte[])stored.Config.Clone();
            using(var hub=new HubSession(stored,CancellationToken.None)) hub.UploadWallpaper(album,CancellationToken.None,(p,s)=>{});
            Assert(stored.Config[2]==2,"Stored upload verified successfully but left the hub on another display page.");
            Assert(storedConfig.Where((v,i)=>i!=2).SequenceEqual(stored.Config.Where((v,i)=>i!=2)),"Stored upload changed another setting while selecting wallpaper display.");
            var failedStored=new SimulatedHub { Corrupt=true }; failedStored.Config[2]=3;
            using(var hub=new HubSession(failedStored,CancellationToken.None)) {
                bool failed=false; try { hub.UploadWallpaper(album,CancellationToken.None,(p,s)=>{}); } catch(InvalidDataException) { failed=true; }
                Assert(failed && failedStored.Config[2]==3,"Failed upload changed the display page.");
            }
            log.Add("PASS: successful stored upload selects wallpaper display and preserves all other settings; failed verification leaves the page unchanged.");
            foreach(bool verifyStored in new[]{true,false}) {
                var completed=new SimulatedHub { RejectConfigAfterUpload=true }; bool completionFailed=false;
                using(var hub=new HubSession(completed,CancellationToken.None)) {
                    try { Assert(hub.UploadWallpaper(album,CancellationToken.None,(p,s)=>{},verifyStored)==null,"Already-selected wallpaper display produced a warning."); }
                    catch(TimeoutException) { completionFailed=true; }
                }
                Assert(completed.Uploads==1 && completed.ReadBack==verifyStored && album.SequenceEqual(completed.Flash.Take(album.Length)),"Completion regression did not finish the requested transfer/verification.");
                Assert(!completionFailed,"REGRESSION: a completed wallpaper upload is reported as failed because of a redundant settings request.");
            }
            log.Add("PASS: completed stored uploads do not request settings again when wallpaper display is already selected, with verification enabled or skipped.");
            var displayTimeout=new SimulatedHub { RejectConfigAfterUpload=true }; displayTimeout.Config[2]=3;
            string displayWarning;
            using(var hub=new HubSession(displayTimeout,CancellationToken.None)) displayWarning=hub.UploadWallpaper(album,CancellationToken.None,(p,s)=>{});
            Assert(displayTimeout.ReadBack && album.SequenceEqual(displayTimeout.Flash.Take(album.Length)) && displayWarning!=null && displayWarning.StartsWith("Could not confirm wallpaper display:"),"A display-selection timeout hid completed upload/verification or claimed display selection succeeded.");
            log.Add("PASS: a timeout selecting the display page is returned separately after successful transfer/verification.");
            var unverified=new SimulatedHub(); string uploadResult=null; int uploadPercent=0;
            using(var hub=new HubSession(unverified,CancellationToken.None)) hub.Upload(album,CancellationToken.None,(p,s)=>{ uploadPercent=p; uploadResult=s; },false);
            Assert(unverified.Uploads==1 && !unverified.ReadBack && uploadPercent==100 && uploadResult.Contains("verification skipped"),"Skipping verification still read flash or claimed verified success.");
            Assert(album.SequenceEqual(unverified.Flash.Take(album.Length)),"Skipping verification altered uploaded bytes.");
            log.Add("PASS: verification can be skipped explicitly without readback; result is labeled unverified and upload bytes are unchanged.");
            fake=new SimulatedHub(); Array.Copy(album,fake.Flash,album.Length);
            using(var hub=new HubSession(fake,CancellationToken.None)) {
                byte[] backup=hub.ReadFlash(CancellationToken.None,(p,s)=>{});
                Assert(!fake.Erased && backup.Length==fake.Flash.Length,"Read-only backup altered the device.");
                for(int i=0;i<backup.Length;i++) Assert(backup[i]==fake.Flash[i],"Backup did not preserve device bytes.");
            }
            log.Add("PASS: read-only backup preserves and CRC-validates the complete album without erasing.");
            fake=new SimulatedHub { ReadLength=album.Length }; Array.Copy(album,fake.Flash,album.Length);
            using(var hub=new HubSession(fake,CancellationToken.None)) {
                byte[] backup=hub.ReadFlash(CancellationToken.None,(p,s)=>{});
                Assert(backup.Length==album.Length && !fake.Erased,"Firmware used-prefix backup failed.");
                for(int i=0;i<backup.Length;i++) Assert(backup[i]==album[i],"Used-prefix backup differs from the album.");
            }
            var badBackup=(byte[])album.Clone(); badBackup[8192]^=1; bool badRejected=false;
            try { Wire.ValidateAlbumBackup(badBackup); } catch(InvalidDataException) { badRejected=true; }
            Assert(badRejected,"Backup corruption was accepted.");
            log.Add("PASS: used-prefix readback is accepted; album CRC corruption is rejected before erase.");
            bool rejected=false; fake=new SimulatedHub { Corrupt=true };
            try { using(var hub=new HubSession(fake,CancellationToken.None)) hub.Upload(album,CancellationToken.None,(p,s)=>{}); }
            catch(InvalidDataException) { rejected=true; }
            Assert(rejected,"Corrupt readback was reported as success.");
            fake=new SimulatedHub { OmitFirstChunk=true }; rejected=false;
            try { using(var hub=new HubSession(fake,CancellationToken.None)) hub.Upload(album,CancellationToken.None,(p,s)=>{}); }
            catch(InvalidDataException) { rejected=true; }
            Assert(rejected,"Incomplete readback was reported as success.");
            using(var cancel=new CancellationTokenSource()) {
                fake=new SimulatedHub(); bool stopped=false;
                using(var hub=new HubSession(fake,CancellationToken.None)) {
                    cancel.Cancel(); try { hub.Upload(album,cancel.Token,(p,s)=>{}); } catch(OperationCanceledException) { stopped=true; }
                }
                Assert(stopped && !fake.Erased,"Cancellation before upload erased flash.");
            }
            log.Add("PASS: corrupt readback fails; cancellation before upload does not erase media.");
            using(var cancel=new CancellationTokenSource()) {
                fake=new SimulatedHub(); bool stopped=false;
                using(var hub=new HubSession(fake,CancellationToken.None)) {
                    try { hub.Upload(album,cancel.Token,(p,s)=> { if(p>0) cancel.Cancel(); }); } catch(OperationCanceledException) { stopped=true; }
                }
                Assert(stopped && fake.Erased && !fake.ReadBack,"Cancellation during transfer did not stop the upload.");
            }
            log.Add("PASS: missing readback bytes fail; active transfer cancellation stops before verification.");
            bool over=false;
            try { Wire.Album(pixels,2,50,1000); } catch(InvalidDataException) { over=true; }
            Assert(over,"Capacity check failed."); log.Add("PASS: capacity overflow rejected before upload.");
            if(File.Exists(gif)) {
                var prepared=Wallpaper.Prepare(gif,false,70,CancellationToken.None);
                Assert(prepared.Frames==70 && prepared.Pixels.Length==70*108800,"70-frame GIF conversion failed.");
                log.Add("PASS: generated GIF decodes to 70 native-size RGB565 frames.");
                var live=LiveMedia.Load(gif,false,CancellationToken.None);
                Assert(live.Frames.Count==70 && live.Frames.All(f=>f.Length==1632),"Live GIF decode failed.");
                Assert(live.IndexAt(0)==0 && live.IndexAt(live.Ends[0])==1 && live.IndexAt(live.Duration)==0,"GIF timing/wrapping failed.");
                var reports=HidLiveSink.Reports(live.Frames[0]); Assert(reports.Length==7,"Live chunk count failed.");
                var rebuilt=new List<byte>();
                for(int i=0;i<7;i++) { Assert(reports[i][0]==5 && reports[i][1]==1 && reports[i][2]==7 && reports[i][3]==i,"Live report headers failed."); rebuilt.AddRange(reports[i].Skip(4).Take(Math.Min(252,1632-i*252))); }
                Assert(rebuilt.SequenceEqual(live.Frames[0]),"Live chunk data order failed."); log.Add("PASS: 70-frame live decode, per-frame timing, RGB888 HID chunks, reassembly.");
            }
            CheckMediaLibrary(directory,gif,log);
            string playlistDir=Path.Combine(directory,"playlist-fixtures"); Directory.CreateDirectory(playlistDir);
            string a=Path.Combine(playlistDir,"a.png"),b=Path.Combine(playlistDir,"b.png");
            using(var bitmap=new Bitmap(32,17)) { using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.Red); bitmap.Save(a); using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.Lime); bitmap.Save(b); }
            File.SetLastWriteTimeUtc(a,new DateTime(2025,1,2,0,0,0,DateTimeKind.Utc)); File.SetLastWriteTimeUtc(b,new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc));
            Directory.CreateDirectory(Path.Combine(playlistDir,"nested")); File.WriteAllText(Path.Combine(playlistDir,"nested","ignored.gif"),"not scanned");
            File.WriteAllText(Path.Combine(playlistDir,".gif70-in-progress.gif"),"incomplete export");
            Assert(PlaylistFiles.Scan(playlistDir,PlaylistOrder.Filename).SequenceEqual(new[]{a,b}),"Filename order/depth failed.");
            Assert(PlaylistFiles.Scan(playlistDir,PlaylistOrder.Modified).SequenceEqual(new[]{b,a}),"Modified-date order failed.");
            var deck=new PlaylistDeck(123); var paths=new[]{a,b,"c.gif"}; string previous=null;
            for(int cycle=0;cycle<10;cycle++) { var picks=new HashSet<string>(); for(int i=0;i<3;i++) { string pick=deck.Next(paths,PlaylistOrder.Shuffle); Assert(pick!=previous,"Shuffle immediately repeated a file."); picks.Add(pick); previous=pick; } Assert(picks.Count==3,"Shuffle repeated inside a cycle."); }
            using(var signal=new AutoResetEvent(false)) using(var sink=new CountingSink()) {
                new PlaylistPlayer().Run(new PlaylistOptions { Folder=playlistDir,Order=PlaylistOrder.Filename,HoldSeconds=0.03,Fps=60 },sink,CancellationToken.None,signal,s=>{},2);
                Assert(sink.Frames>=2,"Playlist did not stream both files.");
            }
            using(var signal=new AutoResetEvent(true)) using(var pacing=new PlaybackPacer(1)) {
                var nextWatch=System.Diagnostics.Stopwatch.StartNew(); Assert(pacing.Wait(CancellationToken.None,signal) && nextWatch.ElapsedMilliseconds<250,"Next did not interrupt a low-FPS wait promptly.");
            }
            log.Add("PASS: filename/modified-date order, top-level-only scan, shuffle cycles, timed multi-file playback.");
            fake=new SimulatedHub(); byte[] config=(byte[])fake.Config.Clone();
            string journal=Path.Combine(directory,"test-live-session.xml");
            using(var hub=new HubSession(fake,CancellationToken.None)) {
                LiveDisplayMode.Run(hub,"SIMULATED",journal,CancellationToken.None,()=> {
                    Assert(fake.Config[2]==3,"Live display mode was not enabled.");
                    for(int i=0;i<24;i++) if(i!=2) Assert(fake.Config[i]==config[i],"Live mode changed another setting.");
                });
                Assert(fake.Config.SequenceEqual(config) && !File.Exists(journal),"Stop did not restore settings and clear the journal.");
            }
            Assert(!fake.Erased,"Live playback touched wallpaper flash."); log.Add("PASS: config framing, reserved-byte preservation, mode-only changes, restore-on-stop, no flash erase.");
            fake=new SimulatedHub();
            using(var hub=new HubSession(fake,CancellationToken.None)) {
                bool cancelled=false;
                try { LiveDisplayMode.Run(hub,"SIMULATED",journal,CancellationToken.None,()=> { fake.Config[4]=12; throw new OperationCanceledException(); }); }
                catch(OperationCanceledException) { cancelled=true; }
                Assert(cancelled && fake.Config[2]==2 && fake.Config[4]==12 && !File.Exists(journal),"Cancellation failed to restore the page while preserving other setting changes.");
            }
            log.Add("PASS: cancelled live session restores only its display-mode change and preserves concurrent brightness changes.");
            string nativeBackups=Path.Combine(directory,"native-playlist-backups");
            fake=new SimulatedHub(); Array.Copy(album,fake.Flash,album.Length); var originalFlash=(byte[])fake.Flash.Clone();
            fake.Config[2]=3; config=(byte[])fake.Config.Clone(); var native=new FullResolutionPlayer(); var transitions=new List<string>();
            using(var signal=new AutoResetEvent(false)) using(var hub=new HubSession(fake,CancellationToken.None)) {
                new FullResolutionPlayer().Run(new PlaylistOptions { Folder=playlistDir,HoldSeconds=0.01 },hub,nativeBackups,CancellationToken.None,signal,s=> {
                    if(s.Playing && (transitions.Count==0 || transitions.Last()!=s.Path)) transitions.Add(s.Path);
                    if(s.Playing) Assert(fake.Readbacks==fake.Uploads+1,"Display timer began before upload verification completed.");
                },2);
            }
            Assert(transitions.SequenceEqual(new[]{a,b}) && fake.Uploads==2 && fake.Readbacks==3,"Native playlist did not back up once and verify both uploads.");
            Assert(fake.Config[2]==2 && config.Where((v,i)=>i!=2).SequenceEqual(fake.Config.Where((v,i)=>i!=2)),"Native playlist changed settings other than the display page.");
            Assert(Wire.U16(fake.Flash,4)==320 && Wire.U16(fake.Flash,6)==170 && Wire.U16(fake.Flash,8)==1 && fake.Flash[8192]==7 && fake.Flash[8193]==224,"Native playlist's final file/dimensions are wrong.");
            Assert(Directory.GetFiles(nativeBackups,"*.bin").Any(p=>File.ReadAllBytes(p).SequenceEqual(originalFlash)),"Native playlist did not save original album bytes.");
            using(var bitmap=Wallpaper.Preview565(Wallpaper.Prepare(b,false,3,CancellationToken.None),0)) Assert(bitmap.GetPixel(160,85).G==255,"Native preview did not decode RGB565.");
            log.Add("PASS: native folder playlist backs up once, uploads distinct 320 x 170 files, verifies before counting display time, preserves settings, and leaves last wallpaper.");
            fake=new SimulatedHub(); Array.Copy(album,fake.Flash,album.Length); fake.Config[3]=2;
            using(var signal=new AutoResetEvent(false)) using(var hub=new HubSession(fake,CancellationToken.None)) {
                new FullResolutionPlayer().Run(new PlaylistOptions { Folder=playlistDir,HoldSeconds=0.01 },hub,nativeBackups,CancellationToken.None,signal,s=> {
                    if(s.Media!=null) Assert(s.Media.Portrait && s.Media.Width==320 && s.Media.Height==170,"Portrait playlist did not pack native frames.");
                },1);
            }
            Assert(fake.Config[3]==2 && Wire.U16(fake.Flash,4)==320 && Wire.U16(fake.Flash,6)==170,"Sideways album used an invalid LCD address window or changed orientation.");
            Wire.ValidateAlbumBackup(fake.Flash);
            var portraitMedia=Wallpaper.Prepare(a,false,3,CancellationToken.None,true);
            using(var portrait=Wallpaper.Preview565(portraitMedia,0)) {
                Assert(portrait.Width==170 && portrait.Height==320 && portrait.GetPixel(85,160).R==255,"Portrait RGB565 preview is transposed or corrupted.");
            }
            log.Add("PASS: portrait preview is upright; sideways albums/playlists retain native 320 x 170 LCD dimensions, valid CRCs and screen orientation.");
            fake=new SimulatedHub(); Array.Copy(album,fake.Flash,album.Length);
            using(var stop=new CancellationTokenSource()) using(var signal=new AutoResetEvent(false)) using(var hub=new HubSession(fake,CancellationToken.None)) {
                bool stopped=false;
                try { native.Run(new PlaylistOptions { Folder=playlistDir,HoldSeconds=1 },hub,nativeBackups,stop.Token,signal,s=> { if(s.Backup!=null && !s.Playing && s.Percent>0) stop.Cancel(); }); }
                catch(OperationCanceledException) { stopped=true; }
                Assert(stopped && fake.Uploads==1 && fake.Readbacks==2 && Wire.U16(fake.Flash,8)==1,"Stop interrupted a native upload or started another.");
                Wire.ValidateAlbumBackup(fake.Flash);
            }
            log.Add("PASS: Stop during native transfer finishes byte-for-byte verification and leaves a complete album.");
            string single=Path.Combine(directory,"single-native"); Directory.CreateDirectory(single); File.Copy(a,Path.Combine(single,"one.png"),true);
            fake=new SimulatedHub(); Array.Copy(album,fake.Flash,album.Length);
            using(var signal=new AutoResetEvent(true)) using(var hub=new HubSession(fake,CancellationToken.None)) native.Run(new PlaylistOptions { Folder=single,HoldSeconds=0.01 },hub,nativeBackups,CancellationToken.None,signal,s=>{},2);
            Assert(fake.Uploads==1,"Unchanged single-file playlist unnecessarily rewrote flash.");
            fake=new SimulatedHub(); Array.Copy(album,fake.Flash,album.Length);
            using(var stop=new CancellationTokenSource()) using(var signal=new AutoResetEvent(false)) using(var hub=new HubSession(fake,CancellationToken.None)) {
                bool stopped=false;
                try { native.Run(new PlaylistOptions { Folder=playlistDir },hub,nativeBackups,stop.Token,signal,s=> { if(s.Stage.Contains("Backing up")) stop.Cancel(); }); }
                catch(OperationCanceledException) { stopped=true; }
                Assert(stopped && fake.Uploads==0,"Stop during native backup erased flash.");
            }
            log.Add("PASS: unchanged native albums avoid repeated flash writes; Stop during backup does not erase wallpapers.");
            fake=new SimulatedHub(); Array.Copy(album,fake.Flash,album.Length); bool invalidPlaying=false,verifyFailed=false;
            using(var signal=new AutoResetEvent(false)) using(var hub=new HubSession(fake,CancellationToken.None)) {
                try { native.Run(new PlaylistOptions { Folder=playlistDir },hub,nativeBackups,CancellationToken.None,signal,s=> {
                    if(s.Backup!=null && !s.Playing && s.Percent>0) fake.Corrupt=true;
                    if(s.Playing) invalidPlaying=true;
                },1); } catch(InvalidDataException) { verifyFailed=true; }
            }
            Assert(verifyFailed && !invalidPlaying,"Native playlist started its display timer despite failed readback.");
            if(File.Exists(gif)) {
                string oversized=Path.Combine(directory,"oversized-native"); Directory.CreateDirectory(oversized); File.Copy(gif,Path.Combine(oversized,"too-many-frames.gif"),true);
                fake=new SimulatedHub(); bool noFit=false;
                using(var signal=new AutoResetEvent(false)) using(var hub=new HubSession(fake,CancellationToken.None)) {
                    try { native.Run(new PlaylistOptions { Folder=oversized },hub,nativeBackups,CancellationToken.None,signal,s=>{},1); }
                    catch(InvalidDataException) { noFit=true; }
                }
                Assert(noFit && !fake.Erased && fake.Readbacks==0,"Oversized native GIF touched flash before capacity validation.");
            }
            log.Add("PASS: failed native readback never starts display time; oversized GIFs are rejected before touching flash.");
            using(var form=new HubForm()) form.CaptureScreenshot(Path.Combine(directory,"control-ui.png"),gif);
            log.Add("PASS: Windows UI builds and renders.");
            File.WriteAllLines(Path.Combine(directory,"self-check.txt"),log);
        }
        static string CreateGifFixture(string directory) {
            string path=Path.Combine(directory,"sample-70.gif");
            using(var writer=new BinaryWriter(File.Create(path))) {
                writer.Write(Encoding.ASCII.GetBytes("GIF89a")); writer.Write((ushort)34); writer.Write((ushort)18);
                writer.Write(new byte[]{0x80,0,0,255,0,0,0,255,0});
                writer.Write(new byte[]{0x21,0xff,11}); writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0")); writer.Write(new byte[]{3,1,0,0,0});
                for(int frame=0;frame<70;frame++) {
                    writer.Write(new byte[]{0x21,0xf9,4,4,5,0,0,0,0x2c});
                    writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)34); writer.Write((ushort)18); writer.Write((byte)0); writer.Write((byte)2);
                    var data=new List<byte>(); int buffer=0,bits=0;
                    Action<int> code=value=> { buffer|=value<<bits; bits+=3; while(bits>=8) { data.Add((byte)buffer); buffer>>=8; bits-=8; } };
                    // Clear the LZW dictionary for each pixel so the fixture needs only 3-bit codes.
                    for(int pixel=0;pixel<34*18;pixel++) { code(4); code(frame%2); }
                    code(5); if(bits>0) data.Add((byte)buffer);
                    for(int offset=0;offset<data.Count;offset+=255) { int count=Math.Min(255,data.Count-offset); writer.Write((byte)count); writer.Write(data.GetRange(offset,count).ToArray()); }
                    writer.Write((byte)0);
                }
                writer.Write((byte)0x3b);
            }
            return path;
        }
        static void CheckSidewaysPacking(string directory,string gif,List<string> log) {
            string pattern=Path.Combine(directory,"portrait-corners.png");
            using(var image=new Bitmap(170,320)) {
                using(var g=Graphics.FromImage(image)) {
                    g.FillRectangle(Brushes.Red,0,0,85,160); g.FillRectangle(Brushes.Lime,85,0,85,160);
                    g.FillRectangle(Brushes.Blue,0,160,85,160); g.FillRectangle(Brushes.Yellow,85,160,85,160);
                }
                image.Save(pattern);
            }
            foreach(byte direction in new byte[]{2,3}) foreach(bool fill in new[]{false,true}) {
                var media=Wallpaper.Prepare(pattern,fill,77,CancellationToken.None,ScreenOrientation.IsPortrait(direction));
                byte[] album=Wire.Combine(new[]{media},8388608); Wire.ValidateAlbumBackup(album);
                int width=Wire.U16(album,4),height=Wire.U16(album,6);
                // Firmware 1.3.7 at 0x0801d6bc centers frames in a fixed native window,
                // then truncates its signed coordinates to unsigned 16-bit LCD addresses.
                int x=(320-width)/2,y=(170-height)/2;
                Assert(x>=0 && y>=0 && x+width<=320 && y+height<=170,"REGRESSION: sideways upload verifies but produces an invalid native LCD address window (black screen).");
                using(var wire=Wallpaper.Preview565(new PreparedMedia { Pixels=media.Pixels,Frames=media.Frames,Width=media.Width,Height=media.Height },0)) {
                    Assert(wire.Width==320 && wire.Height==170,"Sideways wire buffer has portrait dimensions.");
                    Assert(wire.GetPixel(80,42).ToArgb()==Color.Lime.ToArgb() && wire.GetPixel(240,42).ToArgb()==Color.Yellow.ToArgb() &&
                        wire.GetPixel(80,127).ToArgb()==Color.Red.ToArgb() && wire.GetPixel(240,127).ToArgb()==Color.Blue.ToArgb(),"Sideways pixels were stretched or rotated in the wrong direction.");
                }
                using(var upright=Wallpaper.Preview565(media,0)) {
                    Assert(upright.Width==170 && upright.Height==320 && upright.GetPixel(42,80).ToArgb()==Color.Red.ToArgb() &&
                        upright.GetPixel(127,80).ToArgb()==Color.Lime.ToArgb() && upright.GetPixel(42,240).ToArgb()==Color.Blue.ToArgb() &&
                        upright.GetPixel(127,240).ToArgb()==Color.Yellow.ToArgb(),"Sideways preview differs from the uploaded artwork.");
                }
            }
            bool invalidRejected=false;
            try { Wire.Combine(new[]{new PreparedMedia { Pixels=new byte[108800],Frames=1,Width=170,Height=320 }},8388608); }
            catch(InvalidDataException) { invalidRejected=true; }
            Assert(invalidRejected,"Album assembly accepted a portrait header the firmware cannot address.");
            foreach(byte direction in new byte[]{0,1,2,3}) {
                var media=Wallpaper.Prepare(gif,false,77,CancellationToken.None,ScreenOrientation.IsPortrait(direction));
                byte[] album=Wire.Combine(new[]{media},8388608); Wire.ValidateAlbumBackup(album);
                Assert(media.Frames==70 && album.Length==7624192 && Wire.U16(album,4)==320 && Wire.U16(album,6)==170,"70-frame upload is not native-sized in every orientation.");
            }
            log.Add("PASS: sideways corner pattern packs counterclockwise into native LCD bounds, previews upright, rejects portrait headers and preserves 70 frames in all orientations.");
        }
        static T Field<T>(object owner,string name) { return (T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner); }
        static void PumpUntil(Func<bool> completed,string message) {
            var clock=Stopwatch.StartNew();
            while(!completed() && clock.ElapsedMilliseconds<15000) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(completed(),message);
        }
        static void CheckMediaLibrary(string directory,string gif,List<string> log) {
            string root=Path.Combine(directory,"media-integration-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string first=Path.Combine(root,"first"),second=Path.Combine(root,"second"); Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            string a=Path.Combine(first,"same.png"),b=Path.Combine(second,"same.png");
            using(var bitmap=new Bitmap(24,24)) {
                using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.Red); bitmap.Save(a);
                using(var g=Graphics.FromImage(bitmap)) g.Clear(Color.Lime); bitmap.Save(b);
            }
            string folder=Path.Combine(root,"library"); var library=new MediaLibrary(folder);
            Assert(!Directory.Exists(folder),"Library construction performed unnecessary startup writes.");
            string savedA=library.Import(a),savedB=library.Import(b);
            Assert(savedA!=savedB && library.List().Length==2 && library.Import(a)==savedA,"Library collision handling or re-import deduplication failed.");
            Assert(new MediaLibrary(folder).List().Length==2,"Library files were not discoverable on a later launch.");
            File.Copy(a,Path.Combine(folder,".gif70-incomplete.gif"));
            Assert(library.List().Length==2,"Library listed an in-progress crop export.");
            library.Remove(savedA); Assert(File.Exists(a) && library.List().Length==1,"Removing a library copy changed its external source.");
            bool outsideRejected=false; try { library.Remove(b); } catch(InvalidOperationException) { outsideRejected=true; }
            Assert(outsideRejected,"Library removal accepted an external file.");
            log.Add("PASS: persistent media copies, collision-safe names, identical re-import reuse, and removal limited to library files.");
            if(!File.Exists(gif)) throw new FileNotFoundException("Missing generated GIF fixture.",gif);
            string mediaFolder=Path.Combine(root,"media"); library=new MediaLibrary(mediaFolder);
            string selected=library.Import(gif),argsFile=Path.Combine(root,"selected argument.txt"),ack=Path.Combine(root,"import acknowledged.txt");
            string script=Path.Combine(root,"fake crop tool & Unicode-测试.ps1");
            string other=Path.Combine(root,"opened another input.gif"); File.Copy(gif,other);
            string fixtureScript=@"param([string]$Path,[string]$ExportDirectory,[string]$ResultDirectory,[switch]$Portrait)
$ErrorActionPreference='Stop'
[IO.File]::WriteAllText('ARGS',$Path)
[IO.File]::WriteAllText('ARGS.portrait',[string][bool]$Portrait)
[void][IO.Directory]::CreateDirectory($ExportDirectory)
$output=Join-Path $ExportDirectory 'another export & 测试.gif'
[IO.File]::Copy('OTHER',$output)
[IO.File]::WriteAllText((Join-Path $ResultDirectory 'export.tmp'),$output,[Text.UTF8Encoding]::new($false))
[IO.File]::Move((Join-Path $ResultDirectory 'export.tmp'),(Join-Path $ResultDirectory 'export.result'))
$clock=[Diagnostics.Stopwatch]::StartNew()
while(-not [IO.File]::Exists('ACK') -and $clock.Elapsed.TotalSeconds -lt 12) { Start-Sleep -Milliseconds 50 }
".Replace("ARGS",argsFile.Replace("'","''")).Replace("OTHER",other.Replace("'","''")).Replace("ACK",ack.Replace("'","''"));
            File.WriteAllText(script,fixtureScript,Encoding.UTF8);
            using(var form=new HubForm(mediaFolder,script)) {
                var list=Field<ListBox>(form,"files");
                Assert(Field<LivePlaylistPanel>(form,"live")==null && Field<CropSession>(form,"cropSession")==null && Field<Image>(form,"source")==null && list.SelectedIndex==-1,"Startup loaded a playlist, cropper, or GIF preview.");
                Assert(list.Items.Count==1,"Stored library did not list saved files on startup.");
                form.ShowInTaskbar=false; form.Opacity=0; form.Show(); Application.DoEvents();
                var port=Field<ComboBox>(form,"ports"); port.Items.Add("TEST"); port.SelectedItem="TEST";
                Assert(Field<Label>(form,"uploadHint").Text.Contains("orientation not confirmed"),"Missing orientation did not show a persistent upload hint.");
                list.SelectedItem=selected;
                form.GetType().GetField("detectedOrientation",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,(byte)0);
                Field<ComboBox>(form,"orientation").SelectedIndex=1;
                Assert(!Field<Button>(form,"upload").Enabled && Field<Label>(form,"uploadHint").Text.Contains("Click Apply"),"Unapplied orientation did not explain why upload is disabled.");
                Field<ComboBox>(form,"orientation").SelectedIndex=0;
                Assert(Field<Button>(form,"upload").Enabled && Field<Label>(form,"uploadHint").Text.StartsWith("Ready"),"Confirmed orientation did not enable upload with clear guidance.");
                form.GetType().GetField("detectedOrientation",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(form,null);
                list.SelectedItem=selected; Field<ComboBox>(form,"orientation").SelectedIndex=2;
                var preview=Field<PictureBox>(form,"preview"); Assert(preview.Image.Width==170 && preview.Image.Height==320,"Sideways selection did not update the panel preview.");
                Assert(!Field<Button>(form,"upload").Enabled,"Unapplied orientation allowed a mismatched upload.");
                Field<Button>(form,"crop").PerformClick();
                string expected=Path.Combine(mediaFolder,"another export & 测试.gif");
                try { PumpUntil(()=>string.Equals(list.SelectedItem as string,expected,StringComparison.OrdinalIgnoreCase),"Crop export was not imported and selected while the cropper was open."); }
                catch(Exception ex) {
                    var session=Field<CropSession>(form,"cropSession");
                    string state=session==null?"closed":Field<string>(session,"receipts")+" files="+string.Join(",",Directory.GetFiles(Field<string>(session,"receipts")));
                    throw new Exception(ex.Message+" Status: "+Field<Label>(form,"status").Text+"; selected: "+list.SelectedItem+"; session: "+state,ex);
                }
                Assert(Field<CropSession>(form,"cropSession")!=null && list.Items.Count==2,"Crop import waited for process exit or did not refresh the library.");
                Assert(File.ReadAllText(argsFile)==selected,"Crop tool did not receive the currently selected file as a literal path.");
                Assert(File.ReadAllText(argsFile+".portrait")=="True","Sideways orientation was not passed to the cropper.");
                Assert(Field<LivePlaylistPanel>(form,"live")==null,"Cropping or importing unnecessarily created the playlist panel.");
                File.WriteAllText(ack,"received"); PumpUntil(()=>Field<CropSession>(form,"cropSession")==null,"Crop session did not close cleanly.");
                File.WriteAllText(script,"param([string]$Path,[string]$ExportDirectory,[string]$ResultDirectory,[switch]$Portrait)\r\nexit 0\r\n");
                Field<Button>(form,"crop").PerformClick(); PumpUntil(()=>Field<CropSession>(form,"cropSession")==null,"Cancelled crop session did not close.");
                Assert(list.Items.Count==2 && (string)list.SelectedItem==expected,"Closing without export imported or retargeted media.");
                var tabs=Field<TabControl>(form,"tabs"); var streaming=Field<TabPage>(form,"streaming"); tabs.SelectedTab=streaming;
                var live=Field<LivePlaylistPanel>(form,"live"); Assert(live!=null,"Opening Folder playlist did not create its panel."); live.ConfigurePreview(gif);
                tabs.SelectedTab=Field<TabPage>(form,"stored"); tabs.SelectedTab=streaming;
                Assert(object.ReferenceEquals(live,Field<LivePlaylistPanel>(form,"live")),"Playlist panel was recreated on each tab switch.");
                form.Close();
            }
            Assert(CropSession.JoinArguments(new[]{"a&b","trail\\","a\"b"})=="\"a&b\" \"trail\\\\\" \"a\\\"b\"","Windows cropper argument quoting failed.");
            log.Add("PASS: real child-process crop handoff preserves selected paths, imports another-source exports before exit, ignores cancellation, and creates optional UI only on demand.");
        }
    }
    public sealed class CountingSink : ILiveSink {
        public int Frames;
        public void Send(byte[] rgb,CancellationToken token) { token.ThrowIfCancellationRequested(); if(rgb.Length!=1632) throw new Exception("Wrong live frame size."); Frames++; }
        public void Dispose() { }
    }
    sealed class TelemetryTestLink : IHubLink {
        int reads; public int Reads { get { return Volatile.Read(ref reads); } } public volatile bool Disposed,Fail;
        byte[] reply;
        public void Write(byte[] packet) { Interlocked.Exchange(ref reply,packet); }
        public byte[] Read(int timeout,CancellationToken token) {
            token.ThrowIfCancellationRequested(); if(Disposed) throw new ObjectDisposedException("TelemetryTestLink");
            if(Fail) throw new IOException("Simulated unplug.");
            Thread.Sleep(2); Interlocked.Increment(ref reads);
            return Interlocked.Exchange(ref reply,null)??Wire.Packet(9,new byte[]{1,2,3});
        }
        public void Dispose() { Disposed=true; }
    }
}
