// SPDX-License-Identifier: AGPL-3.0-or-later
// HID frame layout follows the HM SignalRGB script; see PROTOCOL.md.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HMLabHub {
    public sealed class HidEndpoint {
        public string Path; public ushort Usage,Page,OutputLength; public string ReportIds;
        public override string ToString() { return string.Format("usage page {0:X4}, usage {1:X4}, output {2} bytes, report IDs {3} | {4}",Page,Usage,OutputLength,ReportIds,Path); }
    }
    public static class HidNative {
        [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Size; public ushort Vendor,Product,Version; }
        [StructLayout(LayoutKind.Sequential)] struct Caps {
            public ushort Usage,UsagePage,InputLength,OutputLength,FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray,SizeConst=17)] public ushort[] Reserved;
            public ushort LinkNodes,InputButtons,InputValues,InputIndices,OutputButtons,OutputValues,OutputIndices,FeatureButtons,FeatureValues,FeatureIndices;
        }
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle handle,ref Attributes attributes);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle handle,out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data,out Caps caps);
        [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type,IntPtr caps,ref ushort count,IntPtr data);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid,IntPtr enumerator,IntPtr parent,uint flags);
        [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,IntPtr device,ref Guid guid,uint index,ref InterfaceData data);
        [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set,ref InterfaceData data,IntPtr detail,uint size,out uint required,IntPtr device);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        public static List<HidEndpoint> Find() {
            Guid guid; HidD_GetHidGuid(out guid); var result=new List<HidEndpoint>();
            IntPtr set=SetupDiGetClassDevs(ref guid,IntPtr.Zero,IntPtr.Zero,0x12);
            if(set==new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                for(uint i=0;;i++) {
                    var item=new InterfaceData { Size=Marshal.SizeOf(typeof(InterfaceData)) };
                    if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref item)) break;
                    uint required; SetupDiGetDeviceInterfaceDetail(set,ref item,IntPtr.Zero,0,out required,IntPtr.Zero);
                    IntPtr detail=Marshal.AllocHGlobal((int)required);
                    try {
                        Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);
                        if(!SetupDiGetDeviceInterfaceDetail(set,ref item,detail,required,out required,IntPtr.Zero)) continue;
                        string path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                        if(path.IndexOf("vid_c019&pid_0401",StringComparison.OrdinalIgnoreCase)<0) continue;
                        using(var handle=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                            if(handle.IsInvalid) continue;
                            var attributes=new Attributes { Size=Marshal.SizeOf(typeof(Attributes)) };
                            if(!HidD_GetAttributes(handle,ref attributes) || attributes.Vendor!=0xc019 || attributes.Product!=0x0401) continue;
                            IntPtr data; if(!HidD_GetPreparsedData(handle,out data)) continue;
                            try {
                                Caps caps;
                                if(HidP_GetCaps(data,out caps)>=0) {
                                    string ids="none"; ushort count=caps.OutputValues;
                                    if(count>0) {
                                        IntPtr values=Marshal.AllocHGlobal(count*72);
                                        try {
                                            if(HidP_GetValueCaps(1,values,ref count,data)>=0) {
                                                var parts=new List<string>(); for(int v=0;v<count;v++) parts.Add(Marshal.ReadByte(values,v*72+2).ToString("X2")); ids=string.Join(",",parts);
                                            }
                                        } finally { Marshal.FreeHGlobal(values); }
                                    }
                                    result.Add(new HidEndpoint { Path=path,Usage=caps.Usage,Page=caps.UsagePage,OutputLength=caps.OutputLength,ReportIds=ids });
                                }
                            }
                            finally { HidD_FreePreparsedData(data); }
                        }
                    } finally { Marshal.FreeHGlobal(detail); }
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }
    }
    public interface ILiveSink : IDisposable { void Send(byte[] rgb,CancellationToken token); }
    public sealed class HidLiveSink : ILiveSink {
        readonly FileStream stream; readonly int length;
        public HidLiveSink() {
            HidEndpoint target=null;
            foreach(var endpoint in HidNative.Find()) if(endpoint.Page==0xffc0 && endpoint.Usage==0x0c00 && endpoint.Path.IndexOf("mi_00",StringComparison.OrdinalIgnoreCase)>=0) {
                if(target!=null) throw new IOException("Multiple HMLab live endpoints found; connect one hub at a time."); target=endpoint;
            }
            if(target==null) throw new IOException("HMLab live HID endpoint was not found. Reconnect the hub and disable its SignalRGB controller.");
            if((target.OutputLength!=256 && target.OutputLength!=257) || target.ReportIds!="05") throw new IOException("Unsupported live HID report layout: "+target);
            length=target.OutputLength;
            var handle=HidNative.CreateFile(target.Path,0x40000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(handle.IsInvalid) { int error=Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error,"Cannot open the hub's live output endpoint."); }
            // Disable stream buffering: each Windows write must remain exactly one HID report.
            try { stream=new FileStream(handle,FileAccess.Write,1,true); } catch { handle.Dispose(); throw; }
        }
        public static byte[][] Reports(byte[] rgb) {
            if(rgb.Length!=32*17*3) throw new ArgumentException("Expected a 32 x 17 RGB888 frame.");
            int count=(rgb.Length+251)/252; var reports=new byte[count][];
            for(int i=0;i<count;i++) {
                var packet=new byte[256]; packet[0]=5; packet[1]=1; packet[2]=(byte)count; packet[3]=(byte)i;
                Array.Copy(rgb,i*252,packet,4,Math.Min(252,rgb.Length-i*252)); reports[i]=packet;
            }
            return reports;
        }
        public void Send(byte[] rgb,CancellationToken token) {
            foreach(var report in Reports(rgb)) {
                token.ThrowIfCancellationRequested();
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    timeout.CancelAfter(2000);
                    var output=new byte[length]; Array.Copy(report,output,report.Length); // Descriptor ID 05; 257-byte reports have one trailing pad byte.
                    try { stream.WriteAsync(output,0,length,timeout.Token).GetAwaiter().GetResult(); }
                    catch(OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new IOException("HMLab live HID write timed out."); }
                }
            }
        }
        public void Dispose() { stream.Dispose(); }
    }
}
