using System.Security.Cryptography;
using System.Text;

namespace OpenAgent.Contracts.Files;

public static class FileObjectTenantScope
{
    public static string CreatePartition(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId))).ToLowerInvariant();
    }

    public static bool ContainsTenantPartition(string objectKey, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        string normalized = objectKey.Replace('\\', '/');
        return normalized.Contains(
            $"/tenants/{CreatePartition(tenantId)}/",
            StringComparison.Ordinal);
    }

    public static bool ContainsTenantSharedPartition(string objectKey, string tenantId)
    {
        if (!ContainsTenantPartition(objectKey, tenantId))
        {
            return false;
        }

        string normalized = objectKey.Replace('\\', '/');
        return !normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Contains("users", StringComparer.OrdinalIgnoreCase);
    }
}
