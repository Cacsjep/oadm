using System.Net;
using System.Security.Cryptography;
using System.Text;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Sessions;

namespace Oadm.Plugins.Radius.Tests.Support;

/// <summary>What a conversation ended with.</summary>
public sealed record Conversation(RadiusCode? Final, int RoundTrips, byte[]? MskFromAccept, RadiusPacket? LastAnswer, IReadOnlyList<RadiusResult> Results)
{
    public EapSessionSnapshot? Session => Results.LastOrDefault(r => r.Session is not null)?.Session;
}

/// <summary>
/// The switch of the tests (an IEEE 802.1X authenticator, RFC 3579 pass-through): wraps the camera's EAP responses into
/// Access-Requests with a Message-Authenticator, State, port and MAC attributes, sends them to the processor, checks every
/// answer as a switch must (Response Authenticator, Message-Authenticator first, Proxy-State echoed) and passes the
/// EAP-Message back to the camera. At the end it decrypts MS-MPPE-Recv-Key and Send-Key.
/// </summary>
public sealed class TestSwitch(RadiusProcessor processor)
{
    public static readonly byte[] DefaultSecret = Encoding.ASCII.GetBytes("a-shared-secret-of-24-ch");

    private byte _identifier = (byte)RandomNumberGenerator.GetInt32(256);

    public RadiusClient Client { get; init; } = new("switch-1", DefaultSecret);

    public IPEndPoint Source { get; init; } = new(IPAddress.Parse("10.0.0.2"), 51812);

    public int? FramedMtu { get; init; } = 1500;

    /// <summary>Framed-MTU and NAS-Port-Type only in the first request of a conversation (many switches do so).</summary>
    public bool PortAttributesOnlyFirst { get; init; }

    /// <summary>Changes the attributes of every request from now on (e.g. large Proxy-States for the last one).</summary>
    public Action<List<RadiusAttribute>>? EditEach { get; set; }

    public string? CallingStationId { get; init; } = "AC-CC-8E-12-34-56";

    public string? NasPortId { get; init; } = "GigabitEthernet1/0/5";

    public uint? NasPortType { get; init; } = 15;

    public IReadOnlyList<byte[]> ProxyStates { get; init; } = [];

    /// <summary>Start with an EAP-Start (RFC 3579 2.1) instead of the camera's identity.</summary>
    public bool StartWithEapStart { get; init; }

    /// <summary>The State of the running conversation.</summary>
    public byte[]? State { get; set; }

    /// <summary>Every EAP packet this switch sent, in order.</summary>
    public List<byte[]> SentEap { get; } = [];

    /// <summary>The camera's next answer when a conversation was paused.</summary>
    public byte[]? NextEap { get; private set; }

    /// <summary>Runs a whole conversation between the camera and the processor.</summary>
    /// <summary>
    /// Runs a conversation between the camera and the processor. <paramref name="stopWhen"/> pauses it after a result
    /// (<see cref="NextEap"/> then holds the camera's next answer); <paramref name="resume"/> continues from there.
    /// </summary>
    public Conversation Run(TestSupplicant camera, int maxRoundTrips = 200, Func<RadiusResult, bool>? stopWhen = null, bool resume = false)
    {
        ArgumentNullException.ThrowIfNull(camera);
        List<RadiusResult> results = [];
        byte[] eap;
        if (resume && NextEap is { } next)
        {
            eap = next;
        }
        else
        {
            State = null;
            var identity = Encoding.UTF8.GetBytes("axis-accc8e123456");
            eap = StartWithEapStart ? [] : EapPacket.Response(1, EapType.Identity, identity).ToBytes();
        }

        for (var round = 1; round <= maxRoundTrips; round++)
        {
            var (result, answer, authenticator) = Send(eap);
            results.Add(result);
            if (answer is null)
            {
                return new Conversation(null, round, null, null, results);
            }

            var eapBack = Join(answer);
            var packet = eapBack.Length > 0 ? EapPacket.Parse(eapBack).Packet : null;
            switch (answer.Code)
            {
                case RadiusCode.AccessChallenge:
                    State = answer.Find(RadiusAttributeType.State)?.Value.ToArray() ?? throw new InvalidOperationException("Challenge without State");
                    var response = camera.Respond(packet ?? throw new InvalidOperationException("Challenge without EAP"));
                    eap = response!.ToBytes();
                    NextEap = eap;
                    if (stopWhen?.Invoke(result) == true)
                    {
                        return new Conversation(null, round, null, answer, results);
                    }

                    break;
                case RadiusCode.AccessAccept:
                    if (packet is not null)
                    {
                        camera.Respond(packet);
                    }

                    return new Conversation(RadiusCode.AccessAccept, round, DecryptMsk(answer, authenticator), answer, results);
                default:
                    if (packet is not null)
                    {
                        camera.Respond(packet);
                    }

                    return new Conversation(answer.Code, round, null, answer, results);
            }
        }

        throw new InvalidOperationException("The conversation did not end.");
    }

