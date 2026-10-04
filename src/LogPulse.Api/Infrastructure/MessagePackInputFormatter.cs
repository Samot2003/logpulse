using System.Runtime.ExceptionServices;
using LogPulse.Core.Contracts;
using MessagePack;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace LogPulse.Api.Infrastructure;

/// <summary>
/// Reads <c>application/x-msgpack</c> request bodies for the ingestion batches only. Unlike the stock formatter
/// package, a malformed or truncated payload becomes a model error (400) instead of an unhandled exception (500).
/// Other endpoints do not accept MessagePack at all.
/// </summary>
public sealed class MessagePackInputFormatter : InputFormatter
{
    public const string MediaType = "application/x-msgpack";

    private readonly MessagePackSerializerOptions _options;

    public MessagePackInputFormatter(MessagePackSerializerOptions options)
    {
        _options = options;
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse(MediaType));
    }

    protected override bool CanReadType(Type type) => type == typeof(IngestLogBatch) || type == typeof(IngestMetricBatch);

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var model = await MessagePackSerializer.DeserializeAsync(
                context.ModelType, context.HttpContext.Request.Body, _options, context.HttpContext.RequestAborted);
            return await InputFormatterResult.SuccessAsync(model);
        }
        catch (MessagePackSerializationException ex) when (ex.InnerException is BadHttpRequestException kestrel)
        {
            // MessagePack wraps stream errors; Kestrel's "body too large" (413) or "too slow" (408) must keep their
            // status. Only those: a truncated payload is also wrapped (EndOfStreamException) and is a plain 400.
            ExceptionDispatchInfo.Throw(kestrel);
            throw;
        }
        catch (MessagePackSerializationException)
        {
            context.ModelState.TryAddModelError(context.ModelName, "The request body is not a valid MessagePack payload for this endpoint.");
            return await InputFormatterResult.FailureAsync();
        }
    }
}
