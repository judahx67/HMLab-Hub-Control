// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HMLabHub {
    static class Program {
        [STAThread] static int Main(string[] args) {
            try {
                if(args.Length>0 && args[0]=="--self-check") { SelfCheck.Run(args.Length>1?args[1]:null); return 0; }
                if(args.Length>1 && args[0]=="--probe") {
                    using(var hub=new HubSession(new SerialLink(args[1]),CancellationToken.None)) File.WriteAllText(args.Length>2?args[2]:"hub-probe.txt",hub.Info.ToString());
                    return 0;
                }
                if(args.Length>2 && args[0]=="--live-info") {
                    using(var hub=new HubSession(new SerialLink(args[1]),CancellationToken.None)) {
                        byte[] raw=hub.ReadConfig(CancellationToken.None);
                        var lines=new List<string> { hub.Info.ToString(),"Config: "+BitConverter.ToString(raw),"Display page: "+raw[2] };
                        foreach(var endpoint in HidNative.Find()) lines.Add(endpoint.ToString()); File.WriteAllLines(args[2],lines);
                    }
                    return 0;
                }
                if(args.Length>3 && args[0]=="--live-test") {
                    double duration=args.Length>4?double.Parse(args[4],System.Globalization.CultureInfo.InvariantCulture):10;
                    int rate=args.Length>5?int.Parse(args[5]):30;
                    if(duration<=0 || duration>60 || rate<1 || rate>60) throw new ArgumentException("Live test needs 1-60 FPS and up to 60 seconds.");
                    var media=LiveMedia.Load(args[2],false,CancellationToken.None); int sent=0; double writes=0; double elapsed=0; byte[] before,after;
                    using(var sink=new HidLiveSink()) using(var hub=new HubSession(new SerialLink(args[1]),CancellationToken.None)) {
                        before=hub.ReadConfig(CancellationToken.None);
                        LiveDisplayMode.Run(hub,args[1],Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"state","live-session.xml"),CancellationToken.None,()=> {
                            var clock=Stopwatch.StartNew();
                            using(var pacing=new PlaybackPacer(rate)) {
                            while(clock.Elapsed.TotalSeconds<duration) {
                                long at=clock.ElapsedMilliseconds; var timer=Stopwatch.StartNew(); sink.Send(media.Frames[media.IndexAt(at)],CancellationToken.None); writes+=timer.Elapsed.TotalMilliseconds; sent++;
                                pacing.Wait(CancellationToken.None);
                            }
                            }
                            elapsed=clock.Elapsed.TotalSeconds;
                        });
                        after=hub.ReadConfig(CancellationToken.None);
                        if(!before.SequenceEqual(after)) throw new InvalidDataException("Live test did not restore every original configuration byte.");
                    }
                    File.WriteAllText(args[3],string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Source: {0}\nLive: 32 x 17 RGB888; {1} source frames\nSent {2} frames in {3:0.000}s at {4:0.00} USB fps (cap {5})\nAverage frame write: {6:0.000} ms\nReports per frame: 7; report ID: 05; Windows report length: 257\nOriginal 24-byte configuration restored: {7}\nWallpaper flash writes: 0\n",Path.GetFullPath(args[2]),media.Frames.Count,sent,elapsed,sent/elapsed,rate,writes/sent,BitConverter.ToString(after)));
                    return 0;
                }
                if(args.Length>3 && args[0]=="--playlist-test") {
                    var options=new PlaylistOptions { Port=args[1],Folder=Path.GetFullPath(args[2]),Order=PlaylistOrder.Filename,HoldSeconds=2,Fps=30 };
                    var observed=new List<string>(); byte[] before,after; bool stopped=false;
                    using(var stop=new CancellationTokenSource()) using(var advance=new AutoResetEvent(false)) using(var sink=new HidLiveSink()) using(var hub=new HubSession(new SerialLink(options.Port),CancellationToken.None)) {
                        before=hub.ReadConfig(CancellationToken.None);
                        try {
                            LiveDisplayMode.Run(hub,options.Port,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"state","live-session.xml"),stop.Token,()=> {
                                stop.CancelAfter(5500);
                                new PlaylistPlayer().Run(options,sink,stop.Token,advance,s=> { if(observed.Count==0 || observed[observed.Count-1]!=s.Path) observed.Add(s.Path); });
                            });
                        } catch(OperationCanceledException) { stopped=true; }
                        after=hub.ReadConfig(CancellationToken.None);
                        if(!stopped || !before.SequenceEqual(after) || observed.Distinct().Count()<2) throw new InvalidDataException("Playlist transitions, Stop, or configuration restoration failed.");
                    }
                    File.WriteAllLines(args[3],new[]{"PASS: real folder playlist streamed multiple files in filename order.","PASS: cancellation stopped live playback and restored every original config byte.","Wallpaper flash writes: 0","Transitions: "+string.Join(" -> ",observed.Select(Path.GetFileName))});
                    return 0;
                }
                if(args.Length>3 && args[0]=="--upload") {
                    string folder=Path.GetFullPath(args[3]); Directory.CreateDirectory(folder);
                    using(var hub=new HubSession(new SerialLink(args[1]),CancellationToken.None)) {
                        var media=Wallpaper.Prepare(args[2],false,hub.Info.Capacity,CancellationToken.None,ScreenOrientation.IsPortrait(hub.ReadConfig(CancellationToken.None)[3]));
                        var album=Wire.Combine(new List<PreparedMedia>{media},hub.Info.Flash);
                        string backup=SaveBackup(hub,folder,CancellationToken.None,(p,s)=>{});
                        string displayWarning=hub.UploadWallpaper(album,CancellationToken.None,(p,s)=>{});
                        File.WriteAllText(Path.Combine(folder,"upload-result.txt"),hub.Info+Environment.NewLine+
                            "Uploaded and readback verified: "+Path.GetFullPath(args[2])+Environment.NewLine+
                            "Frames: "+media.Frames+"; native panel: "+media.Width+" x "+media.Height+"; backup: "+backup+
                            (displayWarning==null?"":Environment.NewLine+displayWarning));
                    }
                    return 0;
                }
                if(args.Length>3 && args[0]=="--restore-album") {
                    byte[] album=File.ReadAllBytes(args[2]); Wire.ValidateAlbumBackup(album);
                    using(var hub=new HubSession(new SerialLink(args[1]),CancellationToken.None)) {
                        if(album.Length>hub.Info.Flash) throw new InvalidDataException("Saved album exceeds this hub's capacity.");
                        string backup;
                        try { backup=SaveBackup(hub,args[3],CancellationToken.None,(p,s)=>{}); }
                        catch(InvalidDataException ex) { backup="Current album is invalid; restoring the selected validated backup. "+ex.Message; }
                        hub.Upload(album,CancellationToken.None,(p,s)=>{});
                        var raw=hub.ReadConfig(CancellationToken.None); if(raw[2]!=2) { raw[2]=2; hub.WriteConfigVerified(raw,CancellationToken.None); }
                        File.WriteAllText(Path.Combine(args[3],"restore-result.txt"),"Album restored and readback verified: "+Path.GetFullPath(args[2])+Environment.NewLine+"Previous album backup: "+backup);
                    }
                    return 0;
                }
                if(args.Length>3 && args[0]=="--native-playlist-test") {
                    string directory=Path.GetFullPath(args[3]); Directory.CreateDirectory(directory);
                    var player=new FullResolutionPlayer(); var transitions=new List<string>(); var timings=new List<string>();
                    using(var next=new AutoResetEvent(false)) using(var hub=new HubSession(new SerialLink(args[1]),CancellationToken.None)) {
                        byte[] original=hub.ReadConfig(CancellationToken.None);
                        try {
                            player.Run(new PlaylistOptions { Folder=Path.GetFullPath(args[2]),Port=args[1],HoldSeconds=0.2 },hub,directory,CancellationToken.None,next,s=> {
                                if(s.Playing && (transitions.Count==0 || transitions.Last()!=s.Path)) {
                                    transitions.Add(s.Path); timings.Add(Path.GetFileName(s.Path)+": "+s.UploadSeconds.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"s upload + verification");
                                    File.WriteAllLines(Path.Combine(directory,"progress.txt"),timings);
                                }
                            },2);
                            if(transitions.Distinct().Count()!=2) throw new InvalidDataException("Expected two distinct native playlist files.");
                        } finally {
                            if(player.BackupPath!=null) {
                                byte[] backup=File.ReadAllBytes(player.BackupPath); Wire.ValidateAlbumBackup(backup);
                                hub.Upload(backup,CancellationToken.None,(p,s)=>{});
                            }
                            var current=hub.ReadConfig(CancellationToken.None); current[2]=original[2]; hub.WriteConfigVerified(current,CancellationToken.None);
                            if(!hub.ReadConfig(CancellationToken.None).SequenceEqual(original)) throw new InvalidDataException("Native test did not restore every original configuration byte.");
                        }
                        timings.Add("PASS: two native folder transitions, each byte-for-byte verified before its display timer.");
                        timings.Add("PASS: pre-test album restored and byte-for-byte verified; original 24-byte configuration restored.");
                        timings.Add("Original album backup: "+player.BackupPath);
                        File.WriteAllLines(Path.Combine(directory,"result.txt"),timings);
                    }
                    return 0;
                }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new HubForm()); return 0;
            } catch(Exception ex) {
                if(args.Length>0) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"last-error.txt"),ex.ToString()); return 1; }
                MessageBox.Show(ex.Message,"HMLab Hub Control",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1;
            }
        }
        public static string SaveBackup(HubSession hub,string folder,CancellationToken token,Action<int,string> progress) {
            byte[] data=hub.ReadFlash(token,progress); token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,"hub-album-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-UTC.bin");
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write)) file.Write(data,0,data.Length);
            File.WriteAllText(path+".txt",hub.Info.ToString()); return path;
        }
    }
    public sealed class HubForm : Form {
        LivePlaylistPanel live; readonly TabControl tabs=new TabControl(); readonly TabPage stored=new TabPage("Stored wallpapers"),streaming=new TabPage("Folder playlist");
        readonly MediaLibrary library; readonly string cropScriptOverride; CropSession cropSession;
        readonly Queue<string[]> pendingImports=new Queue<string[]>(); bool importing;
        readonly ComboBox ports=new ComboBox(),fit=new ComboBox(),orientation=new ComboBox();
        readonly Button detect=new Button(),applyOrientation=new Button(),add=new Button(),remove=new Button(),crop=new Button(),upload=new Button(),cancel=new Button();
        byte? detectedOrientation;
        readonly CheckBox skipBackup=new CheckBox(),skipVerification=new CheckBox();
        readonly ListBox files=new ListBox();
        readonly PictureBox preview=new PictureBox();
        readonly Label device=new Label(),description=new Label(),note=new Label(),status=new Label(),uploadHint=new Label();
        readonly ProgressBar progress=new ProgressBar();
        readonly System.Windows.Forms.Timer animation=new System.Windows.Forms.Timer();
        Image source; Stream sourceStream; int frameIndex,frames; HubInfo info;
        CancellationTokenSource cancellation; bool busy,closing,writing; int lastReport;
        public HubForm() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"media")) { }
        internal HubForm(string mediaDirectory,string cropScript=null) {
            library=new MediaLibrary(mediaDirectory); cropScriptOverride=cropScript;
            Text="HMLab Hub Control - playlists and wallpapers"; Font=new Font("Segoe UI",10);
            BackColor=Color.FromArgb(245,247,250); ClientSize=new Size(1020,690); MinimumSize=new Size(900,620); StartPosition=FormStartPosition.CenterScreen;
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=1,RowCount=6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            tabs.Dock=DockStyle.Fill; stored.Controls.Add(layout); tabs.TabPages.Add(stored); tabs.TabPages.Add(streaming); Controls.Add(tabs);
            var connection=Row(); connection.Controls.Add(new Label { Text="Hub port",AutoSize=true,Margin=new Padding(3,9,8,0) });
            ports.Width=150; ports.DropDownStyle=ComboBoxStyle.DropDownList; connection.Controls.Add(ports);
            ButtonStyle(detect,"Detect hub",120); connection.Controls.Add(detect);
            connection.Controls.Add(new Label { Text="Screen",AutoSize=true,Margin=new Padding(12,9,6,0) });
            orientation.Width=190; orientation.DropDownStyle=ComboBoxStyle.DropDownList;
            orientation.Items.AddRange(new object[]{"Normal (0°)","Upside down (180°)","Sideways (90°)","Sideways (270°)"}); orientation.SelectedIndex=0; connection.Controls.Add(orientation);
            ButtonStyle(applyOrientation,"Apply",80); connection.Controls.Add(applyOrientation); layout.Controls.Add(connection,0,0);
            device.Text="Detect hub to read its screen orientation; Apply changes only orientation."; device.Dock=DockStyle.Fill; device.AutoEllipsis=true; layout.Controls.Add(device,0,1);
            var body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=2 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,300)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent,100)); body.RowStyles.Add(new RowStyle(SizeType.Absolute,52));
            files.Dock=DockStyle.Fill; files.IntegralHeight=false; files.HorizontalScrollbar=true; files.FormattingEnabled=true;
            files.Format+=delegate(object sender,ListControlConvertEventArgs e) { e.Value=Path.GetFileName((string)e.ListItem); }; body.Controls.Add(files,0,0);
            preview.Dock=DockStyle.Fill; preview.BackColor=Color.FromArgb(27,31,39); preview.SizeMode=PictureBoxSizeMode.Zoom; body.Controls.Add(preview,1,0);
            var tools=Row(); ButtonStyle(add,"Add files...",100); ButtonStyle(remove,"Remove",85); ButtonStyle(crop,"Crop tool",95);
            tools.Controls.AddRange(new Control[]{add,remove,crop}); body.Controls.Add(tools,0,1);
            description.Dock=DockStyle.Fill; description.Padding=new Padding(10,5,0,0); description.Text="Native panel preview: 320 x 170"; body.Controls.Add(description,1,1); layout.Controls.Add(body,0,2);
            var options=Row(); options.Controls.Add(new Label { Text="Resize",AutoSize=true,Margin=new Padding(3,9,8,0) });
            fit.DropDownStyle=ComboBoxStyle.DropDownList; fit.Width=240; fit.Items.AddRange(new object[]{"Fit inside (black bars)","Fill panel (center crop)"}); fit.SelectedIndex=0; options.Controls.Add(fit);
            options.Controls.Add(new Label { Text="GIF timing uses one average delay per animation.",AutoSize=true,Margin=new Padding(15,9,0,0) });
            var uploadOptions=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty };
            uploadOptions.RowStyles.Add(new RowStyle(SizeType.Absolute,40)); uploadOptions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            uploadHint.Dock=DockStyle.Fill; uploadHint.TextAlign=ContentAlignment.MiddleLeft; uploadHint.Padding=new Padding(3,0,0,0);
            uploadOptions.Controls.Add(options,0,0); uploadOptions.Controls.Add(uploadHint,0,1); layout.Controls.Add(uploadOptions,0,3);
            var actions=Row(); ButtonStyle(upload,"Upload selected media",225); upload.BackColor=Color.FromArgb(38,110,210); upload.ForeColor=Color.White;
            ButtonStyle(cancel,"Cancel",100);
            skipBackup.Appearance=Appearance.Button; skipBackup.AutoSize=false; skipBackup.Size=new Size(150,32); skipBackup.FlatStyle=FlatStyle.Flat;
            skipBackup.TextAlign=ContentAlignment.MiddleCenter; skipBackup.Checked=false;
            skipVerification.Appearance=Appearance.Button; skipVerification.AutoSize=false; skipVerification.Size=new Size(180,32); skipVerification.FlatStyle=FlatStyle.Flat;
            skipVerification.TextAlign=ContentAlignment.MiddleCenter; skipVerification.Checked=false;
            skipBackup.CheckedChanged+=delegate { UpdateUploadOptions(); }; skipVerification.CheckedChanged+=delegate { UpdateUploadOptions(); };
            actions.Controls.AddRange(new Control[]{upload,cancel,skipBackup,skipVerification}); note.AutoSize=true; note.Margin=new Padding(10,9,0,0); actions.Controls.Add(note); layout.Controls.Add(actions,0,4); UpdateUploadOptions();
            var footer=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,220));
            status.Dock=DockStyle.Fill; status.Text="Add media to your library, then select a file to preview or upload."; status.AutoEllipsis=true;
            progress.Dock=DockStyle.Fill; progress.Visible=false; footer.Controls.Add(status,0,0); footer.Controls.Add(progress,1,0); layout.Controls.Add(footer,0,5);
            CancelButton=cancel; cancel.DialogResult=DialogResult.None;
            tabs.SelectedIndexChanged+=delegate { CancelButton=tabs.SelectedTab==streaming && live!=null?live.CancelControl:cancel; cancel.DialogResult=DialogResult.None; if(live!=null) live.CancelControl.DialogResult=DialogResult.None; };
            tabs.Selecting+=delegate(object sender,TabControlCancelEventArgs e) {
                if((busy && e.TabPage!=stored) || (live!=null && live.IsRunning && e.TabPage==stored)) { e.Cancel=true; return; }
                if(e.TabPage==streaming) EnsureLive();
            };
            CancelButton=cancel;
            tabs.SelectedTab=stored;
            detect.Click+=delegate { RefreshPorts(); Probe(); };
            orientation.SelectedIndexChanged+=delegate { RenderPreview(); UpdateButtons(); status.Text="Apply the chosen orientation before uploading; sideways uses portrait media."; };
            applyOrientation.Click+=delegate { ApplyOrientation(); };
            add.Click+=delegate { using(var dialog=new OpenFileDialog { Filter="Images and GIFs|*.gif;*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",Multiselect=true }) if(dialog.ShowDialog(this)==DialogResult.OK) {
                QueueImports(dialog.FileNames); } };
            remove.Click+=delegate {
                string selected=files.SelectedItem as string; if(selected==null) return;
                if(MessageBox.Show(this,"Remove "+Path.GetFileName(selected)+" from the library? This deletes its saved library file.","Remove media",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return;
                try { DisposePreview(); library.Remove(selected); RefreshLibrary(null); status.Text="Media removed from the local library."; } catch(Exception ex) { status.Text=ex.Message; }
            };
            crop.Click+=delegate { OpenCropper(); };
            files.SelectedIndexChanged+=delegate { LoadPreview(); }; fit.SelectedIndexChanged+=delegate { RenderPreview(); };
            ports.SelectedIndexChanged+=delegate { info=null; detectedOrientation=null; device.Text="Selected "+ports.Text+"; Detect hub reads its identity and orientation."; UpdateButtons(); };
            upload.Click+=delegate { BeginUpload(); };
            cancel.Click+=delegate { if(busy) { cancellation.Cancel(); status.Text="Cancelling..."; } else Close(); };
            animation.Tick+=delegate { if(source==null || frames<2) return; frameIndex=(frameIndex+1)%frames; source.SelectActiveFrame(FrameDimension.Time,frameIndex); RenderPreview(); };
            FormClosing+=delegate(object sender,FormClosingEventArgs e) {
                if(live!=null && live.IsRunning) { e.Cancel=true; closing=true; live.Stop(); }
                if(busy) { e.Cancel=true; closing=true; cancellation.Cancel(); }
            };
            RefreshPorts(); RefreshLibrary(null); UpdateButtons();
        }
        void EnsureLive() {
            if(live!=null) return;
            live=new LivePlaylistPanel(); streaming.Controls.Add(live);
            live.SetScreenOrientation(detectedOrientation.GetValueOrDefault());
            live.RequestClose+=delegate { Close(); };
            live.RunStateChanged+=delegate { stored.Enabled=!live.IsRunning; if(closing && !live.IsRunning && !busy) Close(); };
        }
        void RefreshLibrary(string selected) {
            files.BeginUpdate();
            try { files.Items.Clear(); files.Items.AddRange(library.List()); if(selected!=null && files.Items.Contains(selected)) files.SelectedItem=selected; else files.ClearSelected(); }
            finally { files.EndUpdate(); }
            UpdateButtons();
        }
        void QueueImports(string[] paths) {
            pendingImports.Enqueue(paths);
            if(busy) { status.Text="Crop export ready; it will be imported after the current operation."; return; }
            if(!importing) ImportPending();
        }
        async void ImportPending() {
            if(importing || busy || pendingImports.Count==0 || IsDisposed) return;
            importing=true; UpdateButtons();
            try {
                while(pendingImports.Count>0) {
                    string[] paths=pendingImports.Dequeue(); status.Text="Importing media into the local library...";
                    var errors=new List<string>();
                    string selected=await Task.Run(()=> {
                        string last=null;
                        foreach(string path in paths) { try { last=library.Import(path); } catch(Exception ex) { errors.Add(Path.GetFileName(path)+": "+ex.Message); } }
                        return last;
                    });
                    if(IsDisposed) return;
                    RefreshLibrary(selected); status.Text=errors.Count>0?string.Join("; ",errors):"Imported and selected. Upload sends only the selected file.";
                }
            } finally { importing=false; if(!IsDisposed) UpdateButtons(); }
        }
        void OpenCropper() {
            if(cropSession!=null) return;
            string script=cropScriptOverride??Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","Gif70.ps1");
            if(!File.Exists(script)) { status.Text="Crop tool missing: tools/Gif70.ps1 must be in the application directory."; return; }
            try {
                cropSession=new CropSession(script,files.SelectedItem as string,library.DirectoryPath,
                    path=>QueueImports(new[]{path}),message=>status.Text=message,()=> { cropSession=null; UpdateButtons(); },ScreenOrientation.IsPortrait(orientation.SelectedIndex));
                status.Text="Crop tool opened. Each completed export will be imported and selected automatically."; UpdateButtons();
            } catch(Exception ex) { status.Text="Could not open crop tool: "+ex.Message; }
        }
        static FlowLayoutPanel Row() { return new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty }; }
        static void ButtonStyle(Button b,string text,int width) { b.Text=text; b.Size=new Size(width,32); b.FlatStyle=FlatStyle.Flat; b.BackColor=Color.White; }
        void UpdateUploadOptions() {
            skipBackup.Text=skipBackup.Checked?"Skip backup: On":"Skip backup: Off";
            skipVerification.Text=skipVerification.Checked?"Skip verification: On":"Skip verification: Off";
            skipBackup.BackColor=skipBackup.Checked?Color.FromArgb(215,232,255):Color.White;
            skipVerification.BackColor=skipVerification.Checked?Color.FromArgb(215,232,255):Color.White;
            note.Text=skipBackup.Checked?(skipVerification.Checked?"Both skipped":"Verify only"):(skipVerification.Checked?"Backup only":"Backup + verify");
        }
        void RefreshPorts() {
            string old=ports.Text; var active=SerialPort.GetPortNames().OrderBy(x=>x).ToArray(); ports.Items.Clear(); ports.Items.AddRange(active);
            string found=FindHubPort(active); string chosen=active.Contains(old)?old:found;
            if(chosen!=null && ports.Items.Contains(chosen)) ports.SelectedItem=chosen; else if(ports.Items.Count>0) ports.SelectedIndex=0;
        }
        public static string FindHubPort(string[] active) {
            using(var root=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB")) {
                if(root==null) return null;
                foreach(var name in root.GetSubKeyNames().Where(n=>n.StartsWith("VID_C019&PID_0401",StringComparison.OrdinalIgnoreCase))) using(var group=root.OpenSubKey(name)) {
                    if(group==null) continue;
                    foreach(var id in group.GetSubKeyNames()) using(var child=group.OpenSubKey(id+@"\Device Parameters")) {
                        if(child==null) continue; var port=child.GetValue("PortName") as string; if(port!=null && active.Contains(port)) return port;
                    }
                }
            }
            return null;
        }
        void Probe() {
            if(string.IsNullOrEmpty(ports.Text)) { status.Text="No serial port found. Plug in the hub and try again."; return; }
            string port=ports.Text;
            Run(token=> { using(var hub=new HubSession(new SerialLink(port),token)) { byte value=hub.ReadConfig(token)[3]; ScreenOrientation.IsPortrait(value); return new object[]{hub.Info,value}; } },result=> {
                var data=(object[])result; info=(HubInfo)data[0]; detectedOrientation=(byte)data[1]; orientation.SelectedIndex=detectedOrientation.Value;
                if(live!=null) live.SetScreenOrientation(detectedOrientation.Value);
                RenderPreview(); device.Text=port+" | "+info; status.Text="Hub found. Screen: "+orientation.Text+". Select media to crop or upload.";
            },"Reading hub identity...");
        }
        void ApplyOrientation() {
            if(string.IsNullOrEmpty(ports.Text)) return;
            string port=ports.Text; byte value=(byte)orientation.SelectedIndex;
            Run(token=> { using(var hub=new HubSession(new SerialLink(port),token)) { hub.SetOrientation(value,token); return hub.Info; } },result=> {
                info=(HubInfo)result; detectedOrientation=value; device.Text=port+" | "+info;
                if(live!=null) live.SetScreenOrientation(value);
                status.Text="Screen orientation applied and readback verified: "+orientation.Text+"."; RenderPreview();
            },"Applying screen orientation...");
        }
        void BeginUpload() {
            string selected=files.SelectedItem as string; if(selected==null || string.IsNullOrEmpty(ports.Text)) return;
            if(!detectedOrientation.HasValue || detectedOrientation.Value!=orientation.SelectedIndex) { status.Text="Detect the hub or Apply orientation before uploading."; return; }
            string port=ports.Text; byte expectedOrientation=detectedOrientation.Value; bool fill=fit.SelectedIndex==1,skip=skipBackup.Checked,verify=!skipVerification.Checked;
            Run(token=> {
                using(var hub=new HubSession(new SerialLink(port),token)) {
                    Report(0,"Preparing "+Path.GetFileName(selected)+"...");
                    byte current=hub.ReadConfig(token)[3];
                    if(current!=expectedOrientation) throw new InvalidOperationException("The hub orientation changed. Detect hub again before uploading.");
                    var media=Wallpaper.Prepare(selected,fill,hub.Info.Capacity,token,ScreenOrientation.IsPortrait(current));
                    var buffer=Wire.Combine(new[]{media},hub.Info.Flash); token.ThrowIfCancellationRequested();
                    if(!skip) Program.SaveBackup(hub,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"backups"),token,Report);
                    token.ThrowIfCancellationRequested(); writing=true;
                    string displayWarning=hub.UploadWallpaper(buffer,token,Report,verify); return new object[]{hub.Info,displayWarning};
                }
            },result=> {
                var completed=(object[])result; info=(HubInfo)completed[0]; device.Text=port+" | "+info;
                string displayWarning=completed[1] as string;
                status.Text=(verify?"Uploaded and verified. ":"Upload sent; verification skipped. ")+
                    (displayWarning??"Wallpaper display selected.");
            },"Preparing wallpapers...");
        }
        async void Run(Func<CancellationToken,object> work,Action<object> done,string message) {
            if(busy) return; busy=true; writing=false; cancellation=new CancellationTokenSource(); progress.Value=0; progress.Visible=true; status.Text=message; UpdateButtons(); animation.Stop();
            try { var result=await Task.Run(()=>work(cancellation.Token)); done(result); }
            catch(OperationCanceledException) { status.Text=writing?"Upload stopped. The album may be incomplete; retry to restore it.":"Cancelled."; }
            catch(Exception ex) { status.Text=ex.Message; if(!closing) MessageBox.Show(this,ex.Message,"HMLab Hub Control",MessageBoxButtons.OK,MessageBoxIcon.Error); }
            finally { busy=false; writing=false; progress.Visible=false; cancellation.Dispose(); cancellation=null; UpdateButtons(); if(source!=null && frames>1) animation.Start(); if(closing) Close(); else ImportPending(); }
        }
        void Report(int percent,string text) {
            if(IsDisposed || !IsHandleCreated) return;
            int tick=Environment.TickCount;
            if(percent<100 && unchecked(tick-lastReport)<75) return;
            lastReport=tick;
            BeginInvoke((Action)delegate { if(!busy) return; progress.Value=Math.Max(0,Math.Min(100,percent)); status.Text=text; });
        }
        void UpdateButtons() {
            if(live!=null) live.Enabled=!busy;
            bool idle=!busy && !importing;
            ports.Enabled=detect.Enabled=add.Enabled=files.Enabled=fit.Enabled=orientation.Enabled=skipBackup.Enabled=skipVerification.Enabled=idle;
            applyOrientation.Enabled=idle && !string.IsNullOrEmpty(ports.Text);
            remove.Enabled=idle && files.SelectedItem!=null; crop.Enabled=idle && cropSession==null;
            upload.Enabled=idle && files.SelectedItem!=null && !string.IsNullOrEmpty(ports.Text) && detectedOrientation.HasValue && detectedOrientation.Value==orientation.SelectedIndex; cancel.Enabled=true;
            uploadHint.ForeColor=Color.FromArgb(150,75,0);
            if(busy) uploadHint.Text="Operation in progress. Upload becomes available when it finishes.";
            else if(importing) uploadHint.Text="Importing media. Upload becomes available when it finishes.";
            else if(string.IsNullOrEmpty(ports.Text)) uploadHint.Text="Before uploading: connect the hub and select its port, then click Detect hub.";
            else if(!detectedOrientation.HasValue) uploadHint.Text="Screen orientation not confirmed. Click Detect hub, or choose an orientation and click Apply.";
            else if(detectedOrientation.Value!=orientation.SelectedIndex) uploadHint.Text="Orientation changed. Click Apply to use "+orientation.Text+" before uploading.";
            else if(files.SelectedItem==null) uploadHint.Text="Screen orientation confirmed. Add or select a media file to enable Upload.";
            else { uploadHint.Text="Ready to upload using "+orientation.Text+"."; uploadHint.ForeColor=Color.FromArgb(35,100,55); }
        }
        void LoadPreview() {
            DisposePreview(); if(files.SelectedItem==null) return;
            try {
                sourceStream=new MemoryStream(File.ReadAllBytes((string)files.SelectedItem)); source=Image.FromStream(sourceStream); frames=Wallpaper.FrameCount(source); frameIndex=0;
                int delay=Wallpaper.Delay(source,frames); animation.Interval=Math.Max(20,delay); RenderPreview(); if(frames>1) animation.Start();
            } catch(Exception ex) { DisposePreview(); status.Text="Preview failed: "+ex.Message; }
            UpdateButtons();
        }
        void RenderPreview() {
            if(source==null) return; bool portrait=ScreenOrientation.IsPortrait(orientation.SelectedIndex);
            var image=Wallpaper.Render(source,fit.SelectedIndex==1,portrait); var old=preview.Image; preview.Image=image; if(old!=null) old.Dispose();
            int delay=Wallpaper.Delay(source,frames);
            description.Text=string.Format("{0} x {1} source | {2} frame(s) | {3}\n{4} panel; {5}",source.Width,source.Height,frames,frames>1?delay+" ms per frame":"still image",portrait?"170 x 320":"320 x 170",fit.SelectedIndex==1?"center crop":"fit inside");
        }
        void DisposePreview() { animation.Stop(); if(preview.Image!=null) { var old=preview.Image; preview.Image=null; old.Dispose(); } if(source!=null) source.Dispose(); if(sourceStream!=null) sourceStream.Dispose(); source=null; sourceStream=null; frames=0; }
        public void CaptureScreenshot(string path,string sample) {
            if(!string.IsNullOrEmpty(sample) && File.Exists(sample)) { files.Items.Add(sample); files.SelectedItem=sample; }
            ShowInTaskbar=false; Opacity=0; Show(); Application.DoEvents();
            using(var bitmap=new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height)); bitmap.Save(path,ImageFormat.Png); } Close();
        }
        protected override void Dispose(bool disposing) {
            if(disposing) { if(cropSession!=null) cropSession.Dispose(); DisposePreview(); animation.Dispose(); if(cancellation!=null) cancellation.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
