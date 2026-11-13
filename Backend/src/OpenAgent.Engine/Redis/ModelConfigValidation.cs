using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Redis;

internal static class ModelConfigValidation
{
    internal static bool IsValid(OpenCodeModelConfig model) =>
        IsValidConnection(model.ModelId, model.Endpoint, model.ContextTokens, model.Format);

    internal static bool IsValid(LlmProviderProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.TenantId)
        && !string.IsNullOrWhiteSpace(profile.Id)
        && Enum.IsDefined(profile.Modality)
        && IsValidConnection(profile.ModelId, profile.Endpoint, profile.ContextTokens, profile.Format);

    private static bool IsValidConnection(string modelId, string endpointValue, int contextTokens, ApiFormat format) =>
        !string.IsNullOrWhiteSpace(modelId)
        && Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpoint)
        && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
        && contextTokens > 0
        && Enum.IsDefined(format);
}
