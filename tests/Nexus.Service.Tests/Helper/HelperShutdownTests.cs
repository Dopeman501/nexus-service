#if WINDOWS
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Helper;

public sealed class HelperShutdownTests
{
    [Fact]
    public async Task Shutdown_completes_when_no_helper_is_connected()
    {
        await LifecycleCommands.SendShutdownAsync(new HelperRegistry(), new CancellationToken(canceled: true));
    }

    [Fact]
    public async Task Shutdown_waits_until_the_helper_closes_its_pipe()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = await ConnectedPipe.CreateAsync(testTimeout.Token);

        var shutdown = LifecycleCommands.SendShutdownAsync(pipe.Registry, testTimeout.Token);
        await pipe.AssertShutdownFrameAsync(testTimeout.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => shutdown.WaitAsync(TimeSpan.FromMilliseconds(250)));

        pipe.Client.Dispose();
        await shutdown.WaitAsync(testTimeout.Token);
    }

    [Fact]
    public async Task Shutdown_obeys_its_deadline_when_the_helper_keeps_its_pipe_open()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testTimeout.Token);
        await using var pipe = await ConnectedPipe.CreateAsync(testTimeout.Token);

        var shutdown = LifecycleCommands.SendShutdownAsync(pipe.Registry, deadline.Token);
        await pipe.AssertShutdownFrameAsync(testTimeout.Token);
        await deadline.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => shutdown.WaitAsync(TimeSpan.FromSeconds(2), testTimeout.Token));
        Assert.True(pipe.Registry.IsAnyConnected);
    }

    [Fact]
    public async Task Shutdown_reports_write_failure_when_the_connected_peer_breaks_its_pipe()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe = await ConnectedPipe.CreateAsync(testTimeout.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipe.Registry.InboundEnvelope += (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };

        try
        {
            var probe = new HelperEnvelope { Type = "test.readerProbe" };
            await Framing.WriteFrameAsync(pipe.Client,
                JsonSerializer.SerializeToUtf8Bytes(probe, AppJsonContext.Default.HelperEnvelope), testTimeout.Token);
            await entered.Task.WaitAsync(testTimeout.Token);
            pipe.Client.Dispose();

            await Assert.ThrowsAnyAsync<IOException>(() => LifecycleCommands.SendShutdownAsync(pipe.Registry, testTimeout.Token));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class ConnectedPipe(NamedPipeClientStream client, HelperRegistry registry, Task serverRun) : IAsyncDisposable
    {
        internal NamedPipeClientStream Client { get; } = client;
        internal HelperRegistry Registry { get; } = registry;

        internal static async Task<ConnectedPipe> CreateAsync(CancellationToken token)
        {
            var name = $"nexus-helper-shutdown-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var registry = new HelperRegistry();
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            registry.Connected += _ => connected.TrySetResult();
            try
            {
                await Task.WhenAll(server.WaitForConnectionAsync(token), client.ConnectAsync(token));
                var serverRun = HelperConnection.RunAsync(server, registry, token);
                using var currentProcess = Process.GetCurrentProcess();
                var hello = new HelperEnvelope
                {
                    Type = "hello",
                    Payload = JsonSerializer.SerializeToElement(new HelperHello
                    {
                        SessionId = currentProcess.SessionId,
                        Pid = currentProcess.Id,
                        Version = "test",
                    }, AppJsonContext.Default.HelperHello),
                };
                await Framing.WriteFrameAsync(client, JsonSerializer.SerializeToUtf8Bytes(hello, AppJsonContext.Default.HelperEnvelope), token);
                await connected.Task.WaitAsync(token);
                return new ConnectedPipe(client, registry, serverRun);
            }
            catch
            {
                client.Dispose();
                server.Dispose();
                throw;
            }
        }

        internal async Task AssertShutdownFrameAsync(CancellationToken token)
        {
            var frame = await Framing.ReadFrameAsync(Client, token);
            Assert.NotNull(frame);
            var envelope = JsonSerializer.Deserialize(frame, AppJsonContext.Default.HelperEnvelope);
            Assert.NotNull(envelope);
            Assert.Equal("helper.shutdown", envelope.Type);
            Assert.Null(envelope.Id);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await serverRun.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
#endif
