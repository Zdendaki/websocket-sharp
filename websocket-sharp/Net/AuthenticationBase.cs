#region License
/*
 * AuthenticationBase.cs
 *
 * The MIT License
 *
 * Copyright (c) 2014 sta.blockhead
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

using System.Collections.Specialized;
using System.Text;

namespace WebSocketSharp.Net
{
    internal abstract class AuthenticationBase
    {
        #region Private Fields

        private readonly AuthenticationSchemes _scheme;

        #endregion

        #region Internal Fields

        internal NameValueCollection Parameters;

        #endregion

        #region Protected Constructors

        protected AuthenticationBase(AuthenticationSchemes scheme, NameValueCollection parameters)
        {
            _scheme = scheme;
            Parameters = parameters;
        }

        #endregion

        #region Public Properties

        public string Algorithm => Parameters["algorithm"];

        public string Nonce => Parameters["nonce"];

        public string Opaque => Parameters["opaque"];

        public string Qop => Parameters["qop"];

        public string Realm => Parameters["realm"];

        public AuthenticationSchemes Scheme => _scheme;

        #endregion

        #region Internal Methods

        internal static string CreateNonceValue()
        {
            byte[]? src = new byte[16];
            Random rand = new();
            rand.NextBytes(src);

            StringBuilder res = new(32);
            foreach (byte b in src)
                res.Append(b.ToString("x2"));

            return res.ToString();
        }

        internal static NameValueCollection ParseParameters(string value)
        {
            NameValueCollection res = new();
            foreach (string? param in value.SplitHeaderValue(','))
            {
                int i = param.IndexOf('=');
                string? name = i > 0 ? param.Substring(0, i).Trim() : null;
                string? val = i < 0
                  ? param.Trim().Trim('"')
                  : i < param.Length - 1
                    ? param.Substring(i + 1).Trim().Trim('"')
                    : string.Empty;

                res.Add(name, val);
            }

            return res;
        }

        internal abstract string ToBasicString();

        internal abstract string ToDigestString();

        #endregion

        #region Public Methods

        public override string ToString()
        {
            return _scheme == AuthenticationSchemes.Basic
                   ? ToBasicString()
                   : _scheme == AuthenticationSchemes.Digest
                     ? ToDigestString()
                     : string.Empty;
        }

        #endregion
    }
}
