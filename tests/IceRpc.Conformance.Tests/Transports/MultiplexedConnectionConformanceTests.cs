// Copyright (c) ZeroC, Inc. All rights reserved.

using IceRpc.Internal;
using IceRpc.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;

namespace IceRpc.Conformance.Tests;

/// <summary>Conformance tests for the multiplexed transports.</summary>
public abstract partial class MultiplexedConnectionConformanceTests
{
    private static readonly ReadOnlyMemory<byte> _oneBytePayload = new(new byte[] { 0xFF });

    /// <summary>Verifies that both peers can initiate and accept streams.</summary>
    /// <param name="serverInitiated">Whether the stream is initiated by the server or by the client.</param>
    [Test]
    public async Task Accept_a_stream([Values(true, false)] bool serverInitiated)
    {
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);

        IMultiplexedConnection clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        await using LocalAndRemoteStreams sut = await CreateAndAcceptStreamAsync(
            serverInitiated ? serverConnection : clientConnection,
            serverInitiated ? clientConnection : serverConnection);

        Assert.That(sut.LocalStream.Id, Is.EqualTo(sut.RemoteStream.Id));
    }

    /// <summary>Verifies that accept stream calls can be canceled.</summary>
    [Test]
    public async Task Accept_stream_cancellation()
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        using var cts = new CancellationTokenSource();
        ValueTask<IMultiplexedStream> acceptTask = serverConnection.AcceptStreamAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(10)); // give a few ms for acceptTask to start

        // Act
        cts.Cancel();

        // Assert
        Assert.That(async () => await acceptTask, Throws.TypeOf<OperationCanceledException>());

        // We also verify we can still create new streams. This shows that canceling AcceptAsync does not "abort" new
        // streams and is a transient cancellation (not obvious with QUIC).
        Assert.That(
            async () =>
            {
                await using var streams = await CreateAndAcceptStreamAsync(clientConnection, serverConnection);
            },
            Throws.Nothing);
    }

    /// <summary>Verifies that AcceptStream fails when the connection is closed.</summary>
    [TestCase(MultiplexedConnectionCloseError.NoError, IceRpcError.ConnectionClosedByPeer)]
    [TestCase(MultiplexedConnectionCloseError.ServerBusy, IceRpcError.ServerBusy)]
    [TestCase((MultiplexedConnectionCloseError)255, IceRpcError.ConnectionAborted)]
    public async Task Accept_stream_fails_on_close(
        MultiplexedConnectionCloseError closeError,
        IceRpcError expectedIceRpcError)
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        IMultiplexedConnection clientConnection =
            provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        Task acceptStreams = serverConnection.AcceptStreamAsync(CancellationToken.None).AsTask();

        // Act
        await clientConnection.CloseAsync(closeError, CancellationToken.None);

        // Assert
        IceRpcException ex = Assert.ThrowsAsync<IceRpcException>(async () => await acceptStreams)!;
        Assert.That(ex.IceRpcError, Is.EqualTo(expectedIceRpcError));
    }

    /// <summary>Verifies that after reaching the stream max count, new streams are not accepted until a
    /// stream is closed.</summary>
    /// <param name="streamMaxCount">The max stream count limit to use for the test.</param>
    /// <param name="bidirectional">Whether to test with bidirectional or unidirectional streams.</param>
    [Test]
    public async Task After_reach_max_stream_count_completing_a_stream_allows_accepting_a_new_one(
       [Values(1, 1024)] int streamMaxCount,
       [Values(true, false)] bool bidirectional)
    {
        // Arrange
        IServiceCollection serviceCollection = CreateServiceCollection().AddMultiplexedTransportTest();
        if (bidirectional)
        {
            serviceCollection.AddOptions<MultiplexedConnectionOptions>().Configure(
                options => options.MaxBidirectionalStreams = streamMaxCount);
        }
        else
        {
            serviceCollection.AddOptions<MultiplexedConnectionOptions>().Configure(
                options => options.MaxUnidirectionalStreams = streamMaxCount);
        }
        await using ServiceProvider provider = serviceCollection.BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        List<IMultiplexedStream> streams = await CreateStreamsAsync(streamMaxCount, bidirectional);

        ValueTask<IMultiplexedStream> lastStreamTask = clientConnection.CreateStreamAsync(bidirectional, default);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await using IMultiplexedStream serverStream = await serverConnection.AcceptStreamAsync(default);
        if (bidirectional)
        {
            serverStream.Output.Complete(new OperationCanceledException()); // exception does not matter
        }
        bool isCompleted = lastStreamTask.IsCompleted;

        // Act
        serverStream.Input.Complete();

        // Assert
        Assert.That(isCompleted, Is.False);
        Assert.That(async () => await lastStreamTask, Throws.Nothing);

        await CleanupStreamsAsync(streams.ToArray());
        await CleanupStreamsAsync(await lastStreamTask);

        async Task<List<IMultiplexedStream>> CreateStreamsAsync(int count, bool bidirectional)
        {
            var streams = new List<IMultiplexedStream>();
            for (int i = 0; i < count; i++)
            {
                IMultiplexedStream stream = await clientConnection.CreateStreamAsync(
                    bidirectional,
                    default).ConfigureAwait(false);
                streams.Add(stream);
                await stream.Output.WriteAsync(_oneBytePayload, default);
            }
            return streams;
        }
    }

    /// <summary>Verify streams cannot be created after closing down the connection.</summary>
    [TestCase(MultiplexedConnectionCloseError.NoError, IceRpcError.ConnectionClosedByPeer)]
    [TestCase(MultiplexedConnectionCloseError.ServerBusy, IceRpcError.ServerBusy)]
    [TestCase((MultiplexedConnectionCloseError)255, IceRpcError.ConnectionAborted)]
    public async Task Cannot_create_streams_with_a_closed_connection(
        MultiplexedConnectionCloseError closeError,
        IceRpcError expectedIceRpcError)
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        // Act
        await serverConnection.CloseAsync(closeError, CancellationToken.None);

        // Assert
        IceRpcException? exception;

        exception = Assert.ThrowsAsync<IceRpcException>(
            () => clientConnection.AcceptStreamAsync(CancellationToken.None).AsTask());
        Assert.That(exception!.IceRpcError, Is.EqualTo(expectedIceRpcError));

        exception = Assert.ThrowsAsync<IceRpcException>(
            () => clientConnection.CreateStreamAsync(true, default).AsTask());
        Assert.That(exception!.IceRpcError, Is.EqualTo(expectedIceRpcError));
    }

    /// <summary>Verify streams cannot be created after closing down the connection.</summary>
    [TestCase(MultiplexedConnectionCloseError.NoError, IceRpcError.ConnectionClosedByPeer)]
    [TestCase(MultiplexedConnectionCloseError.ServerBusy, IceRpcError.ServerBusy)]
    [TestCase((MultiplexedConnectionCloseError)255, IceRpcError.ConnectionAborted)]
    public async Task Pending_create_streams_fails_on_connection_close(
        MultiplexedConnectionCloseError closeError,
        IceRpcError expectedIceRpcError)
    {
        // Arrange
        IServiceCollection serviceCollection = CreateServiceCollection().AddMultiplexedTransportTest();
        serviceCollection.AddOptions<MultiplexedConnectionOptions>().Configure(
                options => options.MaxBidirectionalStreams = 1);
        await using ServiceProvider provider = serviceCollection.BuildServiceProvider(validateScopes: true);

        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        await using IMultiplexedStream stream1 = await clientConnection.CreateStreamAsync(true, default);
        await stream1.Output.WriteAsync(_oneBytePayload, default); // Ensures the stream is started.

        ValueTask<IMultiplexedStream> stream2CreateStreamTask = clientConnection.CreateStreamAsync(true, default);
        await Task.Delay(100);
        Assert.That(stream2CreateStreamTask.IsCompleted, Is.False);

        // Act
        await serverConnection.CloseAsync(closeError, CancellationToken.None);

        // Assert
        IceRpcException? exception = Assert.ThrowsAsync<IceRpcException>(async () => await stream2CreateStreamTask);
        Assert.That(exception!.IceRpcError, Is.EqualTo(expectedIceRpcError));

        await CleanupStreamsAsync(stream1);
    }

    /// <summary>Verify streams cannot be created after disposing the connection.</summary>
    /// <param name="disposeServerConnection">Whether to dispose the server connection or the client connection.
    /// </param>
    [Test]
    public async Task Cannot_create_streams_with_a_disposed_connection(
        [Values(true, false)] bool disposeServerConnection)
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        IMultiplexedConnection disposedConnection = disposeServerConnection ? serverConnection : clientConnection;
        IMultiplexedConnection peerConnection = disposeServerConnection ? clientConnection : serverConnection;
        IMultiplexedStream peerStream = await peerConnection.CreateStreamAsync(true, default).ConfigureAwait(false);
        await peerStream.Output.WriteAsync(_oneBytePayload); // Make sure the stream is started before DisposeAsync

        // Act
        await disposedConnection.DisposeAsync();

        // Assert

        IceRpcException? exception;

        Assert.ThrowsAsync<ObjectDisposedException>(() => disposedConnection.CreateStreamAsync(true, default).AsTask());

        exception = Assert.ThrowsAsync<IceRpcException>(async () =>
            {
                // It can take few writes for the peer to detect the connection closure.
                while (true)
                {
                    FlushResult result = await peerStream.Output.WriteAsync(_oneBytePayload);
                    if (result.IsCompleted)
                    {
                        return;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(20));
                }
            });

        // TODO: we get ConnectionClosedByPeer with Quic because it sends a Close frame with the default (0) error code
        // when calling DisposeAsync on the connection. Fixing #2225 would allow Slic to behave the same as Slic here.
        Assert.That(
            exception!.IceRpcError,
            Is.EqualTo(IceRpcError.ConnectionClosedByPeer).Or.EqualTo(IceRpcError.ConnectionAborted));
    }

    [Test]
    public async Task Close_connection()
    {
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        IMultiplexedConnection clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        IListener<IMultiplexedConnection> listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();

        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        // Act/Assert
        Assert.That(async () => await clientConnection.CloseAsync(
            MultiplexedConnectionCloseError.NoError,
            CancellationToken.None), Throws.Nothing);

        Assert.That(async () => await serverConnection.CloseAsync(
            MultiplexedConnectionCloseError.NoError,
            CancellationToken.None), Throws.Nothing);
    }

    [Test]
    public async Task Close_connection_on_both_sides()
    {
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        IMultiplexedConnection clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        IListener<IMultiplexedConnection> listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        // Act
        Task clientCloseTask = clientConnection.CloseAsync(
            MultiplexedConnectionCloseError.NoError,
            CancellationToken.None);

        Task serverCloseTask = serverConnection.CloseAsync(
            MultiplexedConnectionCloseError.NoError,
            CancellationToken.None);

        // Assert
        Assert.That(() => clientCloseTask, Throws.Nothing);
        Assert.That(() => serverCloseTask, Throws.Nothing);
    }

    /// <summary>Verifies that ConnectAsync can be canceled.</summary>
    [Test]
    public async Task Connect_cancellation()
    {
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);

        using var cts = new CancellationTokenSource();
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var connectTask = clientConnection.ConnectAsync(cts.Token);

        // Act
        cts.Cancel();

        // Assert
        Assert.That(async () => await connectTask, Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>Verifies that connect fails if the listener is disposed.</summary>
    [Test]
    public async Task Connect_fails_if_listener_is_disposed()
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection().BuildServiceProvider(validateScopes: true);
        IListener<IMultiplexedConnection> listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        var clientTransport = provider.GetRequiredService<IMultiplexedClientTransport>();

        Task connectTask = ConnectAsync(clientTransport);

        // Act
        await listener.DisposeAsync();

        // Assert

        // If using Quic and the listener is disposed during the ssl handshake this can fail
        // with AuthenticationException otherwise it fails with TransportException.
        Assert.That(
            async () => await connectTask,
            Throws.InstanceOf<IceRpcException>().Or.TypeOf<AuthenticationException>());

        async Task ConnectAsync(IMultiplexedClientTransport clientTransport)
        {
            // Establish connections until we get a failure.
            var connections = new List<IMultiplexedConnection>();
            try
            {
                while (true)
                {
                    IMultiplexedConnection connection = clientTransport.CreateConnection(
                        listener.ServerAddress,
                        provider.GetRequiredService<IOptions<MultiplexedConnectionOptions>>().Value,
                        provider.GetService<SslClientAuthenticationOptions>());
                    connections.Add(connection);

                    await connection.ConnectAsync(default);

                    // Continue until connect fails.
                }
            }
            finally
            {
                await Task.WhenAll(connections.Select(c => c.DisposeAsync().AsTask()));
            }
        }
    }

    /// <summary>Verifies that disabling the idle timeout doesn't abort the connection if it's idle.</summary>
    [Test]
    public async Task Connection_with_no_idle_timeout_is_not_aborted_when_idle()
    {
        // Arrange
        IServiceCollection services = CreateServiceCollection();

        services.AddOptions<SlicTransportOptions>("server").Configure(
            options => options.IdleTimeout = Timeout.InfiniteTimeSpan);
        services.AddOptions<SlicTransportOptions>("client").Configure(
            options => options.IdleTimeout = Timeout.InfiniteTimeSpan);

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        var clientTransport = provider.GetRequiredService<IMultiplexedClientTransport>();

        await using var clientConnection = clientTransport.CreateConnection(
            listener.ServerAddress,
            provider.GetRequiredService<IOptions<MultiplexedConnectionOptions>>().Value,
            provider.GetService<SslClientAuthenticationOptions>());

        var connectTask = clientConnection.ConnectAsync(default);
        await using var serverConnection = (await listener.AcceptAsync(default)).Connection;

        _ = await serverConnection.ConnectAsync(default);
        _ = await connectTask;

        ValueTask<IMultiplexedStream> acceptTask = serverConnection.AcceptStreamAsync(default);

        // Act
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Assert
        Assert.That(acceptTask.IsCompleted, Is.False);
    }

    /// <summary>Verifies that setting the idle timeout doesn't abort the connection if it's idle.</summary>
    [Test]
    public async Task Connection_with_idle_timeout_is_not_aborted_when_idle(
        [Values(true, false)] bool serverIdleTimeout)
    {
        // Arrange
        IServiceCollection services = CreateServiceCollection();

        var idleTimeout = TimeSpan.FromSeconds(1);
        if (serverIdleTimeout)
        {
            services.AddOptions<SlicTransportOptions>("server").Configure(options => options.IdleTimeout = idleTimeout);
        }
        else
        {
            services.AddOptions<SlicTransportOptions>("client").Configure(options => options.IdleTimeout = idleTimeout);
        }

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        var clientTransport = provider.GetRequiredService<IMultiplexedClientTransport>();
        await using var clientConnection = clientTransport.CreateConnection(
            listener.ServerAddress,
            provider.GetRequiredService<IOptions<MultiplexedConnectionOptions>>().Value,
            provider.GetService<SslClientAuthenticationOptions>());

        var connectTask = clientConnection.ConnectAsync(default);
        await using var serverConnection = (await listener.AcceptAsync(default)).Connection;

        _ = await serverConnection.ConnectAsync(default);
        _ = await connectTask;

        ValueTask<IMultiplexedStream> acceptTask = serverConnection.AcceptStreamAsync(default);

        // Act
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Assert
        Assert.That(acceptTask.IsCompleted, Is.False);
    }

    [Test]
    public async Task Create_client_connection_with_unknown_server_address_parameter_fails_with_format_exception()
    {
        await using ServiceProvider provider = CreateServiceCollection().BuildServiceProvider(validateScopes: true);
        var clientTransport = provider.GetRequiredService<IMultiplexedClientTransport>();

        var serverAddress = new ServerAddress(new Uri("icerpc://foo?unknown-parameter=foo"));

        // Act/Asserts
        Assert.Throws<ArgumentException>(() => clientTransport.CreateConnection(
            serverAddress,
            new MultiplexedConnectionOptions(),
            provider.GetService<SslClientAuthenticationOptions>()));
    }

    [Test]
    public async Task Create_server_connection_with_unknown_server_address_parameter_fails_with_format_exception()
    {
        await using ServiceProvider provider = CreateServiceCollection().BuildServiceProvider(validateScopes: true);
        var serverTransport = provider.GetRequiredService<IMultiplexedServerTransport>();

        var serverAddress = new ServerAddress(new Uri("icerpc://foo?unknown-parameter=foo"));

        // Act/Asserts
        Assert.Throws<ArgumentException>(() => serverTransport.Listen(
            serverAddress,
            new MultiplexedConnectionOptions(),
            provider.GetService<SslServerAuthenticationOptions>()));
    }

    /// <summary>Verifies we can dispose a stream without calling Complete on its Input or Output.</summary>
    [Test]
    public async Task Dispose_stream_without_complete()
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        await using IMultiplexedConnection serverConnection =
        await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        IMultiplexedStream clientStream = await clientConnection.CreateStreamAsync(bidirectional: true, default);
        await clientStream.Output.WriteAsync(_oneBytePayload);
        IMultiplexedStream serverStream = await serverConnection.AcceptStreamAsync(default);

        // Act
        await clientStream.DisposeAsync();
        await serverStream.DisposeAsync();
    }

    [Test]
    [Ignore("fails with Quic, see https://github.com/dotnet/runtime/issues/77216")]
    [TestCase(100)]
    [TestCase(512 * 1024)]
    public async Task Disposing_the_server_connection_completes_ReadsClosed_on_streams(int payloadSize)
    {
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        await using LocalAndRemoteStreams sut = await CreateAndAcceptStreamAsync(clientConnection, serverConnection);

        var payload = new ReadOnlySequence<byte>(new byte[payloadSize]);
        _ = sut.LocalStream.Output.WriteAsync(payload, endStream: true, CancellationToken.None).AsTask();
        _ = sut.RemoteStream.Output.WriteAsync(payload, endStream: true, CancellationToken.None).AsTask();

        await Task.Delay(100); // Ensures that the EOS is received by the remote stream.

        // Act
        await serverConnection.DisposeAsync();

        // Assert
        Assert.That(async () => await sut.LocalStream.InputClosed, Throws.InstanceOf<IceRpcException>());
        Assert.That(async () => await sut.RemoteStream.InputClosed, Throws.InstanceOf<IceRpcException>());
    }

    /// <summary>Verifies that disposing the connection aborts the streams.</summary>
    /// <param name="disposeServer">Whether to dispose the server connection or the client connection.
    /// </param>
    [Test]
    public async Task Disposing_the_connection_aborts_the_streams([Values(true, false)] bool disposeServer)
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        IMultiplexedConnection disposedConnection = disposeServer ? serverConnection : clientConnection;
        await using LocalAndRemoteStreams sut = await CreateAndAcceptStreamAsync(clientConnection, serverConnection);

        IMultiplexedStream disposedStream = disposeServer ? sut.RemoteStream : sut.LocalStream;
        IMultiplexedStream peerStream = disposeServer ? sut.LocalStream : sut.RemoteStream;

        // Act
        await disposedConnection.DisposeAsync();

        // Assert

        Assert.ThrowsAsync<IceRpcException>(async () => await disposedStream.Input.ReadAsync());
        Assert.ThrowsAsync<IceRpcException>(async () => await disposedStream.Output.WriteAsync(_oneBytePayload));

        Assert.ThrowsAsync<IceRpcException>(async () => await peerStream.Input.ReadAsync());
        Assert.ThrowsAsync<IceRpcException>(async () => await peerStream.Output.WriteAsync(_oneBytePayload));
    }

    [Test]
    public async Task Disposing_the_connection_closes_the_streams()
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        await using LocalAndRemoteStreams sut = await CreateAndAcceptStreamAsync(clientConnection, serverConnection);

        // Act
        await serverConnection.DisposeAsync();

        // Assert
        Assert.That(async () => await sut.LocalStream.InputClosed, Throws.TypeOf<IceRpcException>());
        Assert.That(async () => await sut.LocalStream.OutputClosed, Throws.TypeOf<IceRpcException>());
        Assert.That(async () => await sut.RemoteStream.InputClosed, Throws.TypeOf<IceRpcException>());
        Assert.That(async () => await sut.RemoteStream.OutputClosed, Throws.TypeOf<IceRpcException>());
    }

    /// <summary>Write data until the transport flow control start blocking, at this point we start a read task and
    /// ensure that this unblocks the pending write calls.</summary>
    [Test]
    public async Task Flow_control()
    {
        // Arrange
        var payload = new byte[1024 * 64];
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        await using LocalAndRemoteStreams sut = await CreateAndAcceptStreamAsync(clientConnection, serverConnection);
        sut.LocalStream.Input.Complete();
        sut.RemoteStream.Output.Complete();

        Task<FlushResult> writeTask;
        while (true)
        {
            writeTask = sut.LocalStream.Output.WriteAsync(payload).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            if (writeTask.IsCompleted)
            {
                await writeTask;
            }
            else
            {
                break;
            }
        }

        // Act
        Task readTask = ReadAsync(sut.RemoteStream);

        // Assert
        Assert.That(async () => await writeTask, Throws.Nothing);
        sut.LocalStream.Output.Complete();
        Assert.That(async () => await readTask, Throws.Nothing);

        static async Task ReadAsync(IMultiplexedStream stream)
        {
            ReadResult readResult = default;
            while (!readResult.IsCompleted)
            {
                readResult = await stream.Input.ReadAsync();
                stream.Input.AdvanceTo(readResult.Buffer.End);
            }
            stream.Input.Complete();
        }
    }

    /// <summary>Verifies that connection cannot exceed the bidirectional stream max count.</summary>
    [Test]
    public async Task Max_bidirectional_stream_stress_test()
    {
        // Arrange
        const int streamMaxCount = 16;
        const int createStreamCount = 32;

        var serviceCollection = CreateServiceCollection().AddMultiplexedTransportTest();
        serviceCollection.AddOptions<MultiplexedConnectionOptions>().Configure(
                options => options.MaxBidirectionalStreams = streamMaxCount);

        await using ServiceProvider provider = serviceCollection.BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        const int payloadSize = 16 * 1024;
        byte[] payloadData = Enumerable.Range(0, payloadSize).Select(i => (byte)(i % 256)).ToArray();
        var payload = new ReadOnlyMemory<byte>(payloadData);

        int streamCount = 0;
        int streamCountMax = 0;
        var mutex = new object();

        var streams = new List<IMultiplexedStream>();
        var tasks = new List<Task>();

        for (int i = 0; i < createStreamCount; ++i)
        {
            tasks.Add(ClientReadWriteAsync());
        }

        // Act
        for (int i = 0; i < createStreamCount; ++i)
        {
            tasks.Add(ServerReadWriteAsync(await serverConnection.AcceptStreamAsync(default)));
        }

        // Assert
        await Task.WhenAll(tasks);
        Assert.That(streamCountMax, Is.LessThanOrEqualTo(streamMaxCount));

        await CleanupStreamsAsync(streams.ToArray());

        async Task ClientReadWriteAsync()
        {
            IMultiplexedStream stream = await clientConnection.CreateStreamAsync(true, default);
            await stream.Output.WriteAsync(payload);
            lock (mutex)
            {
                streams.Add(stream);
                streamCount++;
                streamCountMax = Math.Max(streamCount, streamCountMax);
            }
            stream.Output.Complete();

            while (true)
            {
                ReadResult readResult = await stream.Input.ReadAsync();
                if (readResult.IsCompleted)
                {
                    stream.Input.AdvanceTo(readResult.Buffer.End);
                    break;
                }
                stream.Input.AdvanceTo(readResult.Buffer.End);
            }
            stream.Input.Complete();
        }

        async Task ServerReadWriteAsync(IMultiplexedStream stream)
        {
            while (true)
            {
                ReadResult readResult = await stream.Input.ReadAsync();
                if (readResult.IsCompleted)
                {
                    stream.Input.AdvanceTo(readResult.Buffer.End);
                    break;
                }
                stream.Input.AdvanceTo(readResult.Buffer.End);
            }
            stream.Input.Complete();

            lock (mutex)
            {
                streamCount--;
            }

            await stream.Output.WriteAsync(payload);
            stream.Output.Complete();
        }
    }

    /// <summary>Verifies that connection cannot exceed the unidirectional stream max count.</summary>
    [Test]
    public async Task Max_unidirectional_stream_stress_test()
    {
        // Arrange
        const int streamMaxCount = 16;
        const int createStreamCount = 32;

        var serviceCollection = CreateServiceCollection().AddMultiplexedTransportTest();
        serviceCollection.AddOptions<MultiplexedConnectionOptions>().Configure(
                options => options.MaxUnidirectionalStreams = streamMaxCount);

        await using ServiceProvider provider = serviceCollection.BuildServiceProvider(validateScopes: true);
        var clientConnection = provider.GetRequiredService<IMultiplexedConnection>();
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        await using IMultiplexedConnection serverConnection =
            await ConnectAndAcceptConnectionAsync(listener, clientConnection);

        const int payloadSize = 16 * 1024;
        byte[] payloadData = Enumerable.Range(0, payloadSize).Select(i => (byte)(i % 256)).ToArray();
        var payload = new ReadOnlyMemory<byte>(payloadData);

        int streamCount = 0;
        int streamCountMax = 0;
        var mutex = new object();

        var streams = new List<IMultiplexedStream>();
        var tasks = new List<Task>();
        for (int i = 0; i < createStreamCount; ++i)
        {
            tasks.Add(ClientWriteAsync());
        }

        // Act
        for (int i = 0; i < createStreamCount; ++i)
        {
            tasks.Add(ServerReadAsync(await serverConnection.AcceptStreamAsync(default)));
        }

        // Assert
        await Task.WhenAll(tasks);
        Assert.That(streamCountMax, Is.LessThanOrEqualTo(streamMaxCount));

        await CleanupStreamsAsync(streams.ToArray());

        async Task ClientWriteAsync()
        {
            IMultiplexedStream stream = await clientConnection.CreateStreamAsync(false, default);
            await stream.Output.WriteAsync(payload);
            lock (mutex)
            {
                streams.Add(stream);
                streamCount++;
                streamCountMax = Math.Max(streamCount, streamCountMax);
            }

            // It's important to write enough data to ensure that the last stream frame is not received before the
            // receiver starts reading.
            await stream.Output.WriteAsync(payload);
            await stream.Output.WriteAsync(payload);
            await stream.Output.WriteAsync(payload);
            await stream.Output.WriteAsync(payload);

            stream.Output.Complete();
        }

        async Task ServerReadAsync(IMultiplexedStream stream)
        {
            // The stream is terminated as soon as the last frame of the request is received, so we have
            // to decrement the count here before the request receive completes.
            lock (mutex)
            {
                streamCount--;
            }

            ReadResult readResult;
            do
            {
                readResult = await stream.Input.ReadAsync();
                stream.Input.AdvanceTo(readResult.Buffer.End);
            }
            while (!readResult.IsCompleted);

            stream.Input.Complete();
        }
    }

    [Test]
    public async Task Listen_twice_on_the_same_address_fails_with_a_transport_exception()
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection().BuildServiceProvider(validateScopes: true);
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
        var serverTransport = provider.GetRequiredService<IMultiplexedServerTransport>();

        // Act/Assert
        IceRpcException? exception = Assert.Throws<IceRpcException>(
            () => serverTransport.Listen(
                listener.ServerAddress,
                new MultiplexedConnectionOptions(),
                provider.GetService<SslServerAuthenticationOptions>()));
        // BUGFIX with Quic this throws an internal error https://github.com/dotnet/runtime/issues/78573
        Assert.That(
            exception!.IceRpcError,
            Is.EqualTo(IceRpcError.AddressInUse).Or.EqualTo(IceRpcError.IceRpcError));
    }

    [Test]
    public async Task Listener_server_address_transport_property_is_set()
    {
        // Arrange
        await using ServiceProvider provider = CreateServiceCollection()
            .AddMultiplexedTransportTest()
            .BuildServiceProvider(validateScopes: true);
        var transport = provider.GetRequiredService<IMultiplexedClientTransport>().Name;
        var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();

        // Act/Assert
        Assert.That(listener.ServerAddress.Transport, Is.EqualTo(transport));
    }

    /// <summary>Creates the service collection used for multiplexed transport conformance tests.</summary>
    protected abstract IServiceCollection CreateServiceCollection();

    private static async Task<LocalAndRemoteStreams> CreateAndAcceptStreamAsync(
        IMultiplexedConnection localConnection,
        IMultiplexedConnection remoteConnection,
        bool isBidirectional = true)
    {
        IMultiplexedStream localStream = await localConnection.CreateStreamAsync(
            bidirectional: isBidirectional,
            default).ConfigureAwait(false);
        _ = await localStream.Output.WriteAsync(_oneBytePayload);
        IMultiplexedStream remoteStream = await remoteConnection.AcceptStreamAsync(default);
        ReadResult readResult = await remoteStream.Input.ReadAsync();
        remoteStream.Input.AdvanceTo(readResult.Buffer.End);
        return new LocalAndRemoteStreams(localStream, remoteStream);
    }

    private readonly struct LocalAndRemoteStreams : IAsyncDisposable
    {
        internal IMultiplexedStream LocalStream { get; }

        internal IMultiplexedStream RemoteStream { get; }

        public ValueTask DisposeAsync() => CleanupStreamsAsync(LocalStream, RemoteStream);

        internal LocalAndRemoteStreams(IMultiplexedStream localStream, IMultiplexedStream remoteStream)
        {
            LocalStream = localStream;
            RemoteStream = remoteStream;
        }
    }

    private static async ValueTask CleanupStreamsAsync(params IMultiplexedStream[] streams)
    {
        foreach (IMultiplexedStream stream in streams)
        {
            if (stream.IsBidirectional)
            {
                stream.Output.Complete();
                stream.Input.Complete();
            }
            else if (stream.IsRemote)
            {
                stream.Input.Complete();
            }
            else
            {
                stream.Output.Complete();
            }
            await stream.DisposeAsync();
        }
    }

    private static async Task<IMultiplexedConnection> ConnectAndAcceptConnectionAsync(
        IListener<IMultiplexedConnection> listener,
        IMultiplexedConnection connection)
    {
        var connectTask = connection.ConnectAsync(default);
        var acceptTask = listener.AcceptAsync(default);
        if (connectTask.IsFaulted)
        {
            await connectTask;
        }
        if (acceptTask.IsFaulted)
        {
            await acceptTask;
        }
        var serverConnection = (await acceptTask).Connection;
        await serverConnection.ConnectAsync(default);
        await connectTask;
        return serverConnection;
    }
}

public static class MultiplexedTransportServiceCollectionExtensions
{
    public static IServiceCollection AddMultiplexedTransportTest(this IServiceCollection services) =>
        services.AddSingleton(provider =>
        {
            var listener = provider.GetRequiredService<IListener<IMultiplexedConnection>>();
            var clientTransport = provider.GetRequiredService<IMultiplexedClientTransport>();
            var connection = clientTransport.CreateConnection(
                listener.ServerAddress,
                provider.GetRequiredService<IOptions<MultiplexedConnectionOptions>>().Value,
                provider.GetService<SslClientAuthenticationOptions>());
            return connection;
        });
}