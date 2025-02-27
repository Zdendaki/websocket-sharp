#region License
/*
 * EndPointListener.cs
 *
 * This code is derived from EndPointListener.cs (System.Net) of Mono
 * (http://www.mono-project.com).
 *
 * The MIT License
 *
 * Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
 * Copyright (c) 2012-2020 sta.blockhead
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

#region Contributors
/*
 * Contributors:
 * - Liryna <liryna.stark@gmail.com>
 * - Nicholas Devenish
 */
#endregion

using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WebSocketSharp.Net
{
    internal sealed class EndPointListener
    {
        #region Private Fields

        private List<HttpListenerPrefix> _all; // host == '+'
        private readonly Dictionary<HttpConnection, HttpConnection> _connections;
        private readonly object _connectionsSync;
        private static readonly string _defaultCertFolderPath;
        private readonly IPEndPoint _endpoint;
        private List<HttpListenerPrefix> _prefixes;
        private readonly bool _secure;
        private readonly Socket _socket;
        private readonly ServerSslConfiguration _sslConfig;
        private List<HttpListenerPrefix> _unhandled; // host == '*'

        #endregion

        #region Static Constructor

        static EndPointListener()
        {
            _defaultCertFolderPath = Environment.GetFolderPath(
                                       Environment.SpecialFolder.ApplicationData
                                     );
        }

        #endregion

        #region Internal Constructors

        internal EndPointListener(
          IPEndPoint endpoint,
          bool secure,
          string certificateFolderPath,
          ServerSslConfiguration sslConfig,
          bool reuseAddress
        )
        {
            _endpoint = endpoint;

            if (secure)
            {
                X509Certificate2? cert = getCertificate(
                     endpoint.Port,
                     certificateFolderPath,
                     sslConfig.ServerCertificate
                   );

                if (cert == null)
                {
                    string? msg = "No server certificate could be found.";

                    throw new ArgumentException(msg);
                }

                _secure = true;
                _sslConfig = new ServerSslConfiguration(sslConfig)
                {
                    ServerCertificate = cert
                };
            }

            _prefixes = new List<HttpListenerPrefix>();
            _connections = new Dictionary<HttpConnection, HttpConnection>();
            _connectionsSync = ((ICollection)_connections).SyncRoot;

            _socket = new Socket(
                        endpoint.Address.AddressFamily,
                        SocketType.Stream,
                        ProtocolType.Tcp
                      );

            if (reuseAddress)
            {
                _socket.SetSocketOption(
                  SocketOptionLevel.Socket,
                  SocketOptionName.ReuseAddress,
                  true
                );
            }

            _socket.Bind(endpoint);
            _socket.Listen(500);
            _socket.BeginAccept(onAccept, this);
        }

        #endregion

        #region Public Properties

        public IPAddress Address => _endpoint.Address;

        public bool IsSecure => _secure;

        public int Port => _endpoint.Port;

        public ServerSslConfiguration SslConfiguration => _sslConfig;

        #endregion

        #region Private Methods

        private static void AddSpecial(
      List<HttpListenerPrefix> prefixes, HttpListenerPrefix prefix
    )
        {
            string? path = prefix.Path;

            foreach (HttpListenerPrefix? pref in prefixes)
            {
                if (pref.Path == path)
                {
                    string? msg = "The prefix is already in use.";

                    throw new HttpListenerException(87, msg);
                }
            }

            prefixes.Add(prefix);
        }

        private void ClearConnections()
        {
            HttpConnection[] conns = null;

            lock (_connectionsSync)
            {
                int cnt = _connections.Count;

                if (cnt == 0)
                    return;

                conns = new HttpConnection[cnt];

                Dictionary<HttpConnection, HttpConnection>.ValueCollection? vals = _connections.Values;
                vals.CopyTo(conns, 0);

                _connections.Clear();
            }

            foreach (HttpConnection? conn in conns)
                conn.Close(true);
        }

        private static RSACryptoServiceProvider createRSAFromFile(string path)
        {
            RSACryptoServiceProvider? rsa = new RSACryptoServiceProvider();

            byte[]? key = File.ReadAllBytes(path);
            rsa.ImportCspBlob(key);

            return rsa;
        }

        private static X509Certificate2 getCertificate(
          int port, string folderPath, X509Certificate2 defaultCertificate
        )
        {
            if (folderPath == null || folderPath.Length == 0)
                folderPath = _defaultCertFolderPath;

            try
            {
                string? cer = Path.Combine(folderPath, string.Format("{0}.cer", port));
                string? key = Path.Combine(folderPath, string.Format("{0}.key", port));

                if (File.Exists(cer) && File.Exists(key))
                {
                    X509Certificate2? cert = new X509Certificate2(cer);
                    cert.CopyWithPrivateKey(createRSAFromFile(key));

                    return cert;
                }
            }
            catch
            {
            }

            return defaultCertificate;
        }

        private void leaveIfNoPrefix()
        {
            if (_prefixes.Count > 0)
                return;

            List<HttpListenerPrefix>? prefs = _unhandled;

            if (prefs != null && prefs.Count > 0)
                return;

            prefs = _all;

            if (prefs != null && prefs.Count > 0)
                return;

            Close();
        }

        private static void onAccept(IAsyncResult asyncResult)
        {
            EndPointListener? lsnr = (EndPointListener)asyncResult.AsyncState;

            Socket sock = null;

            try
            {
                sock = lsnr._socket.EndAccept(asyncResult);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                // TODO: Logging.
            }

            try
            {
                lsnr._socket.BeginAccept(onAccept, lsnr);
            }
            catch (Exception)
            {
                // TODO: Logging.

                if (sock != null)
                    sock.Close();

                return;
            }

            if (sock == null)
                return;

            processAccepted(sock, lsnr);
        }

        private static void processAccepted(
          Socket socket, EndPointListener listener
        )
        {
            HttpConnection conn;
            try
            {
                conn = new HttpConnection(socket, listener);
            }
            catch (Exception)
            {
                // TODO: Logging.

                socket.Close();

                return;
            }

            lock (listener._connectionsSync)
                listener._connections.Add(conn, conn);

            conn.BeginReadRequest();
        }

        private static bool removeSpecial(
          List<HttpListenerPrefix> prefixes, HttpListenerPrefix prefix
        )
        {
            string? path = prefix.Path;
            int cnt = prefixes.Count;

            for (int i = 0; i < cnt; i++)
            {
                if (prefixes[i].Path == path)
                {
                    prefixes.RemoveAt(i);

                    return true;
                }
            }

            return false;
        }

        private static HttpListener searchHttpListenerFromSpecial(
          string path, List<HttpListenerPrefix> prefixes
        )
        {
            if (prefixes == null)
                return null;

            HttpListener ret = null;

            int bestLen = -1;

            foreach (HttpListenerPrefix? pref in prefixes)
            {
                string? prefPath = pref.Path;
                int len = prefPath.Length;

                if (len < bestLen)
                    continue;

                if (path.StartsWith(prefPath, StringComparison.Ordinal))
                {
                    bestLen = len;
                    ret = pref.Listener;
                }
            }

            return ret;
        }

        #endregion

        #region Internal Methods

        internal static bool CertificateExists(int port, string folderPath)
        {
            if (folderPath == null || folderPath.Length == 0)
                folderPath = _defaultCertFolderPath;

            string? cer = Path.Combine(folderPath, string.Format("{0}.cer", port));
            string? key = Path.Combine(folderPath, string.Format("{0}.key", port));

            return File.Exists(cer) && File.Exists(key);
        }

        internal void RemoveConnection(HttpConnection connection)
        {
            lock (_connectionsSync)
                _connections.Remove(connection);
        }

        internal bool TrySearchHttpListener(Uri uri, out HttpListener listener)
        {
            listener = null;

            if (uri == null)
                return false;

            string? host = uri.Host;
            bool dns = Uri.CheckHostName(host) == UriHostNameType.Dns;
            string? port = uri.Port.ToString();
            string? path = HttpUtility.UrlDecode(uri.AbsolutePath);

            if (path[^1] != '/')
                path += "/";

            if (host != null && host.Length > 0)
            {
                List<HttpListenerPrefix>? prefs = _prefixes;
                int bestLen = -1;

                foreach (HttpListenerPrefix? pref in prefs)
                {
                    if (dns)
                    {
                        string? prefHost = pref.Host;
                        bool prefDns = Uri.CheckHostName(prefHost) == UriHostNameType.Dns;

                        if (prefDns)
                        {
                            if (prefHost != host)
                                continue;
                        }
                    }

                    if (pref.Port != port)
                        continue;

                    string? prefPath = pref.Path;
                    int len = prefPath.Length;

                    if (len < bestLen)
                        continue;

                    if (path.StartsWith(prefPath, StringComparison.Ordinal))
                    {
                        bestLen = len;
                        listener = pref.Listener;
                    }
                }

                if (bestLen != -1)
                    return true;
            }

            listener = searchHttpListenerFromSpecial(path, _unhandled);

            if (listener != null)
                return true;

            listener = searchHttpListenerFromSpecial(path, _all);

            return listener != null;
        }

        #endregion

        #region Public Methods

        public void AddPrefix(HttpListenerPrefix prefix)
        {
            List<HttpListenerPrefix> current, future;

            if (prefix.Host == "*")
            {
                do
                {
                    current = _unhandled;
                    future = current != null
                             ? new List<HttpListenerPrefix>(current)
                             : new List<HttpListenerPrefix>();

                    AddSpecial(future, prefix);
                }
                while (
                  Interlocked.CompareExchange(ref _unhandled, future, current) != current
                );

                return;
            }

            if (prefix.Host == "+")
            {
                do
                {
                    current = _all;
                    future = current != null
                             ? new List<HttpListenerPrefix>(current)
                             : new List<HttpListenerPrefix>();

                    AddSpecial(future, prefix);
                }
                while (
                  Interlocked.CompareExchange(ref _all, future, current) != current
                );

                return;
            }

            do
            {
                current = _prefixes;
                int idx = current.IndexOf(prefix);

                if (idx > -1)
                {
                    if (current[idx].Listener != prefix.Listener)
                    {
                        string? msg = string.Format(
                        "There is another listener for {0}.", prefix
                      );

                        throw new HttpListenerException(87, msg);
                    }

                    return;
                }

                future = new List<HttpListenerPrefix>(current)
                {
                    prefix
                };
            }
            while (
              Interlocked.CompareExchange(ref _prefixes, future, current) != current
            );
        }

        public void Close()
        {
            _socket.Close();

            ClearConnections();
            EndPointManager.RemoveEndPoint(_endpoint);
        }

        public void RemovePrefix(HttpListenerPrefix prefix)
        {
            List<HttpListenerPrefix> current, future;

            if (prefix.Host == "*")
            {
                do
                {
                    current = _unhandled;

                    if (current == null)
                        break;

                    future = new List<HttpListenerPrefix>(current);

                    if (!removeSpecial(future, prefix))
                        break;
                }
                while (
                  Interlocked.CompareExchange(ref _unhandled, future, current) != current
                );

                leaveIfNoPrefix();

                return;
            }

            if (prefix.Host == "+")
            {
                do
                {
                    current = _all;

                    if (current == null)
                        break;

                    future = new List<HttpListenerPrefix>(current);

                    if (!removeSpecial(future, prefix))
                        break;
                }
                while (
                  Interlocked.CompareExchange(ref _all, future, current) != current
                );

                leaveIfNoPrefix();

                return;
            }

            do
            {
                current = _prefixes;

                if (!current.Contains(prefix))
                    break;

                future = new List<HttpListenerPrefix>(current);
                future.Remove(prefix);
            }
            while (
              Interlocked.CompareExchange(ref _prefixes, future, current) != current
            );

            leaveIfNoPrefix();
        }

        #endregion
    }
}
