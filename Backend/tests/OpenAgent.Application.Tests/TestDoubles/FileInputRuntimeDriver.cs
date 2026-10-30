using System.Runtime.CompilerServices;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Application.Tests.TestDoubles;

internal sealed class FileInputRuntimeDriver : IAgentRuntimeDriver, IRuntimeWorkspaceFiles
{
    internal Dictionary<string, byte[]> Inputs { get; } = new();
    internal RuntimeFilePreparation? Preparation { get; private set; }

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(RuntimeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Assert.NotNull(invocation.PrepareFilesAsync);
        Assert.Contains(invocation.Tools, tool => tool.Name == "import_file");
        Preparation = await invocation.PrepareFilesAsync(this, cancellationToken);
        try
        {
            yield return new() { Type = AgentStreamEventType.Content, Content = "Processed original files." };
            yield return new() { Type = AgentStreamEventType.Usage };
            await Preparation.Lease.DisposeAsync();
            await invocation.CommitAsync!(cancellationToken);
        }
        finally
        {
            await Preparation.Lease.DisposeAsync();
        }
    }

    public Task<RuntimeWorkspaceFile> ImportAsync(RuntimeFile file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        Inputs[file.FileId] = data.ToArray();
        return Task.FromResult(new RuntimeWorkspaceFile(file, $"inputs/{file.FileId}/{file.FileName}"));
    }
}
