using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using ShapeTraffic.TrafficLab;

namespace ShapeTraffic.E2E.Tests;

public sealed class TrafficLabE2ETests
{
    [Fact]
    public async Task DownloadMeasurement_ShouldHonorConfiguredClientLimit()
    {
        var port = GetFreeTcpPort();
        await using var server = await TrafficLabServer.StartAsync(port, CancellationToken.None);
        var url = new Uri($"http://127.0.0.1:{port}/download?mb=1");

        var unrestricted = await TrafficLabClient.MeasureDownloadAsync(url, null, CancellationToken.None);
        var limited = await TrafficLabClient.MeasureDownloadAsync(url, 256 * 1024, CancellationToken.None);

        unrestricted.BytesTransferred.Should().BeGreaterThan(0);
        limited.BytesTransferred.Should().Be(unrestricted.BytesTransferred);
        limited.BytesPerSecond.Should().BeLessThan(350 * 1024);
        unrestricted.BytesPerSecond.Should().BeGreaterThan(limited.BytesPerSecond);
    }

    [Fact]
    public async Task UploadMeasurement_ShouldTransferExpectedPayload()
    {
        var port = GetFreeTcpPort();
        await using var server = await TrafficLabServer.StartAsync(port, CancellationToken.None);
        var url = new Uri($"http://127.0.0.1:{port}/upload");

        var measurement = await TrafficLabClient.MeasureUploadAsync(url, 512 * 1024, 256 * 1024, CancellationToken.None);

        measurement.BytesTransferred.Should().Be(512 * 1024);
        measurement.BytesPerSecond.Should().BeLessThan(350 * 1024);
        measurement.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}