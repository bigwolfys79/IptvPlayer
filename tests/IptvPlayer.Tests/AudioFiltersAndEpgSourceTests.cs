using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IptvPlayer.Tests;

public class AudioFilterTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    [InlineData(-50)]
    public void GetAudioFilters_WithoutBoost_ReturnsBaseChain(int boostPercent)
    {
        var filters = StreamService.GetAudioFilters("Dynamic", true, boostPercent);
        Assert.Equal("dynaudnorm=f=150:g=5:m=12:p=0.95", filters);
    }

    [Fact]
    public void GetAudioFilters_BoostWithoutNormalization_ReturnsVolumeOnly()
    {
        Assert.Equal("volume=1.25", StreamService.GetAudioFilters("Off", true, 125));
    }

    [Theory]
    [InlineData("Dynamic", true, 150, "dynaudnorm=f=150:g=5:m=12:p=0.95,volume=1.5")]
    [InlineData("Dynamic", true, 200, "dynaudnorm=f=150:g=5:m=12:p=0.95,volume=2")]
    [InlineData("Loudness", true, 125, "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000,asetpts=N/SR/TB,volume=1.25")]
    [InlineData("Loudness", false, 150, "dynaudnorm=f=150:g=5:m=12:p=0.95,volume=1.5")]
    public void GetAudioFilters_Boost_AppendedAfterNormalization(
        string mode, bool allowLoudness, int boostPercent, string expected)
    {
        Assert.Equal(expected, StreamService.GetAudioFilters(mode, allowLoudness, boostPercent));
    }

    [Fact]
    public void GetAudioFilters_Boost_UsesInvariantDecimalSeparator()
    {
        var filters = StreamService.GetAudioFilters("Off", true, 125);
        Assert.DoesNotContain(",", filters);
    }
}

public class XmlTvValidationTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        public StubHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new ByteArrayContent(_payload)
            };
            return Task.FromResult(response);
        }
    }

    private static XmlTvService CreateService(byte[] payload)
    {
        return new XmlTvService(
            NullLogger<XmlTvService>.Instance,
            new HttpClient(new StubHandler(payload)));
    }

    private static byte[] Xmltv() =>
        Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><tv><channel id=\"1\"><display-name>Канал</display-name></channel></tv>");

    [Fact]
    public async Task Validate_PlainXmltv_ReturnsNull()
    {
        Assert.Null(await CreateService(Xmltv()).ValidateEpgSourceAsync("http://stub"));
    }

    [Fact]
    public async Task Validate_GzippedXmltv_ReturnsNull()
    {
        var gzip = new MemoryStream();
        await using (var gz = new GZipStream(gzip, CompressionMode.Compress, leaveOpen: true))
        {
            await gz.WriteAsync(Xmltv());
        }
        Assert.Null(await CreateService(gzip.ToArray()).ValidateEpgSourceAsync("http://stub"));
    }

    [Fact]
    public async Task Validate_Garbage_ReturnsError()
    {
        var error = await CreateService(new byte[] { 1, 2, 3, 4, 5 }).ValidateEpgSourceAsync("http://stub");
        Assert.NotNull(error);
        Assert.Contains("XMLTV", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_HttpError_ReturnsErrorWithStatus()
    {
        var service = new XmlTvService(
            NullLogger<XmlTvService>.Instance,
            new HttpClient(new StubHandler(Array.Empty<byte>()) { StatusCode = HttpStatusCode.NotFound }));
        var error = await service.ValidateEpgSourceAsync("http://stub");
        Assert.NotNull(error);
        // L.T resolves resw directly since 1.21.4 — assert the formatted status, locale-neutral parts
        Assert.Contains("HTTP", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("404", error, StringComparison.Ordinal);
    }
}
