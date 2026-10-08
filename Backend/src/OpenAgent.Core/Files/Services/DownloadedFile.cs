using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Files.Services;

internal sealed record DownloadedFile(
    string FileName,
    string MediaType,
    byte[] Content);
