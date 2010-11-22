﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SuperSocket.Common;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Command;
using SuperSocket.SocketBase.Protocol;
using SuperSocket.SocketEngine.AsyncSocket;

namespace SuperSocket.SocketEngine
{
    interface IAsyncSocketSession
    {
        SocketAsyncEventArgsProxy SocketAsyncProxy { get; set; }
        void ProcessReceive(SocketAsyncEventArgs e);
    }

    class AsyncSocketSession<TAppSession, TCommandInfo> : SocketSession<TAppSession, TCommandInfo>, IAsyncSocketSession
        where TAppSession : IAppSession, IAppSession<TAppSession, TCommandInfo>, new()
        where TCommandInfo : ICommandInfo
    {
        ICommandAsyncReader<TCommandInfo> m_CommandReader;
        SocketContext m_Context;

        public AsyncSocketSession(ICommandAsyncReader<TCommandInfo> initialCommandReader)
            : base()
        {
            m_CommandReader = initialCommandReader;
        }

        protected override void Start(SocketContext context)
        {
            m_Context = context;
            Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            SocketAsyncProxy.Initialize(Client, this, context);
            StartSession();
            StartReceive(SocketAsyncProxy.SocketEventArgs);
        }

        private void StartReceive(SocketAsyncEventArgs e)
        {
            if (IsClosed)
                return;

            bool willRaiseEvent = false;

            try
            {
                willRaiseEvent = Client.ReceiveAsync(e);
            }
            catch (Exception)
            {
                Close();
                return;
            }

            if (!willRaiseEvent)
            {
                ProcessReceive(e);
            }
        }

        public override void SendResponse(SocketContext context, string message)
        {
            if (IsClosed)
                return;

            byte[] data = context.Charset.GetBytes(message);

            if (IsClosed)
                return;

            try
            {
                Client.Send(data);
            }
            catch (Exception)
            {
                Close();
            }
        }

        public override void SendResponse(SocketContext context, byte[] data)
        {
            if(data == null || data.Length <= 0)
                return;

            if (IsClosed)
                return;

            try
            {
                Client.Send(data);
            }
            catch (Exception)
            {
                Close();
            }
        }

        public SocketAsyncEventArgsProxy SocketAsyncProxy { get; set; }

        public void ProcessReceive(SocketAsyncEventArgs e)
        {
            // check if the remote host closed the connection
            AsyncUserToken token = (AsyncUserToken)e.UserToken;
            if (e.BytesTransferred <= 0 || e.SocketError != SocketError.Success)
            {
                Close();
                return;
            }

            var commandInfo = m_CommandReader.FindCommand(m_Context, e.Buffer, e.Offset, e.BytesTransferred);

            m_CommandReader = m_CommandReader.NextCommandReader;

            if (commandInfo != null)
            {
                try
                {
                    ExecuteCommand(commandInfo);
                }
                catch (Exception exc)
                {
                    LogUtil.LogError(AppServer, "An error occurred in session: " + this.SessionID, exc);
                    HandleExceptionalError(exc);
                }
                //read the next block of data send from the client
                StartReceive(e);
            }
            else
            {
                StartReceive(e);
                return;
            }             
        }      

        public override void ApplySecureProtocol(SocketContext context)
        {
            //TODO: Implement async socket SSL/TLS encryption
        }

        /// <summary>
        /// Receives the data.
        /// Process data synchronously, because command execution is waiting the received data
        /// </summary>
        /// <param name="storeSteram">The store steram.</param>
        /// <param name="length">The length.</param>
        public override void ReceiveData(Stream storeSteram, int length)
        {
            byte[] buffer = this.SocketAsyncProxy.SocketEventArgs.Buffer;

            int thisRead = 0;
            int leftRead = length;
            int shouldRead = 0;

            AsyncUserToken token = this.SocketAsyncProxy.SocketEventArgs.UserToken as AsyncUserToken;

            var leftBuffer = m_CommandReader.GetLeftBuffer();

            if (leftBuffer != null && leftBuffer.Count > 0)
            {
                storeSteram.Write(leftBuffer.ToArrayData(), 0, leftBuffer.Count);
                leftRead -= leftBuffer.Count;
            }

            while (leftRead > 0)
            {
                shouldRead = Math.Min(buffer.Length, leftRead);
                thisRead = Client.Receive(buffer, 0, shouldRead, SocketFlags.None);
                AppSession.LastActiveTime = DateTime.Now;

                if (thisRead <= 0)
                {
                    //Slow speed? Wait a moment
                    Thread.Sleep(100);
                    continue;
                }

                storeSteram.Write(buffer, 0, thisRead);
                leftRead -= thisRead;
            }
        }

        /// <summary>
        /// Receives the data.
        /// Process data synchronously, because command execution is waiting the received data
        /// </summary>
        /// <param name="storeSteram">The store steram.</param>
        /// <param name="endMark">The end mark.</param>
        public override void ReceiveData(Stream storeSteram, byte[] endMark)
        {
            byte[] buffer = this.SocketAsyncProxy.SocketEventArgs.Buffer;
            byte[] lastData = new byte[endMark.Length];
            int lastDataSzie = 0;

            int thisRead = 0;

            //var commandBuffer = m_CommandReader.GetLeftBuffer();

            //if (commandBuffer != null && commandBuffer.Count > 0)
            //{
            //    var result = commandBuffer.SearchMark(endMark);

            //    if (!result.HasValue)
            //    {
            //        byte[] commandBufferData = commandBuffer.ToArray();
            //        storeSteram.Write(commandBufferData, 0, commandBufferData.Length);
            //        commandBuffer.Clear();
            //    }
            //}

            while (true)
            {
                thisRead = Client.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                AppSession.LastActiveTime = DateTime.Now;

                if (thisRead <= 0)
                {
                    Thread.Sleep(100);
                    continue;
                }

                storeSteram.Write(buffer, 0, thisRead);

                if(DetectEndMark(buffer, thisRead, endMark, lastData, ref lastDataSzie))
                    return;
            }
        }        
    }
}
