using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShapeTraffic.Core.Models;
using ShapeTraffic.Core.Services;
using ShapeTraffic.Infrastructure.Persistence;
using System.IO;

namespace ShapeTraffic.Core.Tests;

public sealed class PacketPacerTests
{
    [Fact]
    public void Schedule_ShouldSpacePackets_WhenLimitIsSet()
    {
        var pacer = new PacketPacer();
        var now = DateTimeOffset.UtcNow;

        var first = pacer.Schedule("proc", TrafficDirection.Download, 1_024, 1_024, now);
        var second = pacer.Schedule("proc", TrafficDirection.Download, 1_024, 1_024, now);

        first.Should().Be(now);
        second.Should().Be(now.AddSeconds(1));
    }

    [Fact]
    public void Schedule_ShouldReset_WhenUnlimitedTrafficIsApplied()
    {
        var pacer = new PacketPacer();
        var now = DateTimeOffset.UtcNow;

        _ = pacer.Schedule("proc", TrafficDirection.Upload, 2_048, 1_024, now);
        var afterReset = pacer.Schedule("proc", TrafficDirection.Upload, 2_048, null, now);
        var afterUnlimited = pacer.Schedule("proc", TrafficDirection.Upload, 2_048, 1_024, now);

        afterReset.Should().Be(now);
        afterUnlimited.Should().Be(now);
    }
}

public sealed class SqliteTrafficRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ShapeTrafficTests", Guid.NewGuid().ToString("N"));
    private SqliteTrafficRepository? _repository;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteTrafficRepository(Path.Combine(_directory, "traffic.sqlite"), NullLogger<SqliteTrafficRepository>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _repository = null;

        if (Directory.Exists(_directory))
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Repository_ShouldPersistRulesAndSamples()
    {
        var repository = _repository!;
        var rule = TrafficLimitRule.Create("name:test.exe", "test.exe", 64 * 1024, 128 * 1024);
        var sample = new TrafficSample(DateTimeOffset.UtcNow, 1_024, 2_048);

        await repository.UpsertRuleAsync(rule, CancellationToken.None);
        await repository.AppendAggregateSampleAsync(sample, CancellationToken.None);

        var rules = await repository.GetRulesAsync(CancellationToken.None);
        var samples = await repository.GetAggregateSamplesAsync(sample.Timestamp.AddMinutes(-1), CancellationToken.None);

        rules.Should().ContainSingle(storedRule => storedRule.ProcessKey == rule.ProcessKey && storedRule.UploadLimitBytesPerSecond == rule.UploadLimitBytesPerSecond);
        samples.Should().ContainSingle(storedSample => storedSample.UploadBytes == sample.UploadBytes && storedSample.DownloadBytes == sample.DownloadBytes);
    }
}