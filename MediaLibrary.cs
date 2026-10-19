// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace HMLabHub {
    public sealed class MediaLibrary {
        public readonly string DirectoryPath;
        public MediaLibrary(string directory) { DirectoryPath=Path.GetFullPath(directory); }
        public static bool Supports(string path) {
            return new[]{".gif",".png",".jpg",".jpeg",".bmp"}.Contains(Path.GetExtension(path).ToLowerInvariant());
        }
        public string[] List() {
            if(!Directory.Exists(DirectoryPath)) return new string[0];
            return Directory.GetFiles(DirectoryPath).Where(p=>Supports(p) && !Path.GetFileName(p).StartsWith(".gif70-",StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase).ToArray();
        }
        public string Import(string source) {
            source=Path.GetFullPath(source);
            if(!Supports(source)) throw new InvalidDataException("Choose a GIF, PNG, JPG, or BMP image.");
            using(var stream=File.OpenRead(source)) using(var image=Image.FromStream(stream)) {
                if(image.Width<1 || image.Height<1) throw new InvalidDataException("The imported image is invalid.");
            }
            Directory.CreateDirectory(DirectoryPath);
            if(string.Equals(Path.GetDirectoryName(source),DirectoryPath,StringComparison.OrdinalIgnoreCase)) return source;
            string staged=Path.Combine(DirectoryPath,".import-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                File.Copy(source,staged,false);
                string name=Path.GetFileNameWithoutExtension(source),extension=Path.GetExtension(source);
                for(int index=1;;index++) {
                    string target=Path.Combine(DirectoryPath,name+(index==1?"":"_"+index)+extension);
                    if(File.Exists(target)) { if(SameBytes(staged,target)) return target; continue; }
                    try { File.Move(staged,target); return target; }
                    catch(IOException) { if(!File.Exists(target)) throw; }
                }
            } finally { if(File.Exists(staged)) File.Delete(staged); }
        }
        static bool SameBytes(string a,string b) {
            if(new FileInfo(a).Length!=new FileInfo(b).Length) return false;
            using(var hash=SHA256.Create()) using(var first=File.OpenRead(a)) using(var second=File.OpenRead(b))
                return hash.ComputeHash(first).SequenceEqual(hash.ComputeHash(second));
        }
        public void Remove(string path) {
            path=Path.GetFullPath(path);
            if(!string.Equals(Path.GetDirectoryName(path),DirectoryPath,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only a library copy can be removed.");
            File.Delete(path);
        }
    }
}
