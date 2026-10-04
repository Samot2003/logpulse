using LogPulse.Api.Infrastructure;
using LogPulse.Core.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LogPulse.Tests.Unit;

public class MessagePackInputFormatterTests
{
    private static InputFormatterContext ContextFor(Stream body)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = body;
        httpContext.Request.ContentType = MessagePackInputFormatter.MediaType;
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(IngestLogBatch));
        return new InputFormatterContext(httpContext, "batch", new ModelStateDictionary(), metadata, (s, e) => new StreamReader(s, e));
    }

    /// <summary>Simulates Kestrel aborting the read because the body exceeds [RequestSizeLimit].</summary>
    private sealed class TooLargeStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_body_over_the_size_limit_keeps_its_413_instead_of_becoming_a_bad_payload()
    {
        var formatter = new MessagePackInputFormatter(IngestSerialization.MessagePackOptions);

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() => formatter.ReadRequestBodyAsync(ContextFor(new TooLargeStream())));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, error.StatusCode);
    }

    [Fact]
    public async Task A_truncated_payload_is_a_model_error()
    {
        var formatter = new MessagePackInputFormatter(IngestSerialization.MessagePackOptions);
        var context = ContextFor(new MemoryStream([0x81, 0xA7, (byte)'E']));

        var result = await formatter.ReadRequestBodyAsync(context);

        Assert.True(result.HasError);
        Assert.False(context.ModelState.IsValid);
    }
}
