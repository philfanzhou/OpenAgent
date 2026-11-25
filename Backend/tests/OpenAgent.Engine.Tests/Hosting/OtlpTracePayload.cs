using System.Text;

namespace OpenAgent.Engine.Tests.Hosting;

internal static class OtlpTracePayload
{
    internal sealed record Span(string Scope, string Name, string TraceId, string SpanId, string? ParentId,
        IReadOnlyDictionary<string, string> Tags);

    // Decode the length-delimited fields used by the OTLP span assertions.
    internal static IEnumerable<Span> Read(byte[] body)
    {
        foreach (byte[] scopeSpans in Fields(body, 1).SelectMany(resource => Fields(resource, 2)))
        {
            string scope = Text(Fields(scopeSpans, 1).SelectMany(value => Fields(value, 1)));
            foreach (byte[] span in Fields(scopeSpans, 2))
            {
                var tags = Fields(span, 9).ToDictionary(
                    tag => Text(Fields(tag, 1)),
                    tag => Text(Fields(tag, 2).SelectMany(value => Fields(value, 1))),
                    StringComparer.Ordinal);
                yield return new Span(scope, Text(Fields(span, 5)), Hex(Fields(span, 1).Single()),
                    Hex(Fields(span, 2).Single()), Fields(span, 4).Select(Hex).SingleOrDefault(), tags);
            }
        }
    }

    private static string Text(IEnumerable<byte[]> field) => Encoding.UTF8.GetString(field.SingleOrDefault() ?? []);

    private static string Hex(byte[] value) => Convert.ToHexStringLower(value);

    private static IEnumerable<byte[]> Fields(byte[] bytes, int fieldNumber)
    {
        using BinaryReader reader = new(new MemoryStream(bytes));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            int key = reader.Read7BitEncodedInt();
            switch (key & 7)
            {
                case 0: reader.Read7BitEncodedInt64(); break;
                case 1: reader.ReadUInt64(); break;
                case 2:
                    byte[] value = reader.ReadBytes(reader.Read7BitEncodedInt());
                    if ((key >> 3) == fieldNumber)
                        yield return value;
                    break;
                case 5: reader.ReadUInt32(); break;
                default: throw new InvalidDataException($"Unexpected protobuf wire type: {key & 7}.");
            }
        }
    }
}
