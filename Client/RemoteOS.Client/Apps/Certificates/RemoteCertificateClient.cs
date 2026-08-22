using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Client.Services.Auth;
using RemoteOS.Protocol.Certificates;
using RemoteOS.Protocol.Common;

namespace Client.Apps.Certificates;

/// <summary>
/// HTTP implementation of <see cref="IRemoteCertificateClient"/>. Mutating endpoints require an
/// Idempotency-Key header (server-enforced); each call generates a fresh key so retries never
/// duplicate an operation. 202 Accepted responses carry the operation dto in the body.
/// </summary>
public sealed class RemoteCertificateClient(HttpClient http, IAuthSession session) : IRemoteCertificateClient
{
    public Task<IReadOnlyList<CertificateDto>> ListAsync(CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<CertificateDto>>(HttpMethod.Get, CertificateApiRoutes.CollectionPattern, null, null, cancellationToken);

    public Task<CertificateDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CertificateDto?>(HttpMethod.Get, CertificateApiRoutes.ByIdPattern.Replace("{id:guid}", id.ToString("N")), null, null, cancellationToken);

    public Task<CertificatePreflightResultDto> PreflightAsync(CertificatePreflightRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificatePreflightResultDto>(HttpMethod.Post, CertificateApiRoutes.PreflightPattern, request, null, cancellationToken);

    public Task<CertificateOperationDto> RequestAsync(RequestCertificateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.CollectionPattern, request, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> DeployKestrelAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.DeployPattern.Replace("{id:guid}", id.ToString("N")), null, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> RenewAsync(Guid id, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.RenewPattern.Replace("{id:guid}", id.ToString("N")), null, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> RevokeAsync(Guid id, RevokeCertificateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Post, CertificateApiRoutes.RevokePattern.Replace("{id:guid}", id.ToString("N")), request, NewKey(), cancellationToken);

    public Task<CertificateOperationDto> DeleteAsync(Guid id, DeleteCertificateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto>(HttpMethod.Delete, CertificateApiRoutes.DeletePattern.Replace("{id:guid}", id.ToString("N")), request, NewKey(), cancellationToken);

    public Task<CertificateOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto?>(HttpMethod.Get, CertificateApiRoutes.OperationsPattern.Replace("{operationId:guid}", operationId.ToString("N")), null, null, cancellationToken);

    public Task<CertificateOperationDto?> CancelOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => SendAsync<CertificateOperationDto?>(HttpMethod.Post, CertificateApiRoutes.CancelOperationPattern.Replace("{operationId:guid}", operationId.ToString("N")), null, null, cancellationToken);

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException("RemoteOS session is not authenticated.");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')))
        {
            Content = body is null ? null : JsonContent.Create(body, options: RemoteOsJsonOptions.Default),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(request, cancellationToken);
        // 404 maps to default(T) so callers can distinguish "not found" from a transport error.
        if (response.StatusCode == HttpStatusCode.NotFound && default(T) is null)
            return default!;
        if (!response.IsSuccessStatusCode)
        {
            throw new CertificateApiException(await ReadProblemCodeAsync(response, cancellationToken)
                ?? FallbackProblemCode(response.StatusCode), response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(RemoteOsJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException("RemoteOS returned an empty response.");
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try { return JsonSerializer.Deserialize<CertificateProblemDetails>(payload, RemoteOsJsonOptions.Default)?.ProblemCode; }
        catch (JsonException) { return null; }
    }

    private static string FallbackProblemCode(HttpStatusCode statusCode) => $"certificate.http_{(int)statusCode}";

    private sealed record CertificateProblemDetails(string? ProblemCode);
}

/// <summary>Structured certificate API failure exposed to the view model as a stable problem code.</summary>
internal sealed class CertificateApiException(string problemCode, HttpStatusCode statusCode)
    : HttpRequestException(problemCode, null, statusCode)
{
    public string ProblemCode { get; } = problemCode;
}
