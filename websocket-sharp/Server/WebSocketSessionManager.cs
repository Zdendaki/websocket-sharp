#region License
/*
 * WebSocketSessionManager.cs
 *
 * The MIT License
 *
 * Copyright (c) 2012-2021 sta.blockhead
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

using System.Collections;

namespace WebSocketSharp.Server
{
    /// <summary>
    /// Provides the management function for the sessions in a WebSocket service.
    /// </summary>
    /// <remarks>
    /// This class manages the sessions in a WebSocket service provided by the
    /// <see cref="WebSocketServer"/> or <see cref="HttpServer"/> class.
    /// </remarks>
    public class WebSocketSessionManager
    {
        #region Private Fields

        private static readonly byte[] _emptyPingFrameAsBytes;
        private readonly object _forSweep;
        private volatile bool _keepClean;
        private readonly Logger _log;
        private readonly Dictionary<string, IWebSocketSession> _sessions;
        private volatile ServerState _state;
        private volatile bool _sweeping;
        private System.Timers.Timer _sweepTimer;
        private readonly object _sync;
        private TimeSpan _waitTime;

        #endregion

        #region Static Constructor

        static WebSocketSessionManager()
        {
            _emptyPingFrameAsBytes = WebSocketFrame
                                     .CreatePingFrame(false)
                                     .ToArray();
        }

        #endregion

        #region Internal Constructors

        internal WebSocketSessionManager(Logger log)
        {
            _log = log;

            _forSweep = new object();
            _keepClean = true;
            _sessions = new Dictionary<string, IWebSocketSession>();
            _state = ServerState.Ready;
            _sync = ((ICollection)_sessions).SyncRoot;
            _waitTime = TimeSpan.FromSeconds(1);

            setSweepTimer(60000);
        }

        #endregion

        #region Internal Properties

        internal ServerState State => _state;

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets the IDs for the active sessions in the WebSocket service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;string&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the IDs for the active sessions.
        ///   </para>
        /// </value>
        public IEnumerable<string> ActiveIDs
        {
            get
            {
                foreach (KeyValuePair<string, bool> res in Broadping(_emptyPingFrameAsBytes))
                {
                    if (res.Value)
                        yield return res.Key;
                }
            }
        }

        /// <summary>
        /// Gets the number of the sessions in the WebSocket service.
        /// </summary>
        /// <value>
        /// An <see cref="int"/> that represents the number of the sessions.
        /// </value>
        public int Count
        {
            get
            {
                lock (_sync)
                    return _sessions.Count;
            }
        }

        /// <summary>
        /// Gets the IDs for the sessions in the WebSocket service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;string&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the IDs for the sessions.
        ///   </para>
        /// </value>
        public IEnumerable<string> IDs
        {
            get
            {
                if (_state != ServerState.Start)
                    return Enumerable.Empty<string>();

                lock (_sync)
                {
                    if (_state != ServerState.Start)
                        return Enumerable.Empty<string>();

                    return _sessions.Keys.ToList();
                }
            }
        }

        /// <summary>
        /// Gets the IDs for the inactive sessions in the WebSocket service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;string&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the IDs for the inactive sessions.
        ///   </para>
        /// </value>
        public IEnumerable<string> InactiveIDs
        {
            get
            {
                foreach (KeyValuePair<string, bool> res in Broadping(_emptyPingFrameAsBytes))
                {
                    if (!res.Value)
                        yield return res.Key;
                }
            }
        }

        /// <summary>
        /// Gets the session instance with the specified ID.
        /// </summary>
        /// <value>
        ///   <para>
        ///   A <see cref="IWebSocketSession"/> instance or <see langword="null"/>
        ///   if not found.
        ///   </para>
        ///   <para>
        ///   The session instance provides the function to access the information
        ///   in the session.
        ///   </para>
        /// </value>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session to find.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        public IWebSocketSession this[string id]
        {
            get
            {
                if (id == null)
                    throw new ArgumentNullException(nameof(id));

                if (id.Length == 0)
                    throw new ArgumentException("An empty string.", nameof(id));


                tryGetSession(id, out IWebSocketSession session);

                return session;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the inactive sessions in
        /// the WebSocket service are cleaned up periodically.
        /// </summary>
        /// <remarks>
        /// The set operation does nothing if the service has already started or
        /// it is shutting down.
        /// </remarks>
        /// <value>
        /// <c>true</c> if the inactive sessions are cleaned up every 60 seconds;
        /// otherwise, <c>false</c>.
        /// </value>
        public bool KeepClean
        {
            get => _keepClean;

            set
            {
                lock (_sync)
                {
                    if (!CanSet())
                        return;

                    _keepClean = value;
                }
            }
        }

        /// <summary>
        /// Gets the session instances in the WebSocket service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;IWebSocketSession&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the session instances.
        ///   </para>
        /// </value>
        public IEnumerable<IWebSocketSession> Sessions
        {
            get
            {
                if (_state != ServerState.Start)
                    return Enumerable.Empty<IWebSocketSession>();

                lock (_sync)
                {
                    if (_state != ServerState.Start)
                        return Enumerable.Empty<IWebSocketSession>();

                    return _sessions.Values.ToList();
                }
            }
        }

        /// <summary>
        /// Gets or sets the time to wait for the response to the WebSocket Ping
        /// or Close.
        /// </summary>
        /// <remarks>
        /// The set operation does nothing if the service has already started or
        /// it is shutting down.
        /// </remarks>
        /// <value>
        /// A <see cref="TimeSpan"/> to wait for the response.
        /// </value>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value specified for a set operation is zero or less.
        /// </exception>
        public TimeSpan WaitTime
        {
            get => _waitTime;

            set
            {
                if (value <= TimeSpan.Zero)
                {
                    string? msg = "It is zero or less.";

                    throw new ArgumentOutOfRangeException("value", msg);
                }

                lock (_sync)
                {
                    if (!CanSet())
                        return;

                    _waitTime = value;
                }
            }
        }

        #endregion

        #region Private Methods

        private void Broadcast(Opcode opcode, byte[] data, Action completed)
        {
            Dictionary<CompressionMethod, byte[]>? cache = new Dictionary<CompressionMethod, byte[]>();

            try
            {
                foreach (IWebSocketSession? session in Sessions)
                {
                    if (_state != ServerState.Start)
                    {
                        _log.Error("The service is shutting down.");

                        break;
                    }

                    session.Context.WebSocket.Send(opcode, data, cache);
                }

                completed?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex.Message);
                _log.Debug(ex.ToString());
            }
            finally
            {
                cache.Clear();
            }
        }

        private void Broadcast(Opcode opcode, Stream stream, Action completed)
        {
            Dictionary<CompressionMethod, Stream>? cache = new Dictionary<CompressionMethod, Stream>();

            try
            {
                foreach (IWebSocketSession? session in Sessions)
                {
                    if (_state != ServerState.Start)
                    {
                        _log.Error("The service is shutting down.");

                        break;
                    }

                    session.Context.WebSocket.Send(opcode, stream, cache);
                }

                completed?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex.Message);
                _log.Debug(ex.ToString());
            }
            finally
            {
                foreach (Stream? cached in cache.Values)
                    cached.Dispose();

                cache.Clear();
            }
        }

        private void BroadcastAsync(Opcode opcode, byte[] data, Action completed)
        {
            ThreadPool.QueueUserWorkItem(
              state => Broadcast(opcode, data, completed)
            );
        }

        private void BroadcastAsync(Opcode opcode, Stream stream, Action completed)
        {
            ThreadPool.QueueUserWorkItem(
              state => Broadcast(opcode, stream, completed)
            );
        }

        private Dictionary<string, bool> Broadping(byte[] frameAsBytes)
        {
            Dictionary<string, bool>? ret = new Dictionary<string, bool>();

            foreach (IWebSocketSession? session in Sessions)
            {
                if (_state != ServerState.Start)
                {
                    _log.Error("The service is shutting down.");

                    break;
                }

                bool res = session.Context.WebSocket.Ping(frameAsBytes, _waitTime);

                ret.Add(session.ID, res);
            }

            return ret;
        }

        private bool CanSet()
        {
            return _state == ServerState.Ready || _state == ServerState.Stop;
        }

        private static string createID()
        {
            return Guid.NewGuid().ToString("N");
        }

        private void setSweepTimer(double interval)
        {
            _sweepTimer = new System.Timers.Timer(interval);
            _sweepTimer.Elapsed += (sender, e) => Sweep();
        }

        private void stop(PayloadData payloadData, bool send)
        {
            byte[]? bytes = send
                  ? WebSocketFrame
                    .CreateCloseFrame(payloadData, false)
                    .ToArray()
                  : null;

            lock (_sync)
            {
                _state = ServerState.ShuttingDown;
                _sweepTimer.Enabled = false;

                foreach (IWebSocketSession? session in _sessions.Values.ToList())
                    session.Context.WebSocket.Close(payloadData, bytes);

                _state = ServerState.Stop;
            }
        }

        private bool tryGetSession(string id, out IWebSocketSession session)
        {
            session = null;

            if (_state != ServerState.Start)
                return false;

            lock (_sync)
            {
                if (_state != ServerState.Start)
                    return false;

                return _sessions.TryGetValue(id, out session);
            }
        }

        #endregion

        #region Internal Methods

        internal string Add(IWebSocketSession session)
        {
            lock (_sync)
            {
                if (_state != ServerState.Start)
                    return null;

                string? id = createID();

                _sessions.Add(id, session);

                return id;
            }
        }

        internal bool Remove(string id)
        {
            lock (_sync)
                return _sessions.Remove(id);
        }

        internal void Start()
        {
            lock (_sync)
            {
                _sweepTimer.Enabled = _keepClean;
                _state = ServerState.Start;
            }
        }

        internal void Stop(ushort code, string reason)
        {
            if (code == 1005)
            {
                stop(PayloadData.Empty, true);

                return;
            }

            PayloadData? payloadData = new PayloadData(code, reason);
            bool send = !code.IsReserved();

            stop(payloadData, send);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sends the specified data to every client in the WebSocket service.
        /// </summary>
        /// <param name="data">
        /// An array of <see cref="byte"/> that specifies the binary data to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the service is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        public void Broadcast(byte[] data)
        {
            if (_state != ServerState.Start)
            {
                string? msg = "The current state of the service is not Start.";

                throw new InvalidOperationException(msg);
            }

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.LongLength <= WebSocket.FragmentLength)
                Broadcast(Opcode.Binary, data, null);
            else
                Broadcast(Opcode.Binary, new MemoryStream(data), null);
        }

        /// <summary>
        /// Sends the specified data to every client in the WebSocket service.
        /// </summary>
        /// <param name="data">
        /// A <see cref="string"/> that specifies the text data to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the service is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="data"/> could not be UTF-8-encoded.
        /// </exception>
        public void Broadcast(string data)
        {
            if (_state != ServerState.Start)
            {
                string? msg = "The current state of the service is not Start.";

                throw new InvalidOperationException(msg);
            }

            if (data == null)
                throw new ArgumentNullException(nameof(data));


            if (!data.TryGetUTF8EncodedBytes(out byte[] bytes))
            {
                string? msg = "It could not be UTF-8-encoded.";

                throw new ArgumentException(msg, nameof(data));
            }

            if (bytes.LongLength <= WebSocket.FragmentLength)
                Broadcast(Opcode.Text, bytes, null);
            else
                Broadcast(Opcode.Text, new MemoryStream(bytes), null);
        }

        /// <summary>
        /// Sends the data from the specified stream instance to every client in
        /// the WebSocket service.
        /// </summary>
        /// <param name="stream">
        ///   <para>
        ///   A <see cref="Stream"/> instance from which to read the data to send.
        ///   </para>
        ///   <para>
        ///   The data is sent as the binary data.
        ///   </para>
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the service is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="stream"/> cannot be read.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="length"/> is less than 1.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   No data could be read from <paramref name="stream"/>.
        ///   </para>
        /// </exception>
        public void Broadcast(Stream stream, int length)
        {
            if (_state != ServerState.Start)
            {
                string? msg = "The current state of the service is not Start.";

                throw new InvalidOperationException(msg);
            }

            if (stream == null)
                throw new ArgumentNullException("stream");

            if (!stream.CanRead)
            {
                string? msg = "It cannot be read.";

                throw new ArgumentException(msg, "stream");
            }

            if (length < 1)
            {
                string? msg = "It is less than 1.";

                throw new ArgumentException(msg, "length");
            }

            byte[]? bytes = stream.ReadBytes(length);
            int len = bytes.Length;

            if (len == 0)
            {
                string? msg = "No data could be read from it.";

                throw new ArgumentException(msg, "stream");
            }

            if (len < length)
            {
                string? fmt = "Only {0} byte(s) of data could be read from the stream.";
                string? msg = string.Format(fmt, len);

                _log.Warn(msg);
            }

            if (len <= WebSocket.FragmentLength)
                Broadcast(Opcode.Binary, bytes, null);
            else
                Broadcast(Opcode.Binary, new MemoryStream(bytes), null);
        }

        /// <summary>
        /// Sends the specified data asynchronously to every client in
        /// the WebSocket service.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// An array of <see cref="byte"/> that specifies the binary data to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <see cref="Action"/> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the service is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        public void BroadcastAsync(byte[] data, Action completed)
        {
            if (_state != ServerState.Start)
            {
                string? msg = "The current state of the service is not Start.";

                throw new InvalidOperationException(msg);
            }

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.LongLength <= WebSocket.FragmentLength)
                BroadcastAsync(Opcode.Binary, data, completed);
            else
                BroadcastAsync(Opcode.Binary, new MemoryStream(data), completed);
        }

        /// <summary>
        /// Sends the specified data asynchronously to every client in
        /// the WebSocket service.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// A <see cref="string"/> that specifies the text data to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <see cref="Action"/> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the service is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="data"/> could not be UTF-8-encoded.
        /// </exception>
        public void BroadcastAsync(string data, Action completed)
        {
            if (_state != ServerState.Start)
            {
                string? msg = "The current state of the service is not Start.";

                throw new InvalidOperationException(msg);
            }

            if (data == null)
                throw new ArgumentNullException(nameof(data));


            if (!data.TryGetUTF8EncodedBytes(out byte[] bytes))
            {
                string? msg = "It could not be UTF-8-encoded.";

                throw new ArgumentException(msg, nameof(data));
            }

            if (bytes.LongLength <= WebSocket.FragmentLength)
                BroadcastAsync(Opcode.Text, bytes, completed);
            else
                BroadcastAsync(Opcode.Text, new MemoryStream(bytes), completed);
        }

        /// <summary>
        /// Sends the data from the specified stream instance asynchronously to
        /// every client in the WebSocket service.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="stream">
        ///   <para>
        ///   A <see cref="Stream"/> instance from which to read the data to send.
        ///   </para>
        ///   <para>
        ///   The data is sent as the binary data.
        ///   </para>
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <see cref="Action"/> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the service is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="stream"/> cannot be read.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="length"/> is less than 1.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   No data could be read from <paramref name="stream"/>.
        ///   </para>
        /// </exception>
        public void BroadcastAsync(Stream stream, int length, Action completed)
        {
            if (_state != ServerState.Start)
            {
                string? msg = "The current state of the service is not Start.";

                throw new InvalidOperationException(msg);
            }

            if (stream == null)
                throw new ArgumentNullException("stream");

            if (!stream.CanRead)
            {
                string? msg = "It cannot be read.";

                throw new ArgumentException(msg, "stream");
            }

            if (length < 1)
            {
                string? msg = "It is less than 1.";

                throw new ArgumentException(msg, "length");
            }

            byte[]? bytes = stream.ReadBytes(length);
            int len = bytes.Length;

            if (len == 0)
            {
                string? msg = "No data could be read from it.";

                throw new ArgumentException(msg, "stream");
            }

            if (len < length)
            {
                string? fmt = "Only {0} byte(s) of data could be read from the stream.";
                string? msg = string.Format(fmt, len);

                _log.Warn(msg);
            }

            if (len <= WebSocket.FragmentLength)
                BroadcastAsync(Opcode.Binary, bytes, completed);
            else
                BroadcastAsync(Opcode.Binary, new MemoryStream(bytes), completed);
        }

        /// <summary>
        /// Closes the session with the specified ID.
        /// </summary>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session to close.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        public void CloseSession(string id)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Close();
        }

        /// <summary>
        /// Closes the session with the specified ID, code, and reason.
        /// </summary>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session to close.
        /// </param>
        /// <param name="code">
        ///   <para>
        ///   A <see cref="ushort"/> that specifies the status code indicating
        ///   the reason for the close.
        ///   </para>
        ///   <para>
        ///   The status codes are defined in
        ///   <see href="http://tools.ietf.org/html/rfc6455#section-7.4">
        ///   Section 7.4</see> of RFC 6455.
        ///   </para>
        /// </param>
        /// <param name="reason">
        ///   <para>
        ///   A <see cref="string"/> that specifies the reason for the close.
        ///   </para>
        ///   <para>
        ///   The size must be 123 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1010 (mandatory extension).
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1005 (no status) and there is reason.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="reason"/> could not be UTF-8-encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   <para>
        ///   <paramref name="code"/> is less than 1000 or greater than 4999.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The size of <paramref name="reason"/> is greater than 123 bytes.
        ///   </para>
        /// </exception>
        public void CloseSession(string id, ushort code, string reason)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Close(code, reason);
        }

        /// <summary>
        /// Closes the session with the specified ID, code, and reason.
        /// </summary>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session to close.
        /// </param>
        /// <param name="code">
        ///   <para>
        ///   One of the <see cref="CloseStatusCode"/> enum values.
        ///   </para>
        ///   <para>
        ///   It specifies the status code indicating the reason for the close.
        ///   </para>
        /// </param>
        /// <param name="reason">
        ///   <para>
        ///   A <see cref="string"/> that specifies the reason for the close.
        ///   </para>
        ///   <para>
        ///   The size must be 123 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.MandatoryExtension"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.NoStatus"/> and there is reason.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="reason"/> could not be UTF-8-encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size of <paramref name="reason"/> is greater than 123 bytes.
        /// </exception>
        public void CloseSession(string id, CloseStatusCode code, string reason)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Close(code, reason);
        }

        /// <summary>
        /// Sends a ping to the client using the specified session.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the send has done with no error and a pong has been
        /// received from the client within a time; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        public bool PingTo(string id)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            return session.Context.WebSocket.Ping();
        }

        /// <summary>
        /// Sends a ping with the specified message to the client using
        /// the specified session.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the send has done with no error and a pong has been
        /// received from the client within a time; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="message">
        ///   <para>
        ///   A <see cref="string"/> that specifies the message to send.
        ///   </para>
        ///   <para>
        ///   The size must be 125 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="message"/> could not be UTF-8-encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size of <paramref name="message"/> is greater than 125 bytes.
        /// </exception>
        public bool PingTo(string message, string id)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            return session.Context.WebSocket.Ping(message);
        }

        /// <summary>
        /// Sends the specified data to the client using the specified session.
        /// </summary>
        /// <param name="data">
        /// An array of <see cref="byte"/> that specifies the binary data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendTo(byte[] data, string id)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Send(data);
        }

        /// <summary>
        /// Sends the specified data to the client using the specified session.
        /// </summary>
        /// <param name="data">
        /// A <see cref="string"/> that specifies the text data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> could not be UTF-8-encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendTo(string data, string id)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Send(data);
        }

        /// <summary>
        /// Sends the data from the specified stream instance to the client using
        /// the specified session.
        /// </summary>
        /// <param name="stream">
        ///   <para>
        ///   A <see cref="Stream"/> instance from which to read the data to send.
        ///   </para>
        ///   <para>
        ///   The data is sent as the binary data.
        ///   </para>
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="stream"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="stream"/> cannot be read.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="length"/> is less than 1.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   No data could be read from <paramref name="stream"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendTo(Stream stream, int length, string id)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Send(stream, length);
        }

        /// <summary>
        /// Sends the specified data asynchronously to the client using
        /// the specified session.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// An array of <see cref="byte"/> that specifies the binary data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <c>Action&lt;bool&gt;</c> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        ///   <para>
        ///   <c>true</c> is passed to the method if the send has done with
        ///   no error; otherwise, <c>false</c>.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendToAsync(byte[] data, string id, Action<bool> completed)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.SendAsync(data, completed);
        }

        /// <summary>
        /// Sends the specified data asynchronously to the client using
        /// the specified session.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// A <see cref="string"/> that specifies the text data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <c>Action&lt;bool&gt;</c> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        ///   <para>
        ///   <c>true</c> is passed to the method if the send has done with
        ///   no error; otherwise, <c>false</c>.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> could not be UTF-8-encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendToAsync(string data, string id, Action<bool> completed)
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.SendAsync(data, completed);
        }

        /// <summary>
        /// Sends the data from the specified stream instance asynchronously to
        /// the client using the specified session.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="stream">
        ///   <para>
        ///   A <see cref="Stream"/> instance from which to read the data to send.
        ///   </para>
        ///   <para>
        ///   The data is sent as the binary data.
        ///   </para>
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <c>Action&lt;bool&gt;</c> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        ///   <para>
        ///   <c>true</c> is passed to the method if the send has done with
        ///   no error; otherwise, <c>false</c>.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="stream"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="stream"/> cannot be read.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="length"/> is less than 1.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   No data could be read from <paramref name="stream"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendToAsync(
          Stream stream, int length, string id, Action<bool> completed
        )
        {

            if (!TryGetSession(id, out IWebSocketSession session))
            {
                string? msg = "The session could not be found.";

                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.SendAsync(stream, length, completed);
        }

        /// <summary>
        /// Cleans up the inactive sessions in the WebSocket service.
        /// </summary>
        public void Sweep()
        {
            if (_sweeping)
            {
                _log.Info("The sweeping is already in progress.");

                return;
            }

            lock (_forSweep)
            {
                if (_sweeping)
                {
                    _log.Info("The sweeping is already in progress.");

                    return;
                }

                _sweeping = true;
            }

            foreach (string? id in InactiveIDs)
            {
                if (_state != ServerState.Start)
                    break;

                lock (_sync)
                {
                    if (_state != ServerState.Start)
                        break;


                    if (!_sessions.TryGetValue(id, out IWebSocketSession session))
                        continue;

                    WebSocketState state = session.ConnectionState;

                    if (state == WebSocketState.Open)
                    {
                        session.Context.WebSocket.Close(CloseStatusCode.Abnormal);

                        continue;
                    }

                    if (state == WebSocketState.Closing)
                        continue;

                    _sessions.Remove(id);
                }
            }

            _sweeping = false;
        }

        /// <summary>
        /// Tries to get the session instance with the specified ID.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the session is successfully found; otherwise,
        /// <c>false</c>.
        /// </returns>
        /// <param name="id">
        /// A <see cref="string"/> that specifies the ID of the session to find.
        /// </param>
        /// <param name="session">
        ///   <para>
        ///   When this method returns, a <see cref="IWebSocketSession"/>
        ///   instance or <see langword="null"/> if not found.
        ///   </para>
        ///   <para>
        ///   The session instance provides the function to access
        ///   the information in the session.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        public bool TryGetSession(string id, out IWebSocketSession session)
        {
            if (id == null)
                throw new ArgumentNullException(nameof(id));

            if (id.Length == 0)
                throw new ArgumentException("An empty string.", nameof(id));

            return tryGetSession(id, out session);
        }

        #endregion
    }
}
