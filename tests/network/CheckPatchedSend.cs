using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
class Check {
 static void Main(string[] args){
 var asm=Assembly.LoadFrom(args[0]);var t=asm.GetType("Server.Network.NetState");var qt=asm.GetType("Server.Network.SendQueue");
 var ns=FormatterServices.GetUninitializedObject(t);var q=Activator.CreateInstance(qt);var gate=new object();var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
 t.GetField("_SendLock",flags).SetValue(ns,gate);t.GetField("m_SendQueue",flags).SetValue(ns,q);t.GetField("_Sending",flags).SetValue(ns,true);
 var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var peer=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);peer.Connect(listener.LocalEndpoint);var sender=listener.AcceptSocket();listener.Stop();
 t.GetProperty("Socket",flags).SetValue(ns,sender,null);peer.ReceiveTimeout=2000;
 var enqueue=qt.GetMethod("Enqueue",new[]{typeof(byte[]),typeof(int)});var empty=qt.GetProperty("IsEmpty");var on=t.GetMethod("OnSend",flags);
 enqueue.Invoke(q,new object[]{new byte[512],512});Task callback;
 lock(gate){callback=Task.Run(()=>on.Invoke(ns,new object[]{Task.FromResult(512)}));if(SpinWait.SpinUntil(()=>(bool)empty.GetValue(q,null),150))throw new Exception("Callback dequeued outside send lock");enqueue.Invoke(q,new object[]{new byte[512],512});}
 if(!callback.Wait(2000))throw new Exception("deadlock");var bytes=new byte[512];int total=0;while(total<512)total+=peer.Receive(bytes,total,512-total,SocketFlags.None);
 Console.WriteLine("Deployed assembly callback delivered queued 512 bytes after contended handoff");
 }
}
