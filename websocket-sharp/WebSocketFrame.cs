#region License
/*
 * WebSocketFrame.cs
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

#region Contributors
/*
 * Contributors:
 * - Chris Swiedler
 */
#endregion

using System.Collections;
using System.Security.Cryptography;
using System.Text;

namespace WebSocketSharp
{
    internal class WebSocketFrame : IEnumerable<byte>
    {
        #region Private Fields

        private byte[] _extPayloadLength;
        private Fin _fin;
        private Mask _mask;
        private byte[] _maskingKey;
        private Opcode _opcode;
        private PayloadData _payloadData;
        private byte _payloadLength;
        private Rsv _rsv1;
        private Rsv _rsv2;
        private Rsv _rsv3;

        #endregion

        #region Private Constructors

        private WebSocketFrame()
        {
        }

        #endregion

        #region Internal Constructors

        internal WebSocketFrame(Opcode opcode, PayloadData payloadData, bool mask)
          : this(Fin.Final, opcode, payloadData, false, mask)
        {
        }

        internal WebSocketFrame(
          Fin fin, Opcode opcode, byte[] data, bool compressed, bool mask
        )
          : this(fin, opcode, new PayloadData(data), compressed, mask)
        {
        }

        internal WebSocketFrame(
          Fin fin,
          Opcode opcode,
          PayloadData payloadData,
          bool compressed,
          bool mask
        )
        {
            _fin = fin;
            _opcode = opcode;

            _rsv1 = opcode.IsData() && compressed ? Rsv.On : Rsv.Off;
            _rsv2 = Rsv.Off;
            _rsv3 = Rsv.Off;

            ulong len = payloadData.Length;

            if (len < 126)
            {
                _payloadLength = (byte)len;
                _extPayloadLength = WebSocket.EmptyBytes;
            }
            else if (len < 0x010000)
            {
                _payloadLength = 126;
                _extPayloadLength = ((ushort)len).ToByteArray(ByteOrder.Big);
            }
            else
            {
                _payloadLength = 127;
                _extPayloadLength = len.ToByteArray(ByteOrder.Big);
            }

            if (mask)
            {
                _mask = Mask.On;
                _maskingKey = createMaskingKey();

                payloadData.Mask(_maskingKey);
            }
            else
            {
                _mask = Mask.Off;
                _maskingKey = WebSocket.EmptyBytes;
            }

            _payloadData = payloadData;
        }

        #endregion

        #region Internal Properties

        internal ulong ExactPayloadLength => _payloadLength < 126
                   ? _payloadLength
                   : _payloadLength == 126
                     ? _extPayloadLength.ToUInt16(ByteOrder.Big)
                     : _extPayloadLength.ToUInt64(ByteOrder.Big);

        internal int ExtendedPayloadLengthWidth => _payloadLength < 126
                   ? 0
                   : _payloadLength == 126
                     ? 2
                     : 8;

        #endregion

        #region Public Properties

        public byte[] ExtendedPayloadLength => _extPayloadLength;

        public Fin Fin => _fin;

        public bool IsBinary => _opcode == Opcode.Binary;

        public bool IsClose => _opcode == Opcode.Close;

        public bool IsCompressed => _rsv1 == Rsv.On;

        public bool IsContinuation => _opcode == Opcode.Cont;

        public bool IsControl => _opcode >= Opcode.Close;

        public bool IsData => _opcode == Opcode.Text || _opcode == Opcode.Binary;

        public bool IsFinal => _fin == Fin.Final;

        public bool IsFragment => _fin == Fin.More || _opcode == Opcode.Cont;

        public bool IsMasked => _mask == Mask.On;

        public bool IsPing => _opcode == Opcode.Ping;

        public bool IsPong => _opcode == Opcode.Pong;

        public bool IsText => _opcode == Opcode.Text;

        public ulong Length => 2
                   + (ulong)(_extPayloadLength.Length + _maskingKey.Length)
                   + _payloadData.Length;

