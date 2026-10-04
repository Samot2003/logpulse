using LogPulse.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LogPulse.Tests.Unit;

public class BadRequestExceptionHandlerTests
{
    private sealed class RecordingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetails? Written { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written = context.ProblemDetails;
            return ValueTask.CompletedTask;
        }

        // The framework's default reports whether the response started, which never happens on a test HttpContext.
        public async ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            await WriteAsync(context);
            return true;
        }
    }

    [Fact]
    public async Task An_oversized_body_becomes_a_413_problem_instead_of_a_500()
    {
        var problems = new RecordingProblemDetailsService();
        var handler = new BadRequestExceptionHandler(problems);
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(
            context, new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, problems.Written!.Status);
        Assert.Equal("Request body too large", problems.Written.Title);
    }

    [Fact]
    public async Task Other_exceptions_are_left_to_the_default_handler()
    {
        var handler = new BadRequestExceptionHandler(new RecordingProblemDetailsService());

        Assert.False(await handler.TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException(), CancellationToken.None));
    }
}
