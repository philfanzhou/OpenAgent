using System.Text;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Hosting.Errors;

public static class AgentErrorCodeNames
{
    /// <summary>枚举名转 kebab-case 符号名：TenantDataIsolationViolation → tenant-data-isolation-violation。</summary>
    public static string ToSymbolicName(this AgentErrorCode errorCode)
    {
        StringBuilder builder = new();
        foreach (char character in errorCode.ToString())
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
