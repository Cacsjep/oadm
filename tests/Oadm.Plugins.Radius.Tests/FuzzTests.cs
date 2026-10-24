using System.Net;
using System.Text;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Protocol.Text;
using Oadm.Plugins.Radius.Tests.Support;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// Fuzz tests of the RADIUS, EAP and EAP-TLS parsers (docs/spec/radius-server.md "Tests"): random datagrams and requests
/// mutated from real conversations, deterministic seeds, bounded loops. Never an exception outside the parser, never a
/// hang, never more open sessions than the requests made, and every answer is a valid, signed RADIUS packet.
/// </summary>
public sealed class FuzzTests
{
    private static readonly RadiusClient Client = new("switch-1", TestSwitch.DefaultSecret);
    private static readonly IPEndPoint Source = new(IPAddress.Parse("10.0.0.2"), 51812);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomDatagramsAreDroppedWithoutException(int seed)
    {
        var random = new Random(seed);
        var processor = new RadiusProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity }, new TestValidator());
        for (var i = 0; i < 3000; i++)
        {
            var bytes = new byte[random.Next(0, 4200)];
            random.NextBytes(bytes);
            if (random.Next(2) == 0 && bytes.Length >= 20)
            {
                // A plausible header: a request code and a fitting length, so the attribute parser runs.
                bytes[0] = random.Next(2) == 0 ? (byte)1 : (byte)12;
                var length = Math.Min(bytes.Length, 4096);
                bytes[2] = (byte)(length >> 8);
                bytes[3] = (byte)length;
            }

            var result = processor.Process(bytes, Client, Source);
            Assert.Equal(RadiusOutcome.Dropped, result.Outcome);
            Assert.Contains(result.Drop, new[] { RadiusDropReason.Malformed, RadiusDropReason.NotARequest, RadiusDropReason.WrongSecret, RadiusDropReason.NotEap });
        }

        Assert.Equal(0, processor.SessionCount);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void TheParsersNeverThrow(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 20_000; i++)
        {
            var bytes = new byte[random.Next(0, 600)];
            random.NextBytes(bytes);
            if (bytes.Length >= 4 && random.Next(2) == 0)
            {
                bytes[2] = (byte)(bytes.Length >> 8);
                bytes[3] = (byte)bytes.Length;
            }

            var radius = RadiusPacket.Parse(bytes);
            if (radius.Packet is { } packet)
            {
                _ = AccessRequest.Read(packet);
                _ = RadiusAuthenticators.VerifyRequest(packet, TestSwitch.DefaultSecret);
            }

            _ = EapPacket.Parse(bytes);
            _ = EapTlsMessage.Parse(bytes);
            _ = MacAddress.Parse(Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 40))));
            _ = RadiusText.Decode(bytes);
        }
    }

    [Theory]
    [InlineData(100, 12)]
    [InlineData(101, 13)]
    [InlineData(102, 12)]
    [InlineData(103, 13)]
    public void MutatedRequestsOfARealConversationNeverBreakTheServer(int seed, int version)
    {
        var random = new Random(seed);

        // How many rounds a clean conversation takes (the camera fragments its flight so there are many steps).
        Conversation(version, out var probe);
        var length = probe.Run(new TestSupplicant(Camera(version))).RoundTrips;
        Assert.True(length >= 4);

        for (var i = 0; i < 60; i++)
        {
            var processor = Conversation(version, out var radiusSwitch);
            var camera = new TestSupplicant(Camera(version));
            var stopAt = random.Next(1, length);
            var paused = radiusSwitch.Run(camera, stopWhen: r => radiusSwitch.SentEap.Count >= stopAt);
            if (paused.Final is not null)
            {
                continue;
            }

            // The camera's next message, mutated in its EAP bytes, its attributes or both, then signed by the switch.
            var eap = MutateEap(random, radiusSwitch.NextEap!);
            var (result, answer, _) = radiusSwitch.Send(eap, edit: attributes => MutateAttributes(random, attributes));
            Check(result, answer);

            // Whatever happened, the real message afterwards never breaks anything either.
            var (after, afterAnswer, _) = radiusSwitch.Send(radiusSwitch.NextEap!);
            Check(after, afterAnswer);
            Assert.True(processor.SessionCount <= 1, $"{processor.SessionCount} sessions");
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void MutatedEapTlsFragmentsInALiveSessionFailCleanly(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 80; i++)
        {
            var processor = Conversation(12, out var radiusSwitch);
            var camera = new TestSupplicant(Camera(12));
            var stopAt = random.Next(2, 6);
            var paused = radiusSwitch.Run(camera, stopWhen: _ => radiusSwitch.SentEap.Count >= stopAt);
            if (paused.Final is not null)
            {
                continue;
            }

            var real = EapPacket.Parse(radiusSwitch.NextEap!).Packet!;
            var message = EapTlsMessage.Parse(real.Data.Span)!;
            var data = message.Data.ToArray();
            if (data.Length > 0 && random.Next(2) == 0)
            {
                data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
            }

            var flags = (EapTlsBits)(random.Next(8) << 5);
            uint? total = (flags & EapTlsBits.Length) != 0 ? (uint)random.Next(0, 70_000) : null;
            var mutated = EapPacket.Response(real.Identifier, EapType.Tls, new EapTlsMessage(flags, total, data).ToBytes()).ToBytes();

            var (result, answer, _) = radiusSwitch.Send(mutated);
            Check(result, answer);
            Assert.True(processor.SessionCount <= 1);
            _ = paused;
        }
    }

    private static SupplicantOptions Camera(int version) => new()
    {
        Versions = version == 13 ? [ProtocolVersion.TLSv13] : [ProtocolVersion.TLSv12],
        FragmentSize = 300,
    };

    private static RadiusProcessor Conversation(int version, out TestSwitch radiusSwitch)
    {
        var processor = new RadiusProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentityRsa, LargestFragment = 500 }, new TestValidator());
        radiusSwitch = new TestSwitch(processor);
        _ = version;
        return processor;
    }

    /// <summary>An answer is a valid packet the switch accepts (checked by TestSwitch.Check, which throws otherwise), or none.</summary>
    private static void Check(RadiusResult result, RadiusPacket? answer)
    {
        Assert.Equal(result.Response is null, answer is null);
        if (answer is not null)
        {
            Assert.Contains(answer.Code, new[] { RadiusCode.AccessChallenge, RadiusCode.AccessAccept, RadiusCode.AccessReject });
        }
    }

    private static byte[] MutateEap(Random random, byte[] eap)
    {
        var bytes = eap.ToArray();
        switch (random.Next(6))
        {
            case 0 when bytes.Length > 0:
                bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                return bytes;
            case 1 when bytes.Length > 1:
                return bytes[..random.Next(1, bytes.Length)];
            case 2:
                return [.. bytes, .. Enumerable.Range(0, random.Next(1, 40)).Select(_ => (byte)random.Next(256))];
            case 3 when bytes.Length >= 4:
                bytes[2] = (byte)random.Next(256);
                bytes[3] = (byte)random.Next(256);
                return bytes;
            case 4 when bytes.Length >= 6:
                bytes[5] = (byte)random.Next(256); // the EAP-TLS flags
                return bytes;
            default:
                return bytes;
        }
    }

    private static void MutateAttributes(Random random, List<RadiusAttribute> attributes)
    {
        switch (random.Next(7))
        {
            case 0:
                attributes.RemoveAt(random.Next(attributes.Count));
                break;
            case 1:
                attributes.Add(attributes[random.Next(attributes.Count)]);
                break;
            case 2:
                var index = random.Next(attributes.Count);
                var value = new byte[random.Next(0, 254)];
                random.NextBytes(value);
                attributes[index] = new RadiusAttribute(attributes[index].Type, value);
                break;
            case 3:
                attributes.Reverse();
                break;
            case 4:
                attributes.Insert(random.Next(attributes.Count), new RadiusAttribute((byte)random.Next(1, 256), new byte[random.Next(0, 30)]));
                break;
            case 5:
                attributes.RemoveAll(a => a.Type == RadiusAttributeType.State);
                break;
        }

        attributes.RemoveAll(a => a.Type == RadiusAttributeType.MessageAuthenticator);
        while (attributes.Sum(a => 2 + a.Value.Length) > 4000)
        {
            attributes.RemoveAt(attributes.Count - 1);
        }
    }
}