        public Mask Mask => _mask;

        public byte[] MaskingKey => _maskingKey;

        public Opcode Opcode => _opcode;

        public PayloadData PayloadData => _payloadData;

        public byte PayloadLength => _payloadLength;

        public Rsv Rsv1 => _rsv1;

        public Rsv Rsv2 => _rsv2;

        public Rsv Rsv3 => _rsv3;

        #endregion

        #region Private Methods

        private static byte[] createMaskingKey()
        {
            return RandomNumberGenerator.GetBytes(4);
        }

        private static string dump(WebSocketFrame frame)
        {
            ulong len = frame.Length;
            long cnt = (long)(len / 4);
            int rem = (int)(len % 4);

            int cntDigit;
            string cntFmt;

            if (cnt < 10000)
            {
                cntDigit = 4;
                cntFmt = "{0,4}";
            }
            else if (cnt < 0x010000)
            {
                cntDigit = 4;
                cntFmt = "{0,4:X}";
            }
            else if (cnt < 0x0100000000)
            {
                cntDigit = 8;
                cntFmt = "{0,8:X}";
            }
            else
            {
                cntDigit = 16;
                cntFmt = "{0,16:X}";
            }

            string? spFmt = string.Format("{{0,{0}}}", cntDigit);

            string? headerFmt = string.Format(
                        @"
{0} 01234567 89ABCDEF 01234567 89ABCDEF
{0}+--------+--------+--------+--------+\n",
                        spFmt
                      );

            string? lineFmt = string.Format(
                      "{0}|{{1,8}} {{2,8}} {{3,8}} {{4,8}}|\n", cntFmt
                    );

            string? footerFmt = string.Format(
                        "{0}+--------+--------+--------+--------+", spFmt
                      );

            StringBuilder? buff = new StringBuilder(64);

            Func<Action<string, string, string, string>> linePrinter =
              () =>
              {
                  long lineCnt = 0;

                  return (arg1, arg2, arg3, arg4) =>
                  {
                      buff.AppendFormat(
                  lineFmt, ++lineCnt, arg1, arg2, arg3, arg4
                );
                  };
              };

            Action<string, string, string, string>? printLine = linePrinter();
            byte[]? bytes = frame.ToArray();

            buff.AppendFormat(headerFmt, string.Empty);

            for (long i = 0; i <= cnt; i++)
            {
                long j = i * 4;

                if (i < cnt)
                {
                    printLine(
                      Convert.ToString(bytes[j], 2).PadLeft(8, '0'),
                      Convert.ToString(bytes[j + 1], 2).PadLeft(8, '0'),
                      Convert.ToString(bytes[j + 2], 2).PadLeft(8, '0'),
                      Convert.ToString(bytes[j + 3], 2).PadLeft(8, '0')
                    );

                    continue;
                }

                if (rem > 0)
                {
                    printLine(
                      Convert.ToString(bytes[j], 2).PadLeft(8, '0'),
                      rem >= 2
                      ? Convert.ToString(bytes[j + 1], 2).PadLeft(8, '0')
                      : string.Empty,
                      rem == 3
                      ? Convert.ToString(bytes[j + 2], 2).PadLeft(8, '0')
                      : string.Empty,
                      string.Empty
                    );
                }
            }

            buff.AppendFormat(footerFmt, string.Empty);

            return buff.ToString();
        }

