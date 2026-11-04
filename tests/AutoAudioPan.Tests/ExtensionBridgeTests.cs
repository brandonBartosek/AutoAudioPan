using System.Net;
using System.Text;
using AutoAudioPan.Core;
using Xunit;

public class ExtensionBridgeTests
{
    private static async Task<HttpResponseMessage> Post(int port, string? origin)
    {
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/chrome")
        {
            Content = new StringContent("{\"pan\":-0.5}", Encoding.UTF8, "application/json")
        };
        if (origin != null) req.Headers.Add("Origin", origin);
        return await http.SendAsync(req);
    }

    [Fact]
    public async Task ExtensionOriginGetsConfigAndMarksConnected()
    {
        using var bridge = new ExtensionBridge(47932);
        bridge.Config = new ExtensionConfig("Manual", 0.25, 80, true);
        Assert.False(bridge.Connected);

        var res = await Post(47932, "chrome-extension://abcdef");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"mode\":\"Manual\"", body);
        Assert.Contains("\"muted\":true", body);
        Assert.True(bridge.Connected);
        Assert.Equal(-0.5, bridge.LastPan, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    public async Task WebPagesAreRejected(string? origin)
    {
        using var bridge = new ExtensionBridge(47933);
        var res = await Post(47933, origin);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.False(bridge.Connected);
    }
}
