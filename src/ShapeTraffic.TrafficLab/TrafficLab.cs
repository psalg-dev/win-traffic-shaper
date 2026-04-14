using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace ShapeTraffic.TrafficLab;

public static class ShapeTrafficTrafficLabProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseArguments(args[1..]);

        switch (command)
        {
            case "server":
            {
                var port = options.TryGetValue("port", out var portText) && int.TryParse(portText, out var parsedPort) ? parsedPort : 5088;
                await using var server = await TrafficLabServer.StartAsync(port, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"Traffic lab server listening on http://127.0.0.1:{server.Port}");
                Console.WriteLine("Press Ctrl+C to stop.");

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    completion.TrySetResult();
                };

                await completion.Task.ConfigureAwait(false);
                return 0;
            }

            case "download":
            {
                var url = options.TryGetValue("url", out var downloadUrl) ? new Uri(downloadUrl) : throw new ArgumentException("A --url argument is required.");
                var limit = ParseLimit(options);
                var measurement = await TrafficLabClient.MeasureDownloadAsync(url, limit, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"Downloaded {measurement.BytesTransferred} bytes in {measurement.Elapsed.TotalSeconds:0.00}s at {measurement.MegabitsPerSecond:0.00} Mbps.");
                return 0;
            }

            case "upload":
            {
                var url = options.TryGetValue("url", out var uploadUrl) ? new Uri(uploadUrl) : throw new ArgumentException("A --url argument is required.");
                var megabytes = options.TryGetValue("mb", out var sizeText) && long.TryParse(sizeText, out var parsedMegabytes) ? parsedMegabytes : 16;
                var limit = ParseLimit(options);
                var measurement = await TrafficLabClient.MeasureUploadAsync(url, megabytes * 1024 * 1024, limit, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"Uploaded {measurement.BytesTransferred} bytes in {measurement.Elapsed.TotalSeconds:0.00}s at {measurement.MegabitsPerSecond:0.00} Mbps.");
                return 0;
            }

            default:
                PrintUsage();
                return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = current[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            options[key] = value;
        }

        return options;
    }

    private static long? ParseLimit(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("limit-kbps", out var limitText))
        {
            return null;
        }

        return long.TryParse(limitText, out var limitKilobytes) && limitKilobytes > 0
            ? limitKilobytes * 1024
            : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ShapeTraffic.TrafficLab commands:");
        Console.WriteLine("  server --port 5088");
        Console.WriteLine("  download --url http://127.0.0.1:5088/download?mb=32 [--limit-kbps 256]");
        Console.WriteLine("  upload --url http://127.0.0.1:5088/upload [--mb 32] [--limit-kbps 256]");
    }
}

public sealed class TrafficLabServer : IAsyncDisposable
{
    private readonly WebApplication _application;

    private TrafficLabServer(WebApplication application, int port)
    {
        _application = application;
        Port = port;
    }

    public int Port { get; }

    public static async Task<TrafficLabServer> StartAsync(int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/download", async (HttpContext context, int mb) =>
        {
            var totalBytes = Math.Max(1, mb) * 1024 * 1024;
            var buffer = new byte[64 * 1024];
            Random.Shared.NextBytes(buffer);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = totalBytes;

            var remaining = totalBytes;
            while (remaining > 0)
            {
                var nextChunk = Math.Min(remaining, buffer.Length);
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, nextChunk), context.RequestAborted).ConfigureAwait(false);
                remaining -= nextChunk;
            }
        });
        app.MapPost("/upload", async (HttpContext context) =>
        {
            var buffer = new byte[64 * 1024];
            long totalBytes = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                totalBytes += read;
            }

            return Results.Ok(new { bytesReceived = totalBytes });
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new TrafficLabServer(app, port);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

public static class TrafficLabClient
{
    public static async Task<TransferMeasurement> MeasureDownloadAsync(Uri url, long? readLimitBytesPerSecond, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        int read;
        while ((read = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalBytes += read;
            await ApplyRateLimitAsync(totalBytes, readLimitBytesPerSecond, stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new TransferMeasurement(totalBytes, stopwatch.Elapsed);
    }

    public static async Task<TransferMeasurement> MeasureUploadAsync(Uri url, long totalBytes, long? writeLimitBytesPerSecond, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        await using var contentStream = new GeneratedTrafficStream(totalBytes, writeLimitBytesPerSecond);
        using var content = new StreamContent(contentStream, 64 * 1024);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        stopwatch.Stop();
        return new TransferMeasurement(totalBytes, stopwatch.Elapsed);
    }

    internal static async Task ApplyRateLimitAsync(long transferredBytes, long? bytesPerSecond, TimeSpan actualElapsed, CancellationToken cancellationToken)
    {
        if (!bytesPerSecond.HasValue || bytesPerSecond.Value <= 0)
        {
            return;
        }

        var expectedElapsed = TimeSpan.FromSeconds(transferredBytes / (double)bytesPerSecond.Value);
        var remainingDelay = expectedElapsed - actualElapsed;
        if (remainingDelay > TimeSpan.Zero)
        {
            await Task.Delay(remainingDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record TransferMeasurement(long BytesTransferred, TimeSpan Elapsed)
{
    public double BytesPerSecond => BytesTransferred / Math.Max(Elapsed.TotalSeconds, 0.001d);

    public double MegabitsPerSecond => BytesPerSecond * 8d / 1_000_000d;
}

internal sealed class GeneratedTrafficStream : Stream
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly long? _limitBytesPerSecond;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _position;

    public GeneratedTrafficStream(long length, long? limitBytesPerSecond)
    {
        Length = length;
        _limitBytesPerSecond = limitBytesPerSecond;
        Random.Shared.NextBytes(_buffer);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= Length)
        {
            return 0;
        }

        var nextRead = (int)Math.Min(Math.Min(buffer.Length, _buffer.Length), Length - _position);
        _buffer.AsMemory(0, nextRead).CopyTo(buffer);
        _position += nextRead;
        await TrafficLabClient.ApplyRateLimitAsync(_position, _limitBytesPerSecond, _stopwatch.Elapsed, cancellationToken).ConfigureAwait(false);
        return nextRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}