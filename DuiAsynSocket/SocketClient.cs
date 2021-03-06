using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
namespace DuiAsynSocket
{
    /// <summary>
    /// Implements the connection logic for the socket client.
    /// </summary>
    public sealed class SocketClient : IDisposable
    {
        #region Fields

        #endregion

        #region Properties

        /// <summary>
        /// 是否使用心跳验证,启用后发送的第一个报文为验证报文
        /// </summary>
        public bool IsUseHeartBeatCertificate { get; set; } = false;

        private int _bufferSize = 1024 * 4;
        /// <summary>
        /// socket收发缓存大小,默认4k
        /// </summary>
        public int BufferSize
        {
            get
            {
                return _bufferSize;
            }
            set
            {
                if (value < 1024)
                    _bufferSize = 1024;
                else
                    _bufferSize = value;
            }
        }

        /// <summary>
        /// The socket used to send/receive messages.
        /// </summary>
        private Socket _socket;

        public Socket Socket
        {
            get
            {
                return _socket;
            }
        }

        public bool IsConnected
        {
            get
            {
                return Socket == null ? false : Socket.Connected;
            }
        }

        /// <summary>
        /// 客户端连接状态变更事件
        /// </summary>
        public event Action<ConnStatusChangeArgs> OnConnChangeEvent;
        /// <summary>
        /// 数据接收事件
        /// </summary>
        public event Action<DataReceivedArgs> OnReceivedEvent;
        #endregion

        #region Constructors
        /// <summary>
        /// Create an uninitialized client instance.  
        /// To start the send/receive processing
        /// call the Connect method followed by SendReceive method.
        /// </summary>
        /// <param name="hostName">Name of the host where the listener is running.</param>
        /// <param name="port">Number of the TCP port from the listener.</param>
        public SocketClient()
        {
            //_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        #endregion

        #region HeartBeats
        private int _heartBeatSpan = 60;
        private System.Timers.Timer _heartBeatsTimer;
        /// <summary>
        /// 心跳时间(s)最低设置30s，默认1分钟
        /// </summary>
        public int HeartBeatSpan
        {
            get
            {
                return _heartBeatSpan;
            }
            set
            {
                if (_heartBeatSpan < 30)
                    _heartBeatSpan = 30;
                else
                    _heartBeatSpan = value;
            }
        }
        /// <summary>
        /// 是否开启心跳,连接前设置
        /// </summary>
        public bool HeartBeatsEnable { get; set; } = true;
        /// <summary>
        /// 是否把网络字节顺序转为本地字节顺序
        /// </summary>
        public bool NetByteOrder { get; set; }
        /// <summary>
        /// 上次数据交换时间
        /// </summary>
        private int _lastExchangeTime = Environment.TickCount;
        private void InitHeartBeatsTimer()
        {
            if (HeartBeatsEnable == false)
                return;
            _heartBeatsTimer = new System.Timers.Timer(3 * 1000);
            var time = HeartBeatSpan * 1000;
            _heartBeatsTimer.Elapsed += (s, e) =>
            {
                if (Environment.TickCount - _lastExchangeTime > time)
                {
                    SendHeartBeatsValidData();
                }
            };
            _heartBeatsTimer.Start();
        }
        #endregion

        #region Methods
        /// <summary>
        /// 发送心跳验证数据
        /// </summary>
        private void SendHeartBeatsValidData()
        {
            byte[] bytes = UTF8Encoding.UTF8.GetBytes(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            Send(bytes);
        }

        /// <summary>
        /// Connect to the host.
        /// </summary>
        /// <returns>True if connection has succeded, else false.</returns>
        public void Connect(string hostName, int port)
        {
            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                //_socket.Connect(new IPEndPoint(IPAddress.Parse(hostName), port));
                var list = Dns.GetHostAddresses(hostName);
                _socket.Connect(list[list.Length - 1], port);
                if (_socket.Connected)
                {
                    if (IsUseHeartBeatCertificate)
                        SendHeartBeatsValidData();
                    ProcessConnect();
                }
            }
            catch (Exception ex)
            {
                RaiseOnConnChange(new ConnStatusChangeArgs(false));
                Trace.WriteLine(ex.Message);
            }
        }
        private void OnIOComplete(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                switch (e.LastOperation)
                {
                    case SocketAsyncOperation.Receive:
                        ProcessReceive(e);
                        break;
                    case SocketAsyncOperation.Send:
                        ProcessSend(e);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex.Message);
            }
        }
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                AsyncUserToken token = e.UserToken as AsyncUserToken;

