#region License
/*
 * ChunkedRequestStream.cs
 *
 * This code is derived from ChunkedInputStream.cs (System.Net) of Mono
 * (http://www.mono-project.com).
 *
 * The MIT License
 *
 * Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
 * Copyright (c) 2012-2022 sta.blockhead
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion

#region Authors
/*
 * Authors:
 * - Gonzalo Paniagua Javier <gonzalo@novell.com>
 */
#endregion


namespace WebSocketSharp.Net
{
    internal class ChunkedRequestStream : RequestStream
    {
        #region Private Fields

        private static readonly int _bufferLength;
        private readonly HttpListenerContext _context;
        private readonly ChunkStream _decoder;
        private bool _disposed;
        private bool _noMoreData;

        #endregion

        #region Static Constructor

        static ChunkedRequestStream()
        {
            _bufferLength = 8192;
        }

        #endregion

        #region Internal Constructors

        internal ChunkedRequestStream(
          Stream innerStream,
          byte[] initialBuffer,
          int offset,
          int count,
          HttpListenerContext context
        )
          : base(innerStream, initialBuffer, offset, count, -1)
        {
            _context = context;

            _decoder = new ChunkStream(
                         (WebHeaderCollection)context.Request.Headers
                       );
        }

        #endregion

        #region Internal Properties

        internal bool HasRemainingBuffer => _decoder.Count + Count > 0;

        internal byte[] RemainingBuffer
        {
            get
            {
                using (MemoryStream? buff = new())
                {
                    int cnt = _decoder.Count;

                    if (cnt > 0)
                        buff.Write(_decoder.EndBuffer, _decoder.Offset, cnt);

                    cnt = Count;

                    if (cnt > 0)
                        buff.Write(InitialBuffer, Offset, cnt);

                    buff.Close();

                    return buff.ToArray();
                }
            }
        }

        #endregion

        #region Private Methods

        private void onRead(IAsyncResult asyncResult)
        {
            ReadBufferState? rstate = (ReadBufferState)asyncResult.AsyncState;
            HttpStreamAsyncResult? ares = rstate.AsyncResult;

            try
            {
                int nread = base.EndRead(asyncResult);

                _decoder.Write(ares.Buffer, ares.Offset, nread);
                nread = _decoder.Read(rstate.Buffer, rstate.Offset, rstate.Count);

                rstate.Offset += nread;
                rstate.Count -= nread;

                if (rstate.Count == 0 || !_decoder.WantsMore || nread == 0)
                {
                    _noMoreData = !_decoder.WantsMore && nread == 0;

                    ares.Count = rstate.InitialCount - rstate.Count;
                    ares.Complete();

                    return;
                }

                base.BeginRead(ares.Buffer, ares.Offset, ares.Count, onRead, rstate);
            }
            catch (Exception ex)
            {
                _context.ErrorMessage = "I/O operation aborted";
                _context.SendError();

                ares.Complete(ex);
            }
        }

        #endregion

        #region Public Methods

        public override IAsyncResult BeginRead(
          byte[] buffer, int offset, int count, AsyncCallback callback, object state
        )
        {
            if (_disposed)
            {
                string? name = GetType().ToString();

                throw new ObjectDisposedException(name);
            }

            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0)
            {
                string? msg = "A negative value.";

                throw new ArgumentOutOfRangeException("offset", msg);
            }

            if (count < 0)
            {
                string? msg = "A negative value.";

                throw new ArgumentOutOfRangeException(nameof(count), msg);
            }

            int len = buffer.Length;

            if (offset + count > len)
            {
                string? msg = "The sum of 'offset' and 'count' is greater than the length of 'buffer'.";

                throw new ArgumentException(msg);
            }

            HttpStreamAsyncResult? ares = new HttpStreamAsyncResult(callback, state);

            if (_noMoreData)
            {
                ares.Complete();

                return ares;
            }

            int nread = _decoder.Read(buffer, offset, count);

            offset += nread;
            count -= nread;

            if (count == 0)
            {
                ares.Count = nread;
                ares.Complete();

                return ares;
            }

            if (!_decoder.WantsMore)
            {
                _noMoreData = nread == 0;

                ares.Count = nread;
                ares.Complete();

                return ares;
            }

            ares.Buffer = new byte[_bufferLength];
            ares.Offset = 0;
            ares.Count = _bufferLength;

            ReadBufferState? rstate = new ReadBufferState(buffer, offset, count, ares);
            rstate.InitialCount += nread;

            base.BeginRead(ares.Buffer, ares.Offset, ares.Count, onRead, rstate);

            return ares;
        }

        public override void Close()
        {
            if (_disposed)
                return;

            base.Close();

            _disposed = true;
        }

        public override int EndRead(IAsyncResult asyncResult)
        {
            if (_disposed)
            {
                string? name = GetType().ToString();

                throw new ObjectDisposedException(name);
            }

            if (asyncResult == null)
                throw new ArgumentNullException(nameof(asyncResult));

            HttpStreamAsyncResult? ares = asyncResult as HttpStreamAsyncResult;

            if (ares == null)
            {
                string? msg = "A wrong IAsyncResult instance.";

                throw new ArgumentException(msg, nameof(asyncResult));
            }

            if (!ares.IsCompleted)
                ares.AsyncWaitHandle.WaitOne();

            if (ares.HasException)
            {
                string? msg = "The I/O operation has been aborted.";

                throw new HttpListenerException(995, msg);
            }

            return ares.Count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            IAsyncResult? ares = BeginRead(buffer, offset, count, null, null);

            return EndRead(ares);
        }

        #endregion
    }
}
