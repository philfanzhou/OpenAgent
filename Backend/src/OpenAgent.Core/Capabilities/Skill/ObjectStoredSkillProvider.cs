using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class ObjectStoredSkillProvider(
    IFileObjectStore objectStore,
    ISkillPackageReader packageReader,
    IHttpClientFactory httpClientFactory)
{
    internal async Task<StoredSkillDefinition> LoadAsync(
        SkillInstanceConfig instance,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.ObjectKey)
            || string.IsNullOrWhiteSpace(instance.PackageFileName))
        {
            throw new InvalidOperationException("Object-stored skills require an object key and package file name.");
        }

        byte[] content = await objectStore.ReadAsync(instance.ObjectKey, cancellationToken).ConfigureAwait(false);
        VerifyHash(instance, content);
        SkillPackageManifest manifest = packageReader.Read(instance.PackageFileName, content);
        var descriptor = new SkillDescriptor
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Description = string.IsNullOrWhiteSpace(instance.Description)
                ? manifest.Description
                : instance.Description,
            ParametersJsonSchema = string.IsNullOrWhiteSpace(instance.ParametersJsonSchema)
                ? manifest.ParametersJsonSchema
                : instance.ParametersJsonSchema,
            Source = SkillSource.Local,
            SourceId = instance.ObjectKey,
            AllowedUserIds = instance.AllowedUserIds,
            AllowedGroups = instance.AllowedGroups,
            AllowedTenantIds = instance.AllowedTenantIds,
            AllowedRoles = instance.AllowedRoles
        };
        return new StoredSkillDefinition(
            descriptor,
            (arguments, invocationCancellation) => ExecuteAsync(
                manifest.EndpointUrl,
                arguments,
                invocationCancellation));
    }

    private async Task<string> ExecuteAsync(
        string endpointUrl,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient("SkillEndpoint");
        string payload = JsonSerializer.Serialize(arguments);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(
            endpointUrl,
            content,
            cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? responseBody
            : $"Skill endpoint returned error: {response.StatusCode} - {responseBody}";
    }

    private static void VerifyHash(SkillInstanceConfig instance, byte[] content)
    {
        if (string.IsNullOrWhiteSpace(instance.Sha256))
        {
            return;
        }

        string actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actual, instance.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Skill package '{instance.Id}' failed its SHA-256 integrity check.");
        }
    }
}

internal sealed record StoredSkillDefinition(
    SkillDescriptor Descriptor,
    Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> ExecuteAsync);