                if (!token.Socket.ReceiveAsync(e))
                {
                    this.ProcessReceive(e);
                }
            }
            else
            {
                this.ProcessError(e);
            }
        }

        private List<byte> _list = new List<byte>();

        private string _sessionId = Guid.NewGuid().ToString();

        /// <summary>
        /// 是否分包标识，每次数据发送和接收的前4个字节代表数据长度
        /// </summary>
        public bool IsSplitPack = true;
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            try
            {
                if (e.SocketError == SocketError.Success && e.BytesTransferred > 0)
                {
                    if (IsSplitPack)
                    {
                        DynamicBufferManager.WriteBuffer(_sessionId, e.Buffer, e.Offset, e.BytesTransferred);
                        var list = DynamicBufferManager.PopPackets(_sessionId);
                        foreach (var item in list)
                        {
                            RaiseOnReceive(new DataReceivedArgs(_sessionId, item));
                        }

                        //int index = 0;
                        //while (index < data.Length - 4)
                        //{
                        //    var lenght = BitConverter.ToInt32(data, index);

                        //    if (NetByteOrder)
                        //        lenght = System.Net.IPAddress.NetworkToHostOrder(lenght); //把网络字节顺序转为本地字节顺序

                        //    if (lenght > 0 && index + lenght + 4 <= data.Length)
                        //    {
                        //        var splitData = new byte[lenght];
                        //        Array.Copy(data, index + 4, splitData, 0, lenght);
                        //        index = index + lenght + 4;
                        //        RaiseOnReceive(new DataReceivedArgs(splitData));
                        //    }
                        //    else
                        //    {
                        //        break;
                        //    }
                        //}
                    }
                    else
                    {
                        var data = new byte[e.BytesTransferred];
                        Array.Copy(e.Buffer, e.Offset, data, 0, e.BytesTransferred);
                        RaiseOnReceive(new DataReceivedArgs(data));
                    }

                    if (_socket.ReceiveAsync(e) == false)
                    {
                        ProcessReceive(e);
                    }
                }
                else
                {
                    this.ProcessError(e);
                }
            }
            catch (Exception)
            {

                this.ProcessError(e);
            }
        }
        /// <summary>
        /// Close socket in case of failure and throws a SockeException according to the SocketError.
        /// </summary>
        /// <param name="e">SocketAsyncEventArg associated with the failed operation.</param>
        private void ProcessError(SocketAsyncEventArgs e)
        {
            Socket s = e.AcceptSocket as Socket;
            if (s != null && s.Connected)
            {
                // close the socket associated with the client
                try
                {
                    s.Shutdown(SocketShutdown.Both);
                }
                catch (Exception)
                {
                    // throws if client process has already closed
                }
                finally
                {
                    if (s.Connected)
                    {
                        s.Close();
                    }
                }
            }
            RaiseOnConnChange(new ConnStatusChangeArgs(false));
        }
        private void ProcessConnect()
        {
            var so = new SocketAsyncEventArgs();
            so.Completed += new EventHandler<SocketAsyncEventArgs>(OnIOComplete);
            so.UserToken = new AsyncUserToken(so);
            so.SetBuffer(new Byte[_bufferSize], 0, _bufferSize);
            if (_socket.ReceiveAsync(so) == false)
                ProcessReceive(so);

            RaiseOnConnChange(new ConnStatusChangeArgs(true));
            InitHeartBeatsTimer();
        }

        //private void ProcessConnect(SocketAsyncEventArgs e)
        //{
        //    // Set the flag for socket connected.
        //    if (e.SocketError == SocketError.Success)
        //    {
        //        var so = new SocketAsyncEventArgs();
        //        so.Completed += new EventHandler<SocketAsyncEventArgs>(OnComplete);
        //        so.UserToken = new AsyncUserToken(so);
        //        so.SetBuffer(new Byte[_bufferSize], 0, _bufferSize);
        //        //so.RemoteEndPoint = hostEndPoint;
        //        if (_socket.ReceiveAsync(so) == false)
        //            ProcessReceive(so);

        //        ConnStatus = ConnectStatus.Connected;

        //        InitHeartBeatsTimer();
        //    }
        //    else
        //        ConnStatus = ConnectStatus.Fault;
        //}
        private void RaiseOnConnChange(ConnStatusChangeArgs args)
        {
            try
            {
                OnConnChangeEvent?.Invoke(args);
            }
            catch (Exception)
            {

            }
        }

        private void RaiseOnReceive(DataReceivedArgs args)
        {
            try
            {
                _lastExchangeTime = Environment.TickCount;
                OnReceivedEvent?.Invoke(args);
            }
            catch (Exception)
            {

            }
        }

        #region 发送数据    
        /// <summary>
        /// 同步发送数据
        /// </summary>
        /// <param name="sessionId"></param>
        /// <param name="data"></param>
        /// <param name="timeout">超时时间(ms)</param>
        /// <returns></returns>
        public int Send(byte[] data, int timeout = 0)
        {
            _lastExchangeTime = Environment.TickCount;

            int sent = 0;
            if (_socket != null && _socket.Connected)
            {
                var socket = _socket;
                socket.SendTimeout = timeout;
                try
                {
                    if (IsSplitPack)
                    {
                        var lenght = data.Length;
                        var list = new List<byte>(BitConverter.GetBytes(lenght));
                        list.AddRange(data);
                        sent += socket.Send(list.ToArray());
                    }
                    else
                    {
                        sent += socket.Send(data);
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.WouldBlock ||
                    ex.SocketErrorCode == SocketError.IOPending ||
                    ex.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                    {
                        // socket buffer is probably full, wait and try again
                        Thread.Sleep(30);
                    }
                    else
                    {
                        //IsConnected = false;
                    }
                }
            }
            else
            {
                //IsConnected = false;
            }
            return sent;
        }

        #endregion

        #endregion

        #region IDisposable Members

        /// <summary>
        /// Disposes the instance of SocketClient.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (_heartBeatsTimer != null)
                {
                    _heartBeatsTimer.Dispose();
                    _heartBeatsTimer = null;
                }
                DynamicBufferManager.Remove(_sessionId);
                _socket?.Disconnect(false);
            }
            catch (Exception)
            {

            }
        }

        #endregion
    }
}
