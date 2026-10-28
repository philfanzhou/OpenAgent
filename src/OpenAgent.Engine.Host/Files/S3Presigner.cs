using Amazon.S3;
using Amazon.S3.Model;

namespace OpenAgent.Engine.Host.Files;

internal interface IS3Presigner
{
    string GetPreSignedURL(GetPreSignedUrlRequest request);
}

internal sealed class S3Presigner(IAmazonS3 client, bool ownsClient = false) : IS3Presigner, IDisposable
{
    public string GetPreSignedURL(GetPreSignedUrlRequest request) => client.GetPreSignedURL(request);

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
