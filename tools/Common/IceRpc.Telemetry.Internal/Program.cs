// Copyright (c) ZeroC, Inc.

using IceRpc;
using IceRpc.Retry;
using IceRpc.Telemetry.Internal;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

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

// Load the root CA certificate
using var rootCA = new X509Certificate2("certs/cacert.der");
var clientAuthenticationOptions = new SslClientAuthenticationOptions
{
    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
    {
        using var customChain = new X509Chain();
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        customChain.ChainPolicy.DisableCertificateDownloads = true;
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.Add(rootCA);
        return customChain.Build((X509Certificate2)certificate!);
    }
};

// Create a client connection that logs messages to a logger with category IceRpc.ClientConnection.
await using var connection = new ClientConnection(new Uri(uri), clientAuthenticationOptions);

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
