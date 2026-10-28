using OpenAgent.Contracts.Files;
using Xunit;

namespace OpenAgent.Contracts.Tests.Files;

public class FileMediaTypeCatalogTests
{
    [Fact]
    public void AllExtensions_IncludesHtmlAndCss()
    {
        Assert.Contains(".html", FileMediaTypeCatalog.AllExtensions);
        Assert.Contains(".htm", FileMediaTypeCatalog.AllExtensions);
        Assert.Contains(".css", FileMediaTypeCatalog.AllExtensions);
        Assert.Contains(".md", FileMediaTypeCatalog.AllExtensions);
        Assert.Equal(FileMediaTypeCatalog.AllExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            FileMediaTypeCatalog.AllExtensions.Count);
    }

    [Fact]
    public void AllMediaTypes_IncludesCanonicalTypesAndAliases()
    {
        Assert.Contains("text/html", FileMediaTypeCatalog.AllMediaTypes);
        Assert.Contains("text/css", FileMediaTypeCatalog.AllMediaTypes);
        Assert.Contains("text/json", FileMediaTypeCatalog.AllMediaTypes);
        Assert.Contains("text/xml", FileMediaTypeCatalog.AllMediaTypes);
        Assert.Equal(FileMediaTypeCatalog.AllMediaTypes.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            FileMediaTypeCatalog.AllMediaTypes.Count);
    }

    [Theory]
    [InlineData(".html", "text/html")]
    [InlineData(".htm", "text/html")]
    [InlineData(".css", "text/css")]
    [InlineData(".md", "text/markdown")]
    public void TryGetMediaType_ReturnsCanonicalType(string extension, string expected)
    {
        Assert.True(FileMediaTypeCatalog.TryGetMediaType(extension, out string mediaType));
        Assert.Equal(expected, mediaType);
    }

    [Fact]
    public void TryGetMediaType_IsCaseInsensitive()
    {
        Assert.True(FileMediaTypeCatalog.TryGetMediaType(".HTML", out string mediaType));
        Assert.Equal("text/html", mediaType);
    }

    [Theory]
    [InlineData(".html", "text/html", true)]
    [InlineData(".html", "TEXT/HTML", true)]
    [InlineData(".md", "text/markdown", true)]
    [InlineData(".md", "text/plain", true)]
    [InlineData(".json", "text/json", true)]
    [InlineData(".xml", "text/xml", true)]
    [InlineData(".html", "text/plain", false)]
    [InlineData(".md", "application/json", false)]
    [InlineData(".py", "text/x-python", false)]
    public void MatchesMediaType_AcceptsCanonicalAndAliasesOnly(string extension, string mediaType, bool expected)
    {
        Assert.Equal(expected, FileMediaTypeCatalog.MatchesMediaType(extension, mediaType));
    }

    [Theory]
    [InlineData("text/html", ".html")]
    [InlineData("text/css", ".css")]
    [InlineData("image/jpeg", ".jpg")]
    public void TryGetExtensionForMediaType_MapsCanonicalTypes(string mediaType, string expected)
    {
        Assert.True(FileMediaTypeCatalog.TryGetExtensionForMediaType(mediaType, out string extension));
        Assert.Equal(expected, extension);
    }

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("text/css", true)]
    [InlineData("application/json", true)]
    [InlineData("application/xml", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("image/png", false)]
    [InlineData("application/pdf", false)]
    public void IsTextMediaType_MatchesTextFamily(string mediaType, bool expected)
    {
        Assert.Equal(expected, FileMediaTypeCatalog.IsTextMediaType(mediaType));
    }
}