        private static string print(WebSocketFrame frame)
        {
            // Payload Length
            byte payloadLen = frame._payloadLength;

            // Extended Payload Length
            string? extPayloadLen = payloadLen > 125
                          ? frame.ExactPayloadLength.ToString()
                          : string.Empty;

            // Masking Key
            string? maskingKey = BitConverter.ToString(frame._maskingKey);

            // Payload Data
            string? payload = payloadLen == 0
                    ? string.Empty
                    : payloadLen > 125
                      ? "---"
                      : !frame.IsText
                        || frame.IsFragment
                        || frame.IsMasked
                        || frame.IsCompressed
                        ? frame._payloadData.ToString()
                        : utf8Decode(frame._payloadData.ApplicationData);

            string? fmt = @"
                    FIN: {0}
                   RSV1: {1}
                   RSV2: {2}
                   RSV3: {3}
                 Opcode: {4}
                   MASK: {5}
         Payload Length: {6}
Extended Payload Length: {7}
            Masking Key: {8}
           Payload Data: {9}";

            return string.Format(
                     fmt,
                     frame._fin,
                     frame._rsv1,
                     frame._rsv2,
                     frame._rsv3,
                     frame._opcode,
                     frame._mask,
                     payloadLen,
                     extPayloadLen,
                     maskingKey,
                     payload
                   );
        }

        private static WebSocketFrame processHeader(byte[] header)
        {
            if (header.Length != 2)
            {
                string? msg = "The header part of a frame could not be read.";

                throw new WebSocketException(msg);
            }

            // FIN
            Fin fin = (header[0] & 0x80) == 0x80 ? Fin.Final : Fin.More;

            // RSV1
            Rsv rsv1 = (header[0] & 0x40) == 0x40 ? Rsv.On : Rsv.Off;

            // RSV2
            Rsv rsv2 = (header[0] & 0x20) == 0x20 ? Rsv.On : Rsv.Off;

            // RSV3
            Rsv rsv3 = (header[0] & 0x10) == 0x10 ? Rsv.On : Rsv.Off;

            // Opcode
            byte opcode = (byte)(header[0] & 0x0f);

            // MASK
            Mask mask = (header[1] & 0x80) == 0x80 ? Mask.On : Mask.Off;

            // Payload Length
            byte payloadLen = (byte)(header[1] & 0x7f);

            if (!opcode.IsSupported())
            {
                string? msg = "A frame has an unsupported opcode.";

                throw new WebSocketException(CloseStatusCode.ProtocolError, msg);
            }

            if (!opcode.IsData() && rsv1 == Rsv.On)
            {
                string? msg = "A non data frame is compressed.";

                throw new WebSocketException(CloseStatusCode.ProtocolError, msg);
            }

            if (opcode.IsControl())
            {
                if (fin == Fin.More)
                {
                    string? msg = "A control frame is fragmented.";

                    throw new WebSocketException(CloseStatusCode.ProtocolError, msg);
                }

                if (payloadLen > 125)
                {
                    string? msg = "A control frame has too long payload length.";

                    throw new WebSocketException(CloseStatusCode.ProtocolError, msg);
                }
            }

            WebSocketFrame? frame = new WebSocketFrame
            {
                _fin = fin,
                _rsv1 = rsv1,
                _rsv2 = rsv2,
                _rsv3 = rsv3,
                _opcode = (Opcode)opcode,
                _mask = mask,
                _payloadLength = payloadLen
            };

            return frame;
        }

        private static WebSocketFrame readExtendedPayloadLength(
          Stream stream, WebSocketFrame frame
        )
        {
            int len = frame.ExtendedPayloadLengthWidth;

            if (len == 0)
            {
                frame._extPayloadLength = WebSocket.EmptyBytes;

                return frame;
            }

            byte[]? bytes = stream.ReadBytes(len);

            if (bytes.Length != len)
            {
                string? msg = "The extended payload length of a frame could not be read.";

                throw new WebSocketException(msg);
            }

            frame._extPayloadLength = bytes;

            return frame;
        }

        private static void readExtendedPayloadLengthAsync(
          Stream stream,
          WebSocketFrame frame,
          Action<WebSocketFrame> completed,
          Action<Exception> error
        )
        {
            int len = frame.ExtendedPayloadLengthWidth;

            if (len == 0)
            {
                frame._extPayloadLength = WebSocket.EmptyBytes;

                completed(frame);

                return;
            }

            stream.ReadBytesAsync(
              len,
              bytes =>
              {
                  if (bytes.Length != len)
                  {
                      string? msg = "The extended payload length of a frame could not be read.";

                      throw new WebSocketException(msg);
                  }

                  frame._extPayloadLength = bytes;

                  completed(frame);
              },
              error
            );
        }

