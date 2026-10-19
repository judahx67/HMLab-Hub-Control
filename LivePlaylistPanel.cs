// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace HMLabHub {
    sealed class PixelPreview : Control {
        public Image Image;
        public PixelPreview() { DoubleBuffered=true; ResizeRedraw=true; BackColor=Color.FromArgb(27,31,39); }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); if(Image==null) return;
            double scale=Math.Min(Width/(double)Image.Width,Height/(double)Image.Height);
            int w=(int)Math.Round(Image.Width*scale),h=(int)Math.Round(Image.Height*scale);
            e.Graphics.InterpolationMode=InterpolationMode.NearestNeighbor; e.Graphics.PixelOffsetMode=PixelOffsetMode.Half;
            e.Graphics.DrawImage(Image,new Rectangle((Width-w)/2,(Height-h)/2,w,h),0,0,Image.Width,Image.Height,GraphicsUnit.Pixel);
        }
    }
    public sealed class LivePlaylistPanel : UserControl {
        readonly TextBox folder=new TextBox(); readonly ComboBox order=new ComboBox(),port=new ComboBox(),fit=new ComboBox(),mode=new ComboBox();
        readonly NumericUpDown seconds=new NumericUpDown(),fps=new NumericUpDown();
        readonly Button browse=new Button(),start=new Button(),next=new Button(),restore=new Button(),refresh=new Button();
        public readonly Button CancelControl=new Button();
        readonly ListBox files=new ListBox(); readonly PixelPreview preview=new PixelPreview();
        readonly Label status=new Label(),playing=new Label(),details=new Label(),modeNote=new Label(),transport=new Label(),fpsLabel=Label("Max USB fps");
        readonly string state,settings,journal;
        CancellationTokenSource cancel; readonly AutoResetEvent advance=new AutoResetEvent(false); bool running,previewOnly,fullResolution=true; long lastUi; string lastPath;
        public event EventHandler RunStateChanged,RequestClose;
        public bool IsRunning { get { return running; } }
        public LivePlaylistPanel() {
            Dock=DockStyle.Fill; Padding=new Padding(12);
            state=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"state"); settings=Path.Combine(state,"playlist.xml"); journal=Path.Combine(state,"live-session.xml");
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=8 };
            foreach(float h in new float[]{42,40,46,0,38,40,42,60}) layout.RowStyles.Add(new RowStyle(h==0?SizeType.Percent:SizeType.Absolute,h==0?100:h)); Controls.Add(layout);
            var source=Row(); source.Controls.Add(Label("Folder")); folder.Width=620; source.Controls.Add(folder); Button(browse,"Browse...",100); source.Controls.Add(browse); layout.Controls.Add(source,0,0);
            var modeRow=Row(); modeRow.Controls.Add(Label("Mode")); mode.Width=270; mode.DropDownStyle=ComboBoxStyle.DropDownList;
            mode.Items.AddRange(new object[]{"Full resolution (native)","Live USB: 32 x 17 (experimental)"}); mode.SelectedIndex=0; modeRow.Controls.Add(mode);
            modeNote.AutoSize=true; modeNote.Margin=new Padding(8,8,0,0); modeRow.Controls.Add(modeNote); layout.Controls.Add(modeRow,0,1);
            var options=Row(); options.Controls.Add(Label("Order")); order.DropDownStyle=ComboBoxStyle.DropDownList; order.Width=175;
            order.Items.AddRange(new object[]{"Filename A-Z","Filename Z-A","Modified: oldest first","Modified: newest first","Shuffle (no repeats)"}); order.SelectedIndex=0;
            options.Controls.Add(order); options.Controls.Add(Label("Display (s)")); seconds.Minimum=1; seconds.Maximum=86400; seconds.Value=30; seconds.Width=78; options.Controls.Add(seconds);
            options.Controls.Add(fpsLabel); fps.Minimum=1; fps.Maximum=60; fps.Value=30; fps.Width=60; options.Controls.Add(fps);
            fit.DropDownStyle=ComboBoxStyle.DropDownList; fit.Width=165; fit.Items.AddRange(new object[]{"Fit (black bars)","Fill (center crop)"}); fit.SelectedIndex=0; options.Controls.Add(fit); layout.Controls.Add(options,0,2);
            var body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,290)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            files.Dock=DockStyle.Fill; files.IntegralHeight=false; files.FormattingEnabled=true;
            files.Format+=delegate(object sender,ListControlConvertEventArgs e) { e.Value=Path.GetFileName((string)e.ListItem); };
            body.Controls.Add(files,0,0); preview.Dock=DockStyle.Fill; body.Controls.Add(preview,1,0); layout.Controls.Add(body,0,3);
            playing.Dock=DockStyle.Fill; playing.Text="Choose a folder to preview and play."; playing.Padding=new Padding(2,8,0,0); layout.Controls.Add(playing,0,4);
            var connection=Row(); connection.Controls.Add(Label("Hub port")); port.Width=95; port.DropDownStyle=ComboBoxStyle.DropDownList; connection.Controls.Add(port);
            Button(refresh,"Rescan",95); connection.Controls.Add(refresh); Button(restore,"Restore display",140); connection.Controls.Add(restore);
            transport.AutoSize=true; transport.Margin=new Padding(8,8,0,0); connection.Controls.Add(transport); layout.Controls.Add(connection,0,5);
            var actions=Row(); Button(start,"Start playlist",150); start.BackColor=Color.FromArgb(38,110,210); start.ForeColor=Color.White;
            Button(next,"Next file",105); Button(CancelControl,"Cancel",105); actions.Controls.AddRange(new Control[]{start,next,CancelControl});
            details.AutoSize=true; details.Margin=new Padding(12,8,0,0); details.Text="Runs while this app is open; minimizing keeps playback running."; actions.Controls.Add(details); layout.Controls.Add(actions,0,6);
            status.Dock=DockStyle.Fill; status.Text="Only this folder's files are used; changes are picked up at the next file. Disable this hub in SignalRGB while playing here."; layout.Controls.Add(status,0,7);
            browse.Click+=delegate { using(var dialog=new FolderBrowserDialog { Description="Choose the top-level GIF/image playlist folder",SelectedPath=folder.Text }) if(dialog.ShowDialog(this)==DialogResult.OK) { folder.Text=dialog.SelectedPath; Scan(); SaveSettings(); } };
            refresh.Click+=delegate { RefreshPorts(); Scan(); };
            folder.Leave+=delegate { if(!running) { Scan(); SaveSettings(); } };
            order.SelectedIndexChanged+=delegate { if(!running) Scan(); };
            files.SelectedIndexChanged+=delegate { if(!running) ShowSelected(); };
            fit.SelectedIndexChanged+=delegate { if(!running) ShowSelected(); };
            mode.SelectedIndexChanged+=delegate { fullResolution=mode.SelectedIndex==0; UpdateControls(); ShowSelected(); };
            start.Click+=delegate { Start(); }; next.Click+=delegate { advance.Set(); };
            CancelControl.Click+=delegate { if(running) Stop(); else if(RequestClose!=null) RequestClose(this,EventArgs.Empty); };
            restore.Click+=delegate { RestoreDisplay(); };
            LoadSettings(); RefreshPorts(); Scan(); UpdateControls();
            Resize+=delegate { folder.Width=Math.Max(280,Width-205); };
        }
        static FlowLayoutPanel Row() { return new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty }; }
        static Label Label(string text) { return new Label { Text=text,AutoSize=true,Margin=new Padding(3,8,8,0) }; }
        static void Button(Button button,string text,int width) { button.Text=text; button.Size=new Size(width,32); button.FlatStyle=FlatStyle.Flat; button.BackColor=Color.White; button.DialogResult=DialogResult.None; }
        void RefreshPorts() {
            string old=port.Text; string[] active=SerialPort.GetPortNames(); port.Items.Clear(); port.Items.AddRange(active);
            string found=HubForm.FindHubPort(active); if(active.Contains(old)) port.SelectedItem=old; else if(found!=null) port.SelectedItem=found; else if(active.Length>0) port.SelectedIndex=0;
        }
        void Scan() {
            if(string.IsNullOrWhiteSpace(folder.Text)) return;
            try {
                string selected=files.SelectedItem as string; string[] paths=PlaylistFiles.Scan(folder.Text,(PlaylistOrder)Math.Max(0,order.SelectedIndex));
                files.BeginUpdate(); files.Items.Clear(); files.Items.AddRange(paths); files.EndUpdate();
                if(selected!=null && paths.Contains(selected)) files.SelectedItem=selected; else if(files.Items.Count>0) files.SelectedIndex=0;
                if(paths.Length==0) { var old=preview.Image; preview.Image=null; if(old!=null) old.Dispose(); preview.Invalidate(); playing.Text="No supported images or GIFs in this folder."; }
                status.Text=paths.Length+" file(s), top-level folder only; additions/removals are read at each transition.";
            } catch(Exception ex) { status.Text=ex.Message; }
            UpdateControls();
        }
        bool portraitPreview;
        public void SetScreenOrientation(byte value) { portraitPreview=ScreenOrientation.IsPortrait(value); if(!running) ShowSelected(); }
        void ShowSelected() {
            string path=files.SelectedItem as string; if(path==null) return;
            try {
                using(var stream=new MemoryStream(File.ReadAllBytes(path))) using(var image=Image.FromStream(stream)) using(var small=Wallpaper.RenderAt(image,fit.SelectedIndex==1,fullResolution?(portraitPreview?170:320):32,fullResolution?(portraitPreview?320:170):17)) {
                    var old=preview.Image; preview.Image=(Image)small.Clone(); if(old!=null) old.Dispose(); preview.Invalidate();
                    playing.Text=Path.GetFileName(path)+" | "+Wallpaper.FrameCount(image)+" source frame(s) | modified "+File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
                }
            } catch(Exception ex) { status.Text="Preview: "+ex.Message; }
        }
        public void ConfigurePreview(string sample) { previewOnly=true; if(File.Exists(sample)) { folder.Text=Path.GetDirectoryName(sample); Scan(); files.SelectedItem=sample; } }
        public void Stop() { if(cancel!=null) { cancel.Cancel(); CancelControl.Enabled=false; next.Enabled=false; status.Text=fullResolution?"Stopping after the current upload/verification; last complete wallpaper stays on the hub...":"Stopping playback and restoring the original display mode..."; } }
        void UpdateControls() {
            folder.Enabled=browse.Enabled=order.Enabled=seconds.Enabled=mode.Enabled=fit.Enabled=port.Enabled=refresh.Enabled=files.Enabled=!running;
            fps.Enabled=!running && !fullResolution;
            fpsLabel.Visible=fps.Visible=!fullResolution;
            modeNote.Text=fullResolution?"Uploads per file; display timer starts after verification.":"32 x 17 stream; fast transitions, lower image detail.";
            transport.Text=fullResolution?"Native resolution | Backup once; flash replaced per file":"32 x 17 | No wallpaper flash writes";
            start.Enabled=!running && files.Items.Count>0 && !string.IsNullOrWhiteSpace(port.Text) && Directory.Exists(folder.Text) && !File.Exists(journal);
            next.Enabled=running; CancelControl.Enabled=true; CancelControl.Text=running?"Stop playlist":"Cancel"; restore.Enabled=!running && File.Exists(journal);
        }
        async void Start() {
            if(running) return;
            PlaylistOptions options;
            try {
                if(PlaylistFiles.Scan(folder.Text,(PlaylistOrder)order.SelectedIndex).Length==0) throw new InvalidDataException("Choose a folder with GIFs or images.");
                options=new PlaylistOptions { Folder=Path.GetFullPath(folder.Text),Port=port.Text,Order=(PlaylistOrder)order.SelectedIndex,HoldSeconds=(double)seconds.Value,Fps=(int)fps.Value,Fill=fit.SelectedIndex==1 };
                SaveSettings();
            } catch(Exception ex) { status.Text=ex.Message; return; }
            cancel=new CancellationTokenSource(); running=true; lastUi=0; lastPath=null; advance.Reset(); UpdateControls(); if(RunStateChanged!=null) RunStateChanged(this,EventArgs.Empty);
            var native=new FullResolutionPlayer();
            status.Text=fullResolution?"Preparing full-resolution playback; backing up the existing album before any replacement...":"Preparing live playback; preserving original display settings...";
            try {
                await Task.Run(()=> {
                    if(fullResolution) {
                        using(var hub=new HubSession(new SerialLink(options.Port),cancel.Token)) native.Run(options,hub,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"backups"),cancel.Token,advance,ReportFullResolution);
                    } else using(var sink=new HidLiveSink()) using(var hub=new HubSession(new SerialLink(options.Port),cancel.Token)) {
                            LiveDisplayMode.Run(hub,options.Port,journal,cancel.Token,()=>new PlaylistPlayer().Run(options,sink,cancel.Token,advance,Report));
                        }
                });
                status.Text=fullResolution?"Stopped; the last verified wallpaper stays on the hub.":"Playlist ended; original display mode restored.";
            } catch(OperationCanceledException) { status.Text=fullResolution?(native.BackupPath==null?"Stopped before replacing wallpapers.":"Stopped; last complete wallpaper kept. Backup: "+native.BackupPath):"Stopped; original display mode restored."; }
            catch(Exception ex) { status.Text=ex.Message+(native.BackupPath!=null?" Original backup: "+native.BackupPath:"")+(File.Exists(journal)?" Use Restore display after reconnecting.":""); }
            finally { running=false; cancel.Dispose(); cancel=null; UpdateControls(); if(RunStateChanged!=null) RunStateChanged(this,EventArgs.Empty); }
        }
        void Report(LiveStatus data) {
            if(IsDisposed || !IsHandleCreated) return; long now=Stopwatch.GetTimestamp();
            if((now-lastUi)/(double)Stopwatch.Frequency<0.15) return; lastUi=now;
            BeginInvoke((Action)delegate {
                if(!running) return; var image=LiveMedia.Preview(data.Rgb); var old=preview.Image; preview.Image=image; if(old!=null) old.Dispose(); preview.Invalidate();
                if(files.Items.Contains(data.Path)) files.SelectedItem=data.Path;
                playing.Text=string.Format("{0} | {1} source frames | next in {2:0.0}s",Path.GetFileName(data.Path),data.FrameCount,data.Remaining);
                status.Text=string.Format("Live USB: {0:0.0} sent fps | {1:0.00} ms average frame write | original display restored on Stop",data.Fps,data.WriteMs);
            });
        }
        void ReportFullResolution(FullResolutionStatus data) {
            if(IsDisposed || !IsHandleCreated) return; long now=Stopwatch.GetTimestamp();
            if(data.Path==lastPath && (now-lastUi)/(double)Stopwatch.Frequency<0.15) return;
            lastUi=now; lastPath=data.Path;
            BeginInvoke((Action)delegate {
                if(!running) return;
                if(data.Media!=null) { var old=preview.Image; preview.Image=Wallpaper.Preview565(data.Media,data.FrameIndex); if(old!=null) old.Dispose(); preview.Invalidate(); }
                if(files.Items.Contains(data.Path)) files.SelectedItem=data.Path;
                playing.Text=Path.GetFileName(data.Path)+(data.Media==null?"":" | "+data.Media.Frames+" native frames")+(data.Playing?string.Format(" | next in {0:0.0}s",data.Remaining):" | "+data.Stage);
                status.Text=data.Playing?string.Format("{0} | Upload + verification: {1:0.0}s | Stop keeps this wallpaper. Original album backed up in backups/.",data.Media.Portrait?"Portrait on native 320 x 170":"320 x 170",data.UploadSeconds):
                    string.Format("{0} {1}% | {2:0.0}s elapsed. Stop finishes any active upload; USB disconnect can leave an incomplete album.",data.Stage,data.Percent,data.UploadSeconds);
                if(cancel!=null && cancel.IsCancellationRequested) status.Text="Stopping after the current upload/verification; last complete wallpaper stays on the hub...";
            });
        }
        async void RestoreDisplay() {
            if(running || !File.Exists(journal)) return;
            running=true; cancel=new CancellationTokenSource(); UpdateControls(); if(RunStateChanged!=null) RunStateChanged(this,EventArgs.Empty);
            string selected=port.Text;
            try { await Task.Run(()=>LiveDisplayMode.Restore(journal,selected,CancellationToken.None)); status.Text="Original display mode restored."; }
            catch(Exception ex) { status.Text="Restore failed: "+ex.Message; }
            finally { running=false; cancel.Dispose(); cancel=null; UpdateControls(); if(RunStateChanged!=null) RunStateChanged(this,EventArgs.Empty); }
        }
        void SaveSettings() {
            if(previewOnly) return;
            var doc=new XmlDocument(); var root=doc.CreateElement("Playlist"); doc.AppendChild(root);
            root.SetAttribute("Folder",folder.Text); root.SetAttribute("Order",order.SelectedIndex.ToString()); root.SetAttribute("Seconds",seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            root.SetAttribute("Fps",fps.Value.ToString()); root.SetAttribute("Fill",fit.SelectedIndex.ToString()); root.SetAttribute("Mode",mode.SelectedIndex.ToString()); Directory.CreateDirectory(state); doc.Save(settings);
        }
        void LoadSettings() {
            if(!File.Exists(settings)) return;
            try {
                var doc=new XmlDocument { XmlResolver=null }; doc.Load(settings); var root=doc.DocumentElement;
                folder.Text=root.GetAttribute("Folder"); int o,f; decimal s;
                if(int.TryParse(root.GetAttribute("Order"),out o) && o>=0 && o<order.Items.Count) order.SelectedIndex=o;
                if(decimal.TryParse(root.GetAttribute("Seconds"),System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out s)) seconds.Value=Math.Min(seconds.Maximum,Math.Max(seconds.Minimum,s));
                if(int.TryParse(root.GetAttribute("Fps"),out f)) fps.Value=Math.Min(fps.Maximum,Math.Max(fps.Minimum,f));
                fit.SelectedIndex=root.GetAttribute("Fill")=="1"?1:0;
                mode.SelectedIndex=root.GetAttribute("Mode")=="1"?1:0;
            } catch { status.Text="Saved playlist options could not be loaded; choose a folder."; }
        }
        protected override void Dispose(bool disposing) {
            if(disposing) { if(cancel!=null) cancel.Cancel(); advance.Dispose(); if(preview.Image!=null) preview.Image.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
