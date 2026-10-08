"""Run the real OnSend method and SendQueue under a forced sender interleaving."""
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def check(source, expect_stalled):
    method = source[source.index('        private void OnSend(Task<int> task)'):]
    method = method[:method.index('        public static void Pause()')]
    harness = r'''
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
namespace Server {
public class BufferPool {
 readonly int size;
 public BufferPool(string name,int count,int bytes){size=bytes;}
 public byte[] AcquireBuffer(){return new byte[size];}
 public void ReleaseBuffer(byte[] b){}
 public void Free(){}
}
public static class Core { public static long TickCount {get{return 0;}} }
}
namespace Server.Network {
class FakeSocket {
 public int Sends;
 public Task<int> SendAsync(ArraySegment<byte> data,SocketFlags flags) {
  Interlocked.Increment(ref Sends);
  return new TaskCompletionSource<int>().Task;
 }
}
class Probe {
 readonly object _SendLock=new object();
 readonly SendQueue m_SendQueue=new SendQueue();
 readonly FakeSocket Socket=new FakeSocket();
 bool _Sending=true;
 long m_NextCheckActivity;
 int m_CoalesceSleep=-1;
 bool disposed;
 void Dispose(bool flush){disposed=true;}
 void TraceException(Exception e){throw new Exception("unexpected send failure",e);}
 METHOD
 public static void Main(string[] args) {
  var p=new Probe();
  bool old=bool.Parse(args[0]);
  p.m_SendQueue.Enqueue(new byte[512],512);
  Task callback;
  lock(p._SendLock) {
   callback=Task.Run(()=>p.OnSend(Task.FromResult(512)));
   // Old callback can remove the completed gram while another sender holds
   // _SendLock. Fixed callback must wait until that sender releases the lock.
   bool emptied=SpinWait.SpinUntil(()=>p.m_SendQueue.IsEmpty,old?2000:150);
   if(emptied!=old)throw new Exception("unexpected handoff ordering");
   lock(p.m_SendQueue) { p.m_SendQueue.Enqueue(new byte[512],512); }
   if(!p._Sending)throw new Exception("callback changed state inside sender lock");
  }
  if(!callback.Wait(2000))throw new Exception("callback deadlocked");
  bool stalled=!p._Sending && !p.m_SendQueue.IsEmpty && !p.m_SendQueue.IsFlushReady;
  if(stalled!=old || p.disposed || p.Socket.Sends!=(old?0:1))
   throw new Exception("wrong queue outcome");
  Console.WriteLine(old?"old: stranded queue reproduced":"fixed: next gram sent");
 }
}
}
'''.replace('METHOD', method)
    with tempfile.TemporaryDirectory() as tmp:
        p = Path(tmp)
        (p/'Probe.cs').write_text(harness)
        subprocess.run(['mcs', '-out:'+str(p/'probe.exe'), str(p/'Probe.cs'),
                        str(ROOT/'Server/Network/SendQueue.cs')], check=True)
        subprocess.run(['mono', str(p/'probe.exe'), str(expect_stalled)], check=True)


if __name__ == '__main__':
    original = subprocess.check_output(
        ['git', 'show', '430401dbb26d8551481f864b69b744c1d101861e:Server/Network/NetState.cs'], cwd=ROOT, text=True)
    check(original, True)
    check((ROOT/'Server/Network/NetState.cs').read_text(), False)
