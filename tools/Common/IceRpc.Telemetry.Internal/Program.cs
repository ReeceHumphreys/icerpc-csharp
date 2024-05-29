// Copyright (c) ZeroC, Inc.

using IceRpc;
using IceRpc.Retry;
using IceRpc.Telemetry.Internal;
using System.Diagnostics;

const int timeout = 3000; // The timeout for the RPC call in milliseconds.
const int maxAttempts = 3; // The maximum number of attempts to retry the RPC call.
const string uri = "icerpc://localhost"; // The URI of the server.

// Parse command-line arguments to get the version
string version = args
    .SkipWhile(arg => arg != "--version")
    .Skip(1)
    .FirstOrDefault() ?? "unknown";

// Create a telemetry object with the version, OS version, processor count, and thread count.
string osVersion = Environment.OSVersion.ToString();
int processorCount = Environment.ProcessorCount;
int threadCount = Process.GetCurrentProcess().Threads.Count;
var telemetry = new Telemetry(version, osVersion, processorCount, threadCount);

try
{
    // Create a client connection that logs messages to a logger with category IceRpc.ClientConnection.
    await using var connection = new ClientConnection(new Uri(uri));

    // Create an invocation pipeline with two interceptors.
    Pipeline pipeline = new Pipeline()
        .UseRetry(new RetryOptions { MaxAttempts = maxAttempts })
        .UseDeadline(defaultTimeout: TimeSpan.FromMilliseconds(timeout))
        .Into(connection);

    // Create a greeter proxy with this invocation pipeline.
    var reporter = new ReporterProxy(pipeline);

    // Upload the telemetry to the server.
    await reporter.UploadAsync(telemetry);

    // Shutdown the connection.
    await connection.ShutdownAsync();
}
catch (Exception) { }
