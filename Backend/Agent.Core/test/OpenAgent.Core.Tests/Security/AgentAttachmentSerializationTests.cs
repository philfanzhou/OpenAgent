using System.Text.Json;
using OpenAgent.Contracts.Content;
using OpenAgent.Contracts.Requests;
using Xunit;

namespace OpenAgent.Core.Tests.Security;

public class AgentAttachmentSerializationTests
{
    [Fact]
    public void Serialize_AgentRequest_DoesNotExposeAttachmentBytes()
    {
        AgentRequest request = new()
        {
            Query = "describe",
            Attachments =
            [
                new AgentAttachment
                {
                    FileName = "private.png",
                    MediaType = "image/png",
                    Data = [1, 2, 3]
                }
            ]
        };

        string json = JsonSerializer.Serialize(request);

        Assert.DoesNotContain("Attachments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private.png", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_AgentRequest_IgnoresAttachmentPayload()
    {
        const string json = """
            {
              "Query": "describe",
              "Attachments": [
                {
                  "FileName": "bypass.png",
                  "MediaType": "image/png",
                  "Data": "AQID"
                }
              ]
            }
            """;

        AgentRequest? request = JsonSerializer.Deserialize<AgentRequest>(json);

        Assert.NotNull(request);
        Assert.Empty(request.Attachments);
    }
}
