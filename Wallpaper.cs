// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HMLabHub {
    public sealed class PreparedMedia {
        public byte[] Pixels;
        public int Frames,Delay;
        public int Width=320,Height=170;
        public bool Portrait;
    }
    public static class Wallpaper {
        public static int FrameCount(Image image) {
            foreach(var id in image.FrameDimensionsList) if(id==FrameDimension.Time.Guid) return image.GetFrameCount(FrameDimension.Time);
            return 1;
        }
        public static int Delay(Image image,int count) {
            if(count<=1) return 0;
            try {
                byte[] d=image.GetPropertyItem(0x5100).Value; long total=0;
                for(int i=0;i<count;i++) total+=Math.Max(2,BitConverter.ToInt32(d,4*i))*10L;
                return (int)Math.Min(65535,Math.Max(20,Math.Round(total/(double)count)));
            } catch(ArgumentException) { return 100; }
        }
        public static Bitmap Render(Image image,bool fill,bool portrait=false) {
            return RenderAt(image,fill,portrait?170:320,portrait?320:170);
        }
        public static Bitmap RenderAt(Image image,bool fill,int width,int height) {
            var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb);
            using(var g=Graphics.FromImage(bitmap)) {
                g.Clear(Color.Black); g.CompositingMode=CompositingMode.SourceOver;
                g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                double scale=fill?Math.Max((double)width/image.Width,(double)height/image.Height):Math.Min((double)width/image.Width,(double)height/image.Height);
                float w=(float)(image.Width*scale),h=(float)(image.Height*scale);
                using(var attrs=new ImageAttributes()) {
                    attrs.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(image,new Rectangle((int)Math.Round((width-w)/2),(int)Math.Round((height-h)/2),
                        Math.Max(1,(int)Math.Round(w)),Math.Max(1,(int)Math.Round(h))),0,0,image.Width,image.Height,GraphicsUnit.Pixel,attrs);
                }
            }
            return bitmap;
        }
        public static byte[] Encode565(Bitmap bitmap) {
            int width=bitmap.Width,height=bitmap.Height;
            var pixels=new byte[checked(width*height*2)];
            var locked=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
            try {
                var row=new byte[width*3];
                for(int y=0;y<height;y++) {
                    Marshal.Copy(IntPtr.Add(locked.Scan0,y*locked.Stride),row,0,row.Length);
                    for(int x=0;x<width;x++) {
                        int i=x*3; ushort value=(ushort)(((row[i+2]&248)<<8)|((row[i+1]&252)<<3)|(row[i]>>3));
                        int p=(y*width+x)*2; pixels[p]=(byte)(value>>8); pixels[p+1]=(byte)value;
                    }
                }
            } finally { bitmap.UnlockBits(locked); }
            return pixels;
        }
        public static Bitmap Preview565(PreparedMedia media,int frame) {
            int width=media.Width,height=media.Height;
            var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb);
            var bits=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
            try {
                var row=new byte[width*3]; int start=checked(frame*width*height*2);
                for(int y=0;y<height;y++) {
                    for(int x=0;x<width;x++) {
                        int p=start+(y*width+x)*2,v=(media.Pixels[p]<<8)|media.Pixels[p+1];
                        int r=(v>>11)&31,g=(v>>5)&63,b=v&31;
                        row[x*3]=(byte)((b<<3)|(b>>2)); row[x*3+1]=(byte)((g<<2)|(g>>4)); row[x*3+2]=(byte)((r<<3)|(r>>2));
                    }
                    Marshal.Copy(row,0,IntPtr.Add(bits.Scan0,y*bits.Stride),row.Length);
                }
            } finally { bitmap.UnlockBits(bits); }
            // Upload pixels are always native landscape; show upright artwork as mounted.
            if(media.Portrait) bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
            return bitmap;
        }
        public static PreparedMedia Prepare(string path,bool fill,int frameBudget,CancellationToken token,bool portrait=false) {
            // Keep the source stream alive; GDI+ composes GIF subframes and disposal modes.
            using(var stream=File.OpenRead(path)) using(var image=Image.FromStream(stream)) {
                int frames=FrameCount(image);
                if(frames>frameBudget) throw new InvalidDataException(Path.GetFileName(path)+" has "+frames+" frames; remaining hub capacity is "+frameBudget+". Trim it with Gif70 first.");
                var data=new byte[checked(frames*108800)]; int delay=Delay(image,frames);
                for(int i=0;i<frames;i++) {
                    token.ThrowIfCancellationRequested();
                    if(frames>1) image.SelectActiveFrame(FrameDimension.Time,i);
                    using(var bitmap=Render(image,fill,portrait)) {
                        // Firmware's wallpaper/LCD address window remains 320 x 170.
                        // screen_dir 2 uses normal scan; 3 adds the hardware 180-degree flip.
                        // Both sideways settings therefore need a counterclockwise quarter turn.
                        if(portrait) bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                        var encoded=Encode565(bitmap); Array.Copy(encoded,0,data,i*108800,108800);
                    }
                }
                return new PreparedMedia { Pixels=data,Frames=frames,Delay=delay,Portrait=portrait };
            }
        }
    }
}
