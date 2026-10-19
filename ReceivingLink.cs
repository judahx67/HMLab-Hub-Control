// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace HMLabHub {
    public sealed class SerialLink : IHubLink {
        readonly IHubLink source;
        readonly object gate=new object(); readonly Queue<byte[]> replies=new Queue<byte[]>();
        readonly CancellationTokenSource stop=new CancellationTokenSource(); readonly Thread receiver;
        Exception failure; bool disposed; long received,telemetry;
        public long ReceivedPackets { get { return Interlocked.Read(ref received); } }
        public long TelemetryPackets { get { return Interlocked.Read(ref telemetry); } }
        internal int PendingReplies { get { lock(gate) return replies.Count; } }
        public SerialLink(string name) : this(new RawSerialLink(name)) { }
        internal SerialLink(IHubLink source) {
            this.source=source;
            receiver=new Thread(Receive) { IsBackground=true,Name="HMLab serial receiver" };
            try { receiver.Start(); } catch { stop.Dispose(); source.Dispose(); throw; }
        }
        void Receive() {
            try {
                while(!stop.IsCancellationRequested) {
                    byte[] packet;
                    try { packet=source.Read(200,stop.Token); }
                    catch(TimeoutException) { if(stop.Token.WaitHandle.WaitOne(2)) return; continue; }
                    Interlocked.Increment(ref received);
                    // Power (9) and device log (10) arrive without a host request. Keep
                    // draining them even while the UI is idle: the device stalls if its
                    // outgoing serial buffer fills. No command handler needs these here.
                    if(packet[0]==9 || packet[0]==10) { Interlocked.Increment(ref telemetry); continue; }
                    lock(gate) {
                        if(disposed) return;
                        if(failure==null) {
                            if(replies.Count>=1024) failure=new IOException("Hub response queue overflowed; reconnect before retrying the operation.");
                            else replies.Enqueue(packet);
                        }
                        Monitor.PulseAll(gate);
                    }
                }
            } catch(OperationCanceledException) { }
            catch(Exception ex) { lock(gate) { if(!disposed) failure=ex; Monitor.PulseAll(gate); } }
        }
        void Check() {
            if(disposed) throw new ObjectDisposedException("SerialLink");
            if(failure!=null) throw new IOException("Hub serial receive failed: "+failure.Message,failure);
        }
        public void Write(byte[] packet) { lock(gate) Check(); source.Write(packet); }
        public byte[] Read(int timeout,CancellationToken token) {
            var clock=Stopwatch.StartNew();
            using(token.Register(()=> { lock(gate) Monitor.PulseAll(gate); })) {
                lock(gate) {
                    while(true) {
                        token.ThrowIfCancellationRequested(); Check();
                        if(replies.Count>0) return replies.Dequeue();
                        int remaining=timeout-(int)clock.ElapsedMilliseconds;
                        if(remaining<=0) throw new TimeoutException("No hub command response. Check the USB connection and close other controllers.");
                        Monitor.Wait(gate,remaining);
                    }
                }
            }
        }
        public void DiscardInput() {
            // Only the receiver touches the serial parser. Clearing queued replies does
            // not truncate an in-flight packet or stop status reception.
            lock(gate) { Check(); replies.Clear(); }
        }
        public void Dispose() {
            lock(gate) { if(disposed) return; disposed=true; Monitor.PulseAll(gate); }
            stop.Cancel();
            if(!receiver.Join(1000)) { source.Dispose(); receiver.Join(1000); }
            else source.Dispose();
            if(!receiver.IsAlive) stop.Dispose();
        }
    }
}
