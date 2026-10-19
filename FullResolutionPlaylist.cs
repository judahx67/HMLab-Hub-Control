// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace HMLabHub {
    public sealed class FullResolutionStatus {
        public string Path,Stage,Backup;
        public int Percent,FrameIndex;
        public double UploadSeconds,Remaining;
        public PreparedMedia Media;
        public bool Playing;
    }
    public sealed class FullResolutionPlayer {
        public string BackupPath { get; private set; }
        // Cancellation is honored before erasing and while waiting. Once an upload starts,
        // finish its transfer/readback so Stop cannot leave a deliberately partial album.
        public void Run(PlaylistOptions options,HubSession hub,string backups,CancellationToken token,AutoResetEvent next,Action<FullResolutionStatus> update,int maxItems=0) {
            if(options.HoldSeconds<=0) throw new ArgumentException("Choose a positive display duration.");
            BackupPath=null; var deck=new PlaylistDeck(Environment.TickCount); int completed=0,bad=0; byte[] previousAlbum=null;
            while(true) {
                token.ThrowIfCancellationRequested();
                string[] paths=PlaylistFiles.Scan(options.Folder,options.Order);
                string path=deck.Next(paths,options.Order); PreparedMedia media;
                update(new FullResolutionStatus { Path=path,Stage="Preparing native frames...",Backup=BackupPath });
                bool portrait=ScreenOrientation.IsPortrait(hub.ReadConfig(token)[3]);
                try { media=Wallpaper.Prepare(path,options.Fill,hub.Info.Capacity,token,portrait); bad=0; }
                catch(OperationCanceledException) { throw; }
                catch(Exception ex) {
                    if(++bad>=paths.Length) throw new InvalidDataException("No playable files fit this hub: "+ex.Message,ex);
                    continue;
                }
                byte[] album=Wire.Combine(new[]{media},hub.Info.Flash);
                if(BackupPath==null) {
                    BackupPath=Program.SaveBackup(hub,backups,token,(p,s)=>update(new FullResolutionStatus { Path=path,Media=media,Stage=s,Percent=p }));
                }
                // Check the connection before erasing; SerialLink receives status continuously.
                hub.ReadConfig(token); token.ThrowIfCancellationRequested();
                var elapsed=Stopwatch.StartNew();
                if(previousAlbum==null || !previousAlbum.SequenceEqual(album)) {
                    hub.Upload(album,CancellationToken.None,(p,s)=>update(new FullResolutionStatus { Path=path,Media=media,Stage=s,Percent=p,Backup=BackupPath,UploadSeconds=elapsed.Elapsed.TotalSeconds }));
                    previousAlbum=album;
                }
                var config=hub.ReadConfig(CancellationToken.None);
                if(config[2]!=2) { config[2]=2; hub.WriteConfigVerified(config,CancellationToken.None); }
                double uploadSeconds=elapsed.Elapsed.TotalSeconds;
                var clock=Stopwatch.StartNew();
                while(true) {
                    update(new FullResolutionStatus { Path=path,Media=media,Playing=true,Stage="Playing verified wallpaper",Percent=100,Backup=BackupPath,UploadSeconds=uploadSeconds,
                        FrameIndex=media.Delay>0?(int)((clock.ElapsedMilliseconds/media.Delay)%media.Frames):0,
                        Remaining=Math.Max(0,options.HoldSeconds-clock.Elapsed.TotalSeconds) });
                    token.ThrowIfCancellationRequested();
                    if(clock.Elapsed.TotalSeconds>=options.HoldSeconds || next.WaitOne(0)) break;
                    int result=WaitHandle.WaitAny(new WaitHandle[]{token.WaitHandle,next},(int)Math.Min(100,Math.Max(1,Math.Ceiling((options.HoldSeconds-clock.Elapsed.TotalSeconds)*1000))));
                    if(result==0) token.ThrowIfCancellationRequested(); if(result==1) break;
                }
                if(maxItems>0 && ++completed>=maxItems) return;
            }
        }
    }
}
