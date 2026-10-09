using OmniPlay.Infrastructure.Tmdb;

namespace OmniPlay.Tests;

public sealed class TmdbProxyGatewayTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://api.themoviedb.org/3")]
    public void ResolveApiBaseUrl_EmptyOrOfficialInput_ReturnsOfficialBase(string input)
    {
        Assert.Equal("https://api.themoviedb.org/3", TmdbProxyGateway.ResolveApiBaseUrl(input));
    }

    [Theory]
    [InlineData("proxy.example.com", "https://proxy.example.com/3")]
    [InlineData("https://proxy.example.com", "https://proxy.example.com/3")]
    [InlineData("http://proxy.example.com/", "http://proxy.example.com/3")]
    [InlineData("https://proxy.example.com/tmdb", "https://proxy.example.com/tmdb/3")]
    [InlineData("https://proxy.example.com/tmdb/", "https://proxy.example.com/tmdb/3")]
    [InlineData("https://proxy.example.com/3", "https://proxy.example.com/3")]
    [InlineData("https://proxy.example.com/api/3/", "https://proxy.example.com/api/3")]
    public void ResolveApiBaseUrl_ValidInputs_ApendsThreeSegment(string input, string expected)
    {
        Assert.Equal(expected, TmdbProxyGateway.ResolveApiBaseUrl(input));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://proxy.example.com")]
    [InlineData("https://")]
    [InlineData("https://proxy.example.com?x=1")]
    [InlineData("https://user:pass@proxy.example.com")]
    [InlineData("https://proxy.example.com#frag")]
    public void ResolveApiBaseUrl_InvalidInputs_ReturnsNull(string input)
    {
        Assert.Null(TmdbProxyGateway.ResolveApiBaseUrl(input));
    }

    [Theory]
    [InlineData("https://api.themoviedb.org/3/search/movie?query=x", "", "https://api.themoviedb.org/3/search/movie?query=x")]
    [InlineData("https://api.themoviedb.org/3/configuration", "https://proxy.example.com/tmdb", "https://proxy.example.com/tmdb/3/configuration")]
    [InlineData("https://api.themoviedb.org/3/search/movie", "proxy.example.com", "https://proxy.example.com/3/search/movie")]
    [InlineData("https://api.themoviedb.org/3/search/movie?query=a%20b&include_adult=false", "https://proxy.example.com/tmdb/", "https://proxy.example.com/tmdb/3/search/movie?query=a%20b&include_adult=false")]
    public void ResolveRequestUrl_RewritesOfficialPathOntoProxy(string officialUrl, string proxy, string expected)
    {
        Assert.Equal(expected, TmdbProxyGateway.ResolveRequestUrl(officialUrl, proxy));
    }

    [Fact]
    public void ResolveRequestUrl_InvalidProxyOrUrl_ReturnsNull()
    {
        Assert.Null(TmdbProxyGateway.ResolveRequestUrl("https://api.themoviedb.org/3/configuration", "not a url"));
        Assert.Null(TmdbProxyGateway.ResolveRequestUrl("not a url", "proxy.example.com"));
    }

    [Fact]
    public void HasInvalidInput_ReportsInvalidInputOnly()
    {
        Assert.False(TmdbProxyGateway.HasInvalidInput(""));
        Assert.False(TmdbProxyGateway.HasInvalidInput("proxy.example.com"));
        Assert.True(TmdbProxyGateway.HasInvalidInput("ftp://proxy.example.com"));
    }
}
