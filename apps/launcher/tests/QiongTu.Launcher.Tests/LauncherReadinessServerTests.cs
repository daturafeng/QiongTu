using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Launcher.Tests;

[TestClass]
public sealed class LauncherReadinessServerTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task AcceptsAuthenticatedMonotonicEventsUntilReady()
    {
        var session = LauncherReadinessSession.Create();
        var server = new LauncherReadinessServer(session);
        var expectedProcessId = Environment.ProcessId;
        var resultTask = server.WaitForReadinessAsync(
            Task.FromResult(expectedProcessId),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await using var client = await ConnectAsync(session.PipeName);
        await WriteEventAsync(client, session.Nonce, expectedProcessId, 1, LaunchReadinessStages.MainStarted);
        await WriteEventAsync(client, session.Nonce, expectedProcessId, 2, LaunchReadinessStages.ReadyToShow);
        var result = await resultTask;

        Assert.AreEqual("ready", result.Outcome);
        Assert.HasCount(2, result.Events);
        Assert.AreEqual(LaunchReadinessStages.ReadyToShow, result.LastStage);
    }

    [TestMethod]
    public async Task RejectsWrongNonceProcessAndNonMonotonicSequence()
    {
        var wrongNonce = await RunSingleEventAsync(
            nonceFactory: _ => new string('0', 64),
            sequence: 1);
        Assert.AreEqual("invalid-event", wrongNonce.FailureCode);

        var wrongProcess = await RunSingleEventAsync(
            nonceFactory: session => session.Nonce,
            sequence: 1,
            processIdOffset: 1);
        Assert.AreEqual("invalid-event", wrongProcess.FailureCode);

        var wrongSequence = await RunSingleEventAsync(
            nonceFactory: session => session.Nonce,
            sequence: 2);
        Assert.AreEqual("invalid-event", wrongSequence.FailureCode);
    }

    [TestMethod]
    public async Task RejectsOversizedMessageAndBoundsTimeout()
    {
        var session = LauncherReadinessSession.Create();
        var server = new LauncherReadinessServer(session);
        var resultTask = server.WaitForReadinessAsync(
            Task.FromResult(Environment.ProcessId),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await using (var client = await ConnectAsync(session.PipeName))
        await using (var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true))
        {
            await writer.WriteLineAsync(new string('x', LauncherReadinessServer.MaximumMessageCharacters + 1));
            await writer.FlushAsync();
        }

        var oversized = await resultTask;
        Assert.AreEqual("message-too-large", oversized.FailureCode);

        var timeoutSession = LauncherReadinessSession.Create();
        var timeoutServer = new LauncherReadinessServer(timeoutSession);
        var timeout = await timeoutServer.WaitForReadinessAsync(
            Task.FromResult(Environment.ProcessId),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.AreEqual("readiness-timeout", timeout.FailureCode);
    }

    private static async Task<LauncherReadinessResult> RunSingleEventAsync(
        Func<LauncherReadinessSession, string> nonceFactory,
        int sequence,
        int processIdOffset = 0)
    {
        var session = LauncherReadinessSession.Create();
        var server = new LauncherReadinessServer(session);
        var expectedProcessId = Environment.ProcessId;
        var resultTask = server.WaitForReadinessAsync(
            Task.FromResult(expectedProcessId),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await using var client = await ConnectAsync(session.PipeName);
        await WriteEventAsync(
            client,
            nonceFactory(session),
            expectedProcessId + processIdOffset,
            sequence,
            LaunchReadinessStages.MainStarted);
        return await resultTask;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2_000);
        return client;
    }

    private static async Task WriteEventAsync(
        Stream stream,
        string nonce,
        int processId,
        int sequence,
        string stage)
    {
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };
        var launchEvent = new LaunchReadinessEvent(
            ContractVersions.LauncherApiV1,
            nonce,
            processId,
            sequence,
            stage,
            DateTimeOffset.UtcNow);
        await writer.WriteLineAsync(JsonSerializer.Serialize(launchEvent, SerializerOptions));
    }
}
