using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Client;
using TabularModelDeployer.Api.Models;

namespace TabularModelDeployer.Api.Services;

/// <summary>
/// Binds DirectQuery credentials via the Power BI REST API — the step TOM's
/// inline Credential does not perform. Without this, SaveChanges() succeeds
/// and tables/schema appear, but no datasource object is registered for the
/// Service to discover, so queries return no data.
/// </summary>
public class PowerBiRestCredentialService
{
    private readonly IConfiguration _config;
    private readonly HttpClient _http;

    public PowerBiRestCredentialService(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _http = httpClientFactory.CreateClient();
        _http.BaseAddress = new Uri("https://api.powerbi.com/v1.0/myorg/");
    }

    public async Task BindCredentialsAsync(
        string workspaceName,
        string? workspaceId,
        string modelName,
        SourceCredential credential)
    {
        var token = await GetAccessTokenAsync();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var groupId = workspaceId ?? await ResolveGroupIdAsync(workspaceName);
        var datasetId = await ResolveDatasetIdAsync(groupId, modelName);

        // TakeOver is often required before a service principal can update
        // credentials on a dataset it didn't originally publish. Non-fatal
        // if it fails — some tenants/datasets don't need it.
        try
        {
            await _http.PostAsync($"groups/{groupId}/datasets/{datasetId}/Default.TakeOver", null);
        }
        catch
        {
            // best-effort — continue regardless
        }

        var (gatewayId, datasourceId) = await ResolveDatasourceAsync(groupId, datasetId);

        await UpdateDatasourceCredentialsAsync(gatewayId, datasourceId, credential);
    }

    private async Task<string> GetAccessTokenAsync()
    {
        var tenantId = _config["PowerBI:TenantId"];
        var clientId = _config["PowerBI:ClientId"];
        var clientSecret = _config["PowerBI:ClientSecret"];

        var app = ConfidentialClientApplicationBuilder.Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .Build();

        var result = await app.AcquireTokenForClient(
            new[] { "https://analysis.windows.net/powerbi/api/.default" }
        ).ExecuteAsync();

        return result.AccessToken;
    }

    private async Task<string> ResolveGroupIdAsync(string workspaceName)
    {
        var resp = await _http.GetAsync($"groups?$filter=name eq '{Uri.EscapeDataString(workspaceName)}'");
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var groups = doc.GetProperty("value");
        if (groups.GetArrayLength() == 0)
            throw new InvalidOperationException($"Workspace '{workspaceName}' not found via Groups API.");

        return groups[0].GetProperty("id").GetString()!;
    }

    private async Task<string> ResolveDatasetIdAsync(string groupId, string modelName)
    {
        var resp = await _http.GetAsync($"groups/{groupId}/datasets");
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var ds in doc.GetProperty("value").EnumerateArray())
        {
            if (string.Equals(ds.GetProperty("name").GetString(), modelName, StringComparison.OrdinalIgnoreCase))
                return ds.GetProperty("id").GetString()!;
        }

        throw new InvalidOperationException($"Dataset '{modelName}' not found in workspace '{groupId}'.");
    }

    private async Task<(string gatewayId, string datasourceId)> ResolveDatasourceAsync(string groupId, string datasetId)
    {
        var resp = await _http.GetAsync($"groups/{groupId}/datasets/{datasetId}/datasources");
        resp.EnsureSuccessStatusCode();

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var sources = doc.GetProperty("value");

        if (sources.GetArrayLength() == 0)
            throw new InvalidOperationException(
                $"No datasource registered for dataset '{datasetId}'. " +
                "This confirms the TOM deploy did not register a discoverable datasource object.");

        var first = sources[0];
        var gatewayId = first.TryGetProperty("gatewayId", out var g) ? g.GetString() : null;
        var datasourceId = first.GetProperty("datasourceId").GetString()!;

        if (string.IsNullOrEmpty(gatewayId))
            throw new InvalidOperationException("Datasource returned no gatewayId — unexpected for this API shape.");

        return (gatewayId, datasourceId);
    }

    private async Task UpdateDatasourceCredentialsAsync(string gatewayId, string datasourceId, SourceCredential credential)
    {
        // NOTE: Normally credentials must be RSA-encrypted with the gateway's
        // public key (GET .../gateways/{gatewayId} → publicKey). For
        // gateway-less/cloud DirectQuery sources, "NotEncrypted" is the
        // commonly-documented path. If the API rejects this (400/403), the
        // fallback is: fetch the gateway public key and RSA-OAEP encrypt the
        // credentialData JSON before sending — flag this back if it fails.
        var credentialData = new[]
        {
            new { name = "username", value = credential.Values.GetValueOrDefault("username", "") },
            new { name = "password", value = credential.Values.GetValueOrDefault("password", "") },
        };

        var body = new
        {
            credentialDetails = new
            {
                credentialType = "Basic",
                credentials = JsonSerializer.Serialize(new { credentialData }),
                encryptedConnection = "NotEncrypted",
                encryptionAlgorithm = "None",
                privacyLevel = "Organizational",
            }
        };

        var resp = await _http.PatchAsync(
            $"gateways/{gatewayId}/datasources/{datasourceId}",
            JsonContent.Create(body));

        // var resp = await _http.PostAsync(
        //     $"gateways/{gatewayId}/datasources/{datasourceId}/Default.UpdateDatasourceCredentials",
        //     JsonContent.Create(body));

        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"UpdateDatasourceCredentials failed ({(int)resp.StatusCode}): {errorBody}");
        }
    }
}