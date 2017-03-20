﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace SocketAsyncServer
{
    public class AsyncUserToken
    {
        public SocketAsyncEventArgs AsynSocketArgs { get; private set; }
        public Socket Socket { get; private set; }

        public string SessionId
        {
            get
            {
                if (Socket != null)
                    return Socket.Handle.ToString();
                else
                    return string.Empty;
            }
        }

        public AsyncUserToken(SocketAsyncEventArgs args)
        {
            AsynSocketArgs = args;
            Socket = args.AcceptSocket;
        }
    }
}