        private static WebSocketFrame readHeader(Stream stream)
        {
            byte[]? bytes = stream.ReadBytes(2);

            return processHeader(bytes);
        }

        private static void readHeaderAsync(
          Stream stream, Action<WebSocketFrame> completed, Action<Exception> error
        )
        {
            stream.ReadBytesAsync(
              2,
              bytes =>
              {
                  WebSocketFrame? frame = processHeader(bytes);

                  completed(frame);
              },
              error
            );
        }

        private static WebSocketFrame readMaskingKey(
          Stream stream, WebSocketFrame frame
        )
        {
            if (!frame.IsMasked)
            {
                frame._maskingKey = WebSocket.EmptyBytes;

                return frame;
            }

            int len = 4;
            byte[]? bytes = stream.ReadBytes(len);

            if (bytes.Length != len)
            {
                string? msg = "The masking key of a frame could not be read.";

                throw new WebSocketException(msg);
            }

            frame._maskingKey = bytes;

            return frame;
        }

        private static void readMaskingKeyAsync(
          Stream stream,
          WebSocketFrame frame,
          Action<WebSocketFrame> completed,
          Action<Exception> error
        )
        {
            if (!frame.IsMasked)
            {
                frame._maskingKey = WebSocket.EmptyBytes;

                completed(frame);

                return;
            }

            int len = 4;

            stream.ReadBytesAsync(
              len,
              bytes =>
              {
                  if (bytes.Length != len)
                  {
                      string? msg = "The masking key of a frame could not be read.";

                      throw new WebSocketException(msg);
                  }

                  frame._maskingKey = bytes;

                  completed(frame);
              },
              error
            );
        }

        private static WebSocketFrame readPayloadData(
          Stream stream, WebSocketFrame frame
        )
        {
            ulong exactLen = frame.ExactPayloadLength;

            if (exactLen > PayloadData.MaxLength)
            {
                string? msg = "A frame has too long payload length.";

                throw new WebSocketException(CloseStatusCode.TooBig, msg);
            }

            if (exactLen == 0)
            {
                frame._payloadData = PayloadData.Empty;

                return frame;
            }

            long len = (long)exactLen;
            byte[]? bytes = frame._payloadLength < 127
                  ? stream.ReadBytes((int)exactLen)
                  : stream.ReadBytes(len, 1024);

            if (bytes.LongLength != len)
            {
                string? msg = "The payload data of a frame could not be read.";

                throw new WebSocketException(msg);
            }

            frame._payloadData = new PayloadData(bytes, len);

            return frame;
        }

        private static void readPayloadDataAsync(
          Stream stream,
          WebSocketFrame frame,
          Action<WebSocketFrame> completed,
          Action<Exception> error
        )
        {
            ulong exactLen = frame.ExactPayloadLength;

            if (exactLen > PayloadData.MaxLength)
            {
                string? msg = "A frame has too long payload length.";

                throw new WebSocketException(CloseStatusCode.TooBig, msg);
            }

            if (exactLen == 0)
            {
                frame._payloadData = PayloadData.Empty;

                completed(frame);

                return;
            }

            long len = (long)exactLen;

            Action<byte[]> comp =
              bytes =>
              {
                  if (bytes.LongLength != len)
                  {
                      string? msg = "The payload data of a frame could not be read.";

                      throw new WebSocketException(msg);
                  }

                  frame._payloadData = new PayloadData(bytes, len);

                  completed(frame);
              };

            if (frame._payloadLength < 127)
            {
                stream.ReadBytesAsync((int)exactLen, comp, error);

                return;
            }

            stream.ReadBytesAsync(len, 1024, comp, error);
        }

