using System.Text.Json;
using System.Text.Json.Serialization;
using LogPulse.Core.Contracts;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace LogPulse.Api.Infrastructure;

/// <summary>
/// Serializer settings for ingestion. Batch limits are enforced while reading, not only by validation afterwards:
/// otherwise a 4 MB body with millions of tiny items would be fully deserialized and validated item by item before
/// the [MaxLength] check rejects it. The whole batch object is read here (not just its list) so that a repeated
/// key, e.g. <c>{"entries":[...],"entries":[...]}</c>, cannot multiply the item budget of one request.
/// </summary>
public static class IngestSerialization
{
    /// <summary>A batch has a single meaningful key; anything bigger than this is not a real client.</summary>
    private const int MaxBatchKeys = 8;

    /// <summary>Contractless (string keys), hardened for untrusted input, with the batch limits built in.</summary>
    public static MessagePackSerializerOptions MessagePackOptions { get; } = MessagePackSerializerOptions.Standard
        .WithResolver(CompositeResolver.Create(
            [LogBatchFormatter.Instance, MetricBatchFormatter.Instance],
            [ContractlessStandardResolver.Instance]))
        .WithSecurity(MessagePackSecurity.UntrustedData);

    public static void AddBatchLimits(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new BatchJsonConverter<IngestLogBatch, IngestLogEntry>(
            nameof(IngestLogBatch.Entries), IngestLogBatch.MaxEntries, items => new IngestLogBatch { Entries = items! }, b => b.Entries));
        options.Converters.Add(new BatchJsonConverter<IngestMetricBatch, IngestMetricSample>(
            nameof(IngestMetricBatch.Samples), IngestMetricBatch.MaxSamples, items => new IngestMetricBatch { Samples = items! }, b => b.Samples));
    }

    /// <summary>
    /// Reads <c>{"&lt;items&gt;": [...]}</c>. Fails (400) on a repeated items key, on more than <c>maxItems</c> items,
    /// and on unexpected shapes. A missing key yields an empty list and a null value yields null, which validation
    /// then rejects with a clear message. Unknown properties are skipped without being materialized.
    /// </summary>
    public sealed class BatchJsonConverter<TBatch, TItem>(
        string itemsProperty, int maxItems, Func<List<TItem>?, TBatch> create, Func<TBatch, List<TItem>> items) : JsonConverter<TBatch>
        where TBatch : class
    {
        public override TBatch? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected an object.");
            }

            List<TItem>? list = [];
            var seen = false;
            var keys = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return create(list);
                }

                if (++keys > MaxBatchKeys)
                {
                    throw new JsonException("Too many properties.");
                }

                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, itemsProperty, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen)
                    {
                        throw new JsonException($"Duplicate property '{itemsProperty}'.");
                    }

                    seen = true;
                    list = ReadBoundedList(ref reader, options);
                }
                else
                {
                    reader.Skip();
                }
            }

            throw new JsonException("Unexpected end of the object.");
        }

        public override void Write(Utf8JsonWriter writer, TBatch value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(value);
            ArgumentNullException.ThrowIfNull(options);
            writer.WriteStartObject();
            writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(itemsProperty) ?? itemsProperty);
            JsonSerializer.Serialize(writer, items(value), options);
            writer.WriteEndObject();
        }

        private List<TItem>? ReadBoundedList(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Expected an array.");
            }

            var list = new List<TItem>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return list;
                }

                if (list.Count == maxItems)
                {
                    throw new JsonException($"At most {maxItems} items are allowed.");
                }

                try
                {
                    // Null items are kept so validation reports them with a clear message.
                    list.Add(JsonSerializer.Deserialize<TItem>(ref reader, options)!);
                }
                catch (JsonException ex)
                {
                    // Nested deserialization restarts the path at "$"; put the item back in it ("$.entries[3].severity").
                    var itemPath = $"$.{options.PropertyNamingPolicy?.ConvertName(itemsProperty) ?? itemsProperty}[{list.Count}]";
                    throw new JsonException(ex.Message, itemPath + ex.Path?.TrimStart('$'), ex.LineNumber, ex.BytePositionInLine, ex);
                }
            }

            throw new JsonException("Unexpected end of the array.");
        }
    }

    // MsgPack009 comes from the source generator, which would auto-register these formatters. They are registered
    // explicitly in the CompositeResolver above instead, for distinct types, so there is no ambiguity.
