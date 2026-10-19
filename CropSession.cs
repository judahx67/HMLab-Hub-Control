// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HMLabHub {
    // Created only when Crop tool is clicked. No PowerShell engine or poller exists
    // during ordinary startup; export receipts are published atomically by Gif70.
    public sealed class CropSession : IDisposable {
        readonly Process process; readonly Task<string> errorOutput; readonly Timer poll=new Timer { Interval=350 };
        readonly Action<string> exported; readonly Action<string> error; readonly Action finished;
        readonly HashSet<string> handled=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly string receipts; bool disposed;
        public CropSession(string script,string input,string outputDirectory,Action<string> exported,Action<string> error,Action finished,bool portrait=false) {
            this.exported=exported; this.error=error; this.finished=finished;
            receipts=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputDirectory)),"state","crop-exports",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(receipts);
            var args=new List<string>{"-NoProfile","-STA","-ExecutionPolicy","Bypass","-File",script,"-ExportDirectory",outputDirectory,"-ResultDirectory",receipts};
            if(!string.IsNullOrEmpty(input)) { args.Add("-Path"); args.Add(input); }
            if(portrait) args.Add("-Portrait");
            var info=new ProcessStartInfo {
                FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments=JoinArguments(args),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true
            };
            try { process=Process.Start(info); errorOutput=process.StandardError.ReadToEndAsync(); poll.Tick+=delegate { Tick(); }; poll.Start(); }
            catch { poll.Dispose(); Directory.Delete(receipts); throw; }
        }
        public static string JoinArguments(IEnumerable<string> arguments) {
            var result=new List<string>();
            foreach(string value in arguments) {
                var quoted=new StringBuilder("\""); int slashes=0;
                foreach(char c in value) {
                    if(c=='\\') { slashes++; continue; }
                    quoted.Append('\\',c=='"'?slashes*2+1:slashes); slashes=0; quoted.Append(c);
                }
                quoted.Append('\\',slashes*2); quoted.Append('"'); result.Add(quoted.ToString());
            }
            return string.Join(" ",result);
        }
        internal static string ReadReceipt(string path) {
            string output=File.ReadAllText(path,Encoding.UTF8);
            if(!Path.IsPathRooted(output) || !string.Equals(Path.GetExtension(output),".gif",StringComparison.OrdinalIgnoreCase) || !File.Exists(output))
                throw new InvalidDataException("Cropper did not return an existing GIF.");
            return Path.GetFullPath(output);
        }
        void Tick() {
            if(disposed) return;
            foreach(string receipt in Directory.GetFiles(receipts,"*.result")) {
                if(!handled.Add(receipt)) continue;
                try { exported(ReadReceipt(receipt)); }
                catch(Exception ex) { error("Crop import: "+ex.Message); }
            }
            if(process.HasExited) {
                int code=process.ExitCode; string detail=errorOutput.GetAwaiter().GetResult().Trim(); Dispose();
                if(code!=0) error("Crop tool closed with an error ("+code+"). "+detail); finished();
            }
        }
        public void Dispose() {
            if(disposed) return; disposed=true; poll.Stop(); poll.Dispose();
            bool exited=process.HasExited; process.Dispose();
            // Closing the hub app does not kill an independently open crop window.
            if(exited) {
                foreach(string file in Directory.GetFiles(receipts)) File.Delete(file);
                Directory.Delete(receipts);
            }
        }
    }
}
