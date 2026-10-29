using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// RFC 5389 long-term credentials require MD5 (the key) and HMAC-SHA1
// (MESSAGE-INTEGRITY).
#pragma warning disable CA5350, CA5351

namespace GGScale.IntegrationTests
{
    /// <summary>
    /// Just enough of a TURN client (RFC 5766, UDP, IPv4) to prove relay
    /// credentials work: Allocate with long-term credentials,
    /// CreatePermission, and Send and Data indications. Tests only.
    /// </summary>
    internal sealed class TurnClient : IDisposable
    {
        private const uint MagicCookie = 0x2112A442;

        private const ushort AllocateRequest = 0x0003;
        private const ushort AllocateSuccess = 0x0103;
        private const ushort CreatePermissionRequest = 0x0008;
        private const ushort CreatePermissionSuccess = 0x0108;
        private const ushort SendIndication = 0x0016;
        private const ushort DataIndication = 0x0017;

        private const ushort AttrUsername = 0x0006;
        private const ushort AttrMessageIntegrity = 0x0008;
        private const ushort AttrErrorCode = 0x0009;
        private const ushort AttrXorPeerAddress = 0x0012;
        private const ushort AttrData = 0x0013;
        private const ushort AttrRealm = 0x0014;
        private const ushort AttrNonce = 0x0015;
        private const ushort AttrXorRelayedAddress = 0x0016;
        private const ushort AttrRequestedTransport = 0x0019;

        private readonly UdpClient _udp;
        private readonly string _username;
        private byte[] _key = Array.Empty<byte>();
        private string _realm = "";
        private string _nonce = "";

        public IPEndPoint RelayedAddress { get; private set; } = null!;

        private TurnClient(UdpClient udp, string username)
        {
            _udp = udp;
            _username = username;
        }

        /// <summary>Allocates on <paramref name="url"/>, which must be one of creds.Urls.</summary>
        public static async Task<TurnClient> AllocateAsync(RelayCredentials creds, string url, CancellationToken ct)
        {
            if (!creds.Urls.Contains(url))
            {
                throw new ArgumentException($"{url} is not in the credentials' URLs", nameof(url));
            }
            // "turn:127.0.0.1:13479?transport=udp" -> 127.0.0.1:13479
            var server = IPEndPoint.Parse(url.Substring("turn:".Length).Split('?')[0]);
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(server);
            var turn = new TurnClient(udp, creds.Username);
            try
            {
                var transport = Attr(AttrRequestedTransport, new byte[] { 17, 0, 0, 0 });
                var challenge = await turn.TransactAsync(AllocateRequest, new[] { transport }, sign: false, ct);
                if (ErrorCode(challenge.Attrs) != 401)
                {
                    throw new InvalidOperationException("TURN Allocate: expected a 401 challenge");
                }
                turn._realm = Encoding.UTF8.GetString(challenge.Attrs[AttrRealm]);
                turn._nonce = Encoding.UTF8.GetString(challenge.Attrs[AttrNonce]);
                turn._key = MD5.HashData(Encoding.UTF8.GetBytes($"{creds.Username}:{turn._realm}:{creds.Password}"));

                var res = await turn.TransactAsync(AllocateRequest, new[] { transport }, sign: true, ct);
                Expect(res, AllocateSuccess, "Allocate");
                turn.RelayedAddress = XorAddress(res.Attrs[AttrXorRelayedAddress]);
                return turn;
            }
            catch
            {
                turn.Dispose();
                throw;
            }
        }

        public async Task CreatePermissionAsync(IPEndPoint peer, CancellationToken ct)
        {
            var res = await TransactAsync(CreatePermissionRequest, new[] { Attr(AttrXorPeerAddress, XorAddress(peer)) }, sign: true, ct);
            Expect(res, CreatePermissionSuccess, "CreatePermission");
        }

        public Task SendAsync(IPEndPoint peer, string data)
        {
            var msg = Build(SendIndication, NewTransactionId(), new[]
            {
                Attr(AttrXorPeerAddress, XorAddress(peer)),
                Attr(AttrData, Encoding.UTF8.GetBytes(data)),
            }, null);
            return _udp.SendAsync(msg, msg.Length);
        }

        /// <summary>Reads Data indications until one carries want.</summary>
        public async Task ReceiveAsync(string want, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                var msg = Parse((await _udp.ReceiveAsync(timeout.Token)).Buffer);
                if (msg.Type == DataIndication && Encoding.UTF8.GetString(msg.Attrs[AttrData]) == want)
                {
                    return;
                }
            }
        }

