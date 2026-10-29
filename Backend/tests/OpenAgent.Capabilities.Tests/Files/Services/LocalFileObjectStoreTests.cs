using System.Text;
using OpenAgent.Contracts.Files;
using OpenAgent.Capabilities.Files.Services;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Files;

public sealed class LocalFileObjectStoreTests
{
    [Fact]
    public async Task FileStore_NewInstanceReadsPersistedSkillAndRejectsEscapingKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "openagent-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            LocalFileObjectStore first = new(root);
            FileObjectReference file = await first.WriteAsync(new()
            {
                FileId = "skill", TenantId = "tenant", UserId = "user", Scope = FileObjectScope.Tenant,
                FileName = "SKILL.md", MediaType = "text/markdown", Sha256 = "test"
            }, new MemoryStream(Encoding.UTF8.GetBytes("persistent skill")), default);
            Assert.True(FileObjectTenantScope.ContainsTenantSharedPartition(file.ObjectKey, "tenant"));
            LocalFileObjectStore second = new(root);
            Assert.Equal("persistent skill", Encoding.UTF8.GetString(await second.ReadAsync(file.ObjectKey, default)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.ReadAsync("../outside", default));
        }
        finally { Directory.Delete(root, true); }
    }
}