        private static string utf8Decode(byte[] bytes)
        {
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Internal Methods

        internal static WebSocketFrame CreateCloseFrame(
          PayloadData payloadData, bool mask
        )
        {
            return new WebSocketFrame(
                     Fin.Final, Opcode.Close, payloadData, false, mask
                   );
        }

        internal static WebSocketFrame CreatePingFrame(bool mask)
        {
            return new WebSocketFrame(
                     Fin.Final, Opcode.Ping, PayloadData.Empty, false, mask
                   );
        }

        internal static WebSocketFrame CreatePingFrame(byte[] data, bool mask)
        {
            return new WebSocketFrame(
                     Fin.Final, Opcode.Ping, new PayloadData(data), false, mask
                   );
        }

        internal static WebSocketFrame CreatePongFrame(
          PayloadData payloadData, bool mask
        )
        {
            return new WebSocketFrame(
                     Fin.Final, Opcode.Pong, payloadData, false, mask
                   );
        }

        internal static WebSocketFrame ReadFrame(Stream stream, bool unmask)
        {
            WebSocketFrame? frame = readHeader(stream);

            readExtendedPayloadLength(stream, frame);
            readMaskingKey(stream, frame);
            readPayloadData(stream, frame);

            if (unmask)
                frame.Unmask();

            return frame;
        }

        internal static void ReadFrameAsync(
          Stream stream,
          bool unmask,
          Action<WebSocketFrame> completed,
          Action<Exception> error
        )
        {
            readHeaderAsync(
              stream,
              frame =>
                readExtendedPayloadLengthAsync(
                  stream,
                  frame,
                  frame1 =>
                    readMaskingKeyAsync(
                      stream,
                      frame1,
                      frame2 =>
                        readPayloadDataAsync(
                          stream,
                          frame2,
                          frame3 =>
                          {
                              if (unmask)
                                  frame3.Unmask();

                              completed(frame3);
                          },
                          error
                        ),
                      error
                    ),
                  error
                ),
              error
            );
        }

        internal void Unmask()
        {
            if (_mask == Mask.Off)
                return;

            _payloadData.Mask(_maskingKey);

            _maskingKey = WebSocket.EmptyBytes;
            _mask = Mask.Off;
        }

        #endregion

        #region Public Methods

        public IEnumerator<byte> GetEnumerator()
        {
            foreach (byte b in ToArray())
                yield return b;
        }

        public void Print(bool dumped)
        {
            string? val = dumped ? dump(this) : print(this);

            Console.WriteLine(val);
        }

        public string PrintToString(bool dumped)
        {
            return dumped ? dump(this) : print(this);
        }

        public byte[] ToArray()
        {
            using (MemoryStream? buff = new MemoryStream())
            {
                int header = (int)_fin;
                header = (header << 1) + (int)_rsv1;
                header = (header << 1) + (int)_rsv2;
                header = (header << 1) + (int)_rsv3;
                header = (header << 4) + (int)_opcode;
                header = (header << 1) + (int)_mask;
                header = (header << 7) + _payloadLength;

                ushort headerAsUshort = (ushort)header;
                byte[]? headerAsBytes = headerAsUshort.ToByteArray(ByteOrder.Big);

                buff.Write(headerAsBytes, 0, 2);

                if (_payloadLength > 125)
                {
                    int cnt = _payloadLength == 126 ? 2 : 8;

                    buff.Write(_extPayloadLength, 0, cnt);
                }

                if (_mask == Mask.On)
                    buff.Write(_maskingKey, 0, 4);

                if (_payloadLength > 0)
                {
                    byte[]? bytes = _payloadData.ToArray();

                    if (_payloadLength < 127)
                        buff.Write(bytes, 0, bytes.Length);
                    else
                        buff.WriteBytes(bytes, 1024);
                }

                buff.Close();

                return buff.ToArray();
            }
        }

        public override string ToString()
        {
            byte[]? val = ToArray();

            return BitConverter.ToString(val);
        }

        #endregion

        #region Explicit Interface Implementations

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion
    }
}
