using System.Formats.Asn1;
using System.Text;

namespace Oadm.Plugins.Est.Protocol;

/// <summary>
/// The bodies of RFC 7030: certificates as a PKCS#7 "certs-only" message (a SignedData without content or signers), and
/// requests (PKCS#10) — both base64 (RFC 7030 section 4: Content-Transfer-Encoding base64).
/// </summary>
public static class EstMessages
{
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string DataOid = "1.2.840.113549.1.7.1";

    /// <summary>The content type of the answers of cacerts, simpleenroll and simplereenroll.</summary>
    public const string CertsOnlyContentType = "application/pkcs7-mime; smime-type=certs-only";

    /// <summary>The content type of cacerts (RFC 7030 4.1.3).</summary>
    public const string CaCertsContentType = "application/pkcs7-mime";

    /// <summary>PKCS#7 certs-only (DER) of <paramref name="certificates"/> (DER), in order.</summary>
    public static byte[] CertsOnly(IEnumerable<byte[]> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(SignedDataOid);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1); // version
                using (writer.PushSetOf())
                {
                    // no digest algorithms
                }

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(DataOid);
                }

                // certificates [0] IMPLICIT SET OF Certificate; DER sorts a SET OF, so it is written by hand in the given order
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
                {
                    foreach (var certificate in certificates)
                    {
                        writer.WriteEncodedValue(certificate);
                    }
                }

                using (writer.PushSetOf())
                {
                    // no signers
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>The certificates (DER) of a certs-only message, in order (tests, the camera side).</summary>
    public static IReadOnlyList<byte[]> ReadCertsOnly(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
        var content = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
        if (content.ReadObjectIdentifier() != SignedDataOid)
        {
            throw new FormatException("Not a PKCS#7 SignedData.");
        }

        var signedData = content.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signedData.ReadInteger();
        signedData.ReadSetOf();
        signedData.ReadSequence();
        var certificates = new List<byte[]>();
        var tag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(tag))
        {
            var set = signedData.ReadSetOf(skipSortOrderValidation: true, expectedTag: tag);
            while (set.HasData)
            {
                certificates.Add(set.ReadEncodedValue().ToArray());
            }
        }

        return certificates;
    }

    /// <summary>Base64 wrapped at 76 characters with CRLF (the camera's EST client expects lines).</summary>
    public static byte[] Base64(byte[] der)
    {
        ArgumentNullException.ThrowIfNull(der);
        var text = Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks);
        return Encoding.ASCII.GetBytes(text + "\r\n");
    }

    /// <summary>
    /// The PKCS#10 request (DER) of an enroll body: base64 with any whitespace, or a PEM block. Throws
    /// <see cref="FormatException"/> for anything else.
    /// </summary>
    public static byte[] ReadRequest(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = Encoding.ASCII.GetString(body);
        var begin = text.IndexOf("-----BEGIN", StringComparison.Ordinal);
        if (begin >= 0)
        {
            var afterHeader = text.IndexOf('\n', begin);
            var end = text.IndexOf("-----END", StringComparison.Ordinal);
            if (afterHeader < 0 || end < afterHeader)
            {
                throw new FormatException("The request is not valid PEM.");
            }

            text = text[(afterHeader + 1)..end];
        }

        var base64 = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                base64.Append(c);
            }
        }

        var der = Convert.FromBase64String(base64.ToString());
        if (der.Length == 0)
        {
            throw new FormatException("The request is empty.");
        }

        return der;
    }
}