    /// <summary>Sends one EAP packet (empty for EAP-Start) as an Access-Request; returns the checked answer.</summary>
    public (RadiusResult Result, RadiusPacket? Answer, byte[] Authenticator) Send(byte[] eap, bool withState = true, Action<List<RadiusAttribute>>? edit = null)
    {
        var authenticator = RandomNumberGenerator.GetBytes(16);
        SentEap.Add(eap);
        var request = Build(eap, authenticator, withState, edit);
        var result = processor.Process(request, Client, Source);
        return (result, Check(result, authenticator, request), authenticator);
    }

    /// <summary>An Access-Request of this switch around <paramref name="eap"/>.</summary>
    public byte[] Build(byte[] eap, byte[] authenticator, bool withState = true, Action<List<RadiusAttribute>>? edit = null)
    {
        List<RadiusAttribute> attributes = [RadiusAttribute.Text(RadiusAttributeType.UserName, "axis-accc8e123456")];
        attributes.Add(RadiusAttribute.Of(RadiusAttributeType.NasIpAddress, [10, 0, 0, 2]));
        if (NasPortId is not null)
        {
            attributes.Add(RadiusAttribute.Text(RadiusAttributeType.NasPortId, NasPortId));
        }

        var first = !withState || State is null;
        if (NasPortType is { } type && (first || !PortAttributesOnlyFirst))
        {
            attributes.Add(RadiusAttribute.Number(RadiusAttributeType.NasPortType, type));
        }

        if (CallingStationId is not null)
        {
            attributes.Add(RadiusAttribute.Text(RadiusAttributeType.CallingStationId, CallingStationId));
        }

        if (FramedMtu is { } mtu && (first || !PortAttributesOnlyFirst))
        {
            attributes.Add(RadiusAttribute.Number(RadiusAttributeType.FramedMtu, (uint)mtu));
        }

        attributes.AddRange(RadiusAttribute.EapMessages(eap));
        if (withState && State is not null)
        {
            attributes.Add(new RadiusAttribute(RadiusAttributeType.State, State));
        }

        attributes.AddRange(ProxyStates.Select(p => new RadiusAttribute(RadiusAttributeType.ProxyState, p)));
        (edit ?? EditEach)?.Invoke(attributes);
        return RadiusPacketWriter.Request(RadiusCode.AccessRequest, _identifier++, authenticator, Client.Secret.Span, attributes)!;
    }

    /// <summary>Checks an answer as a switch does; throws when it is wrong. Null when nothing came back.</summary>
    public RadiusPacket? Check(RadiusResult result, byte[] authenticator, byte[] request)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Response is null)
        {
            return null;
        }

        var answer = RadiusPacket.Parse(result.Response).Packet ?? throw new InvalidOperationException("Answer is not a RADIUS packet");
        if (answer.Identifier != request[1])
        {
            throw new InvalidOperationException("Answer with another identifier");
        }

        if (!RadiusAuthenticators.VerifyResponse(answer, authenticator, Client.Secret.Span))
        {
            throw new InvalidOperationException("Answer with a wrong Response Authenticator or Message-Authenticator");
        }

        if (answer.Attributes[0].Type != RadiusAttributeType.MessageAuthenticator)
        {
            throw new InvalidOperationException("Message-Authenticator is not the first attribute");
        }

        var echoed = answer.All(RadiusAttributeType.ProxyState).Select(a => Convert.ToHexString(a.Value.Span)).ToList();
        var sent = RadiusPacket.Parse(request).Packet!.All(RadiusAttributeType.ProxyState).Select(a => Convert.ToHexString(a.Value.Span));
        if (!echoed.SequenceEqual(sent))
        {
            throw new InvalidOperationException("Proxy-State not echoed in order");
        }

        return answer;
    }

    /// <summary>The joined EAP-Message attributes of an answer.</summary>
    public static byte[] Join(RadiusPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return [.. packet.All(RadiusAttributeType.EapMessage).SelectMany(a => a.Value.ToArray())];
    }

    /// <summary>MS-MPPE-Recv-Key + MS-MPPE-Send-Key decrypted (RFC 2548 2.4.2): the MSK as the switch gets it.</summary>
    public byte[]? DecryptMsk(RadiusPacket accept, byte[] authenticator)
    {
        ArgumentNullException.ThrowIfNull(accept);
        byte[]? recv = null;
        byte[]? send = null;
        foreach (var vendor in accept.All(RadiusAttributeType.VendorSpecific))
        {
            var value = vendor.Value.Span;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(value) != MppeKeys.MicrosoftVendorId)
            {
                continue;
            }

            var type = value[4];
            var salt = value.Slice(6, 2);
            if ((salt[0] & 0x80) == 0)
            {
                throw new InvalidOperationException("Salt without its most significant bit");
            }

            var key = MppeKeys.Decrypt(value[8..], Client.Secret.Span, authenticator, salt);
            if (type == MppeKeys.RecvKeyType)
            {
                recv = key;
            }
            else if (type == MppeKeys.SendKeyType)
            {
                send = key;
            }
        }

        return recv is null || send is null ? null : [.. recv, .. send];
    }
}