        public void Dispose() => _udp.Dispose();

        private sealed class Message
        {
            public ushort Type;
            public byte[] TransactionId = null!;
            public Dictionary<ushort, byte[]> Attrs = new Dictionary<ushort, byte[]>();
        }

        private async Task<Message> TransactAsync(ushort type, byte[][] attrs, bool sign, CancellationToken ct)
        {
            var tid = NewTransactionId();
            var all = new List<byte[]>(attrs);
            if (sign)
            {
                all.Add(Attr(AttrUsername, Encoding.UTF8.GetBytes(_username)));
                all.Add(Attr(AttrRealm, Encoding.UTF8.GetBytes(_realm)));
                all.Add(Attr(AttrNonce, Encoding.UTF8.GetBytes(_nonce)));
            }
            var req = Build(type, tid, all, sign ? _key : null);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _udp.SendAsync(req, req.Length);
            while (true)
            {
                var msg = Parse((await _udp.ReceiveAsync(timeout.Token)).Buffer);
                if (msg.TransactionId.AsSpan().SequenceEqual(tid))
                {
                    return msg;
                }
            }
        }

        private static void Expect(Message res, ushort type, string what)
        {
            if (res.Type != type)
            {
                throw new InvalidOperationException($"TURN {what} failed: type 0x{res.Type:x4}, error {ErrorCode(res.Attrs)}");
            }
        }

        private static int ErrorCode(Dictionary<ushort, byte[]> attrs) =>
            attrs.TryGetValue(AttrErrorCode, out var v) ? (v[2] & 0x7) * 100 + v[3] : 0;

        private static byte[] NewTransactionId() => RandomNumberGenerator.GetBytes(12);

        private static byte[] Attr(ushort type, byte[] value)
        {
            var buf = new byte[4 + ((value.Length + 3) & ~3)];
            BinaryPrimitives.WriteUInt16BigEndian(buf, type);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)value.Length);
            value.CopyTo(buf, 4);
            return buf;
        }

        // Build writes the header and attributes. With a key it appends
        // MESSAGE-INTEGRITY: HMAC-SHA1 over the message before it, with the
        // header length already counting the 24-byte attribute.
        private static byte[] Build(ushort type, byte[] tid, IEnumerable<byte[]> attrs, byte[]? key)
        {
            var body = new List<byte>();
            foreach (var a in attrs)
            {
                body.AddRange(a);
            }
            var msg = new byte[20 + body.Count + (key != null ? 24 : 0)];
            BinaryPrimitives.WriteUInt16BigEndian(msg, type);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)(msg.Length - 20));
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), MagicCookie);
            tid.CopyTo(msg, 8);
            body.CopyTo(msg, 20);
            if (key != null)
            {
                var signed = 20 + body.Count;
                Attr(AttrMessageIntegrity, HMACSHA1.HashData(key, msg.AsSpan(0, signed))).CopyTo(msg, signed);
            }
            return msg;
        }

        private static Message Parse(byte[] buf)
        {
            var msg = new Message
            {
                Type = BinaryPrimitives.ReadUInt16BigEndian(buf),
                TransactionId = buf[8..20],
            };
            var end = 20 + BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2));
            for (var i = 20; i + 4 <= end;)
            {
                var type = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(i));
                var len = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(i + 2));
                msg.Attrs[type] = buf[(i + 4)..(i + 4 + len)];
                i += 4 + ((len + 3) & ~3);
            }
            return msg;
        }

        // XOR-*-ADDRESS, IPv4: family 1, port XOR the cookie's top 16 bits,
        // address XOR the cookie.
        private static IPEndPoint XorAddress(byte[] v)
        {
            var port = BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(2)) ^ (MagicCookie >> 16);
            var ip = BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(4)) ^ MagicCookie;
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, ip);
            return new IPEndPoint(new IPAddress(bytes), (int)port);
        }

        private static byte[] XorAddress(IPEndPoint ep)
        {
            var v = new byte[8];
            v[1] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(v.AsSpan(2), (ushort)(ep.Port ^ (MagicCookie >> 16)));
            BinaryPrimitives.WriteUInt32BigEndian(v.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(ep.Address.GetAddressBytes()) ^ MagicCookie);
            return v;
        }
    }
}