#pragma warning disable MsgPack009
    public sealed class LogBatchFormatter : BatchFormatter<IngestLogBatch, IngestLogEntry>
    {
        public static readonly LogBatchFormatter Instance = new();

        protected override string ItemsKey => nameof(IngestLogBatch.Entries);
        protected override int MaxItems => IngestLogBatch.MaxEntries;
        protected override IngestLogBatch Create(List<IngestLogEntry>? items) => new() { Entries = items! };
        protected override List<IngestLogEntry> Items(IngestLogBatch batch) => batch.Entries;
    }

    public sealed class MetricBatchFormatter : BatchFormatter<IngestMetricBatch, IngestMetricSample>
    {
        public static readonly MetricBatchFormatter Instance = new();

        protected override string ItemsKey => nameof(IngestMetricBatch.Samples);
        protected override int MaxItems => IngestMetricBatch.MaxSamples;
        protected override IngestMetricBatch Create(List<IngestMetricSample>? items) => new() { Samples = items! };
        protected override List<IngestMetricSample> Items(IngestMetricBatch batch) => batch.Samples;
    }
#pragma warning restore MsgPack009

    /// <summary>
    /// Reads a batch map. Rejects repeated item keys, oversized maps and arrays announcing more than the maximum
    /// (checked on the array header, before anything is allocated). Unknown keys are skipped.
    /// </summary>
    public abstract class BatchFormatter<TBatch, TItem> : IMessagePackFormatter<TBatch?>
        where TBatch : class
    {
        protected abstract string ItemsKey { get; }
        protected abstract int MaxItems { get; }
        protected abstract TBatch Create(List<TItem>? items);
        protected abstract List<TItem> Items(TBatch batch);

        public void Serialize(ref MessagePackWriter writer, TBatch? value, MessagePackSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            var items = Items(value);
            var formatter = options.Resolver.GetFormatterWithVerify<TItem>();
            writer.WriteMapHeader(1);
            writer.Write(ItemsKey);
            writer.WriteArrayHeader(items.Count);
            foreach (var item in items)
            {
                formatter.Serialize(ref writer, item, options);
            }
        }

        public TBatch? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (reader.TryReadNil())
            {
                return null;
            }

            var keys = reader.ReadMapHeader();
            if (keys > MaxBatchKeys)
            {
                throw new MessagePackSerializationException("Too many keys.");
            }

            List<TItem>? list = [];
            var seen = false;
            options.Security.DepthStep(ref reader);
            try
            {
                for (var i = 0; i < keys; i++)
                {
                    var key = reader.ReadString();
                    // Ordinal, like the contractless resolver that reads the items: a camelCase "entries" would
                    // otherwise be accepted here and every item silently deserialized with default values.
                    if (string.Equals(key, ItemsKey, StringComparison.Ordinal))
                    {
                        if (seen)
                        {
                            throw new MessagePackSerializationException($"Duplicate key '{ItemsKey}'.");
                        }

                        seen = true;
                        list = ReadBoundedList(ref reader, options);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
            finally
            {
                reader.Depth--;
            }

            return Create(list);
        }

        private List<TItem>? ReadBoundedList(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            var count = reader.ReadArrayHeader();
            if (count > MaxItems)
            {
                throw new MessagePackSerializationException($"At most {MaxItems} items are allowed.");
            }

            var formatter = options.Resolver.GetFormatterWithVerify<TItem>();
            var list = new List<TItem>(count);
            options.Security.DepthStep(ref reader);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    list.Add(formatter.Deserialize(ref reader, options));
                }
            }
            finally
            {
                reader.Depth--;
            }

            return list;
        }
    }
}
