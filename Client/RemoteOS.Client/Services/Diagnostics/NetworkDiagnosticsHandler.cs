using System.Diagnostics;
using RemoteOS.AppSDK;

namespace Client.Services.Diagnostics;

/// <summary>Records a result summary while preserving HttpClient's streaming and exception semantics.</summary>
public sealed class NetworkDiagnosticsHandler(NetworkDiagnosticsService diagnostics, string source) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!diagnostics.ShouldCapture(request.RequestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var requestHeaders = NetworkDiagnosticsService.SanitizeHeaders(request.Headers);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var contentType = response.Content?.Headers.ContentType?.MediaType;
            diagnostics.Record(new NetworkDiagnosticEntry(
                0, startedAt, stopwatch.Elapsed, NetworkDiagnosticKind.Http, source,
                request.RequestUri?.AbsolutePath ?? request.Method.Method, request.Method.Method,
                NetworkDiagnosticsService.SanitizePathAndQuery(request.RequestUri),
                response.IsSuccessStatusCode ? NetworkDiagnosticOutcome.Succeeded : NetworkDiagnosticOutcome.Failed,
                (int)response.StatusCode, contentType, response.Content?.Headers.ContentLength,
                NetworkDiagnosticsService.IsMediaContent(contentType, request.RequestUri), null,
                requestHeaders, NetworkDiagnosticsService.SanitizeHeaders(response.Headers)));
            return response;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var outcome = exception is OperationCanceledException
                ? NetworkDiagnosticOutcome.Cancelled : NetworkDiagnosticOutcome.TransportError;
            diagnostics.Record(new NetworkDiagnosticEntry(
                0, startedAt, stopwatch.Elapsed, NetworkDiagnosticKind.Http, source,
                request.RequestUri?.AbsolutePath ?? request.Method.Method, request.Method.Method,
                NetworkDiagnosticsService.SanitizePathAndQuery(request.RequestUri), outcome, null,
                null, null, false, NetworkDiagnosticsService.ErrorKind(exception), requestHeaders));
            throw;
        }
    }
}
