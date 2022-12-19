// Copyright (c) ZeroC, Inc. All rights reserved.

using HelloQuicExample;
using IceRpc;
using IceRpc.Transports;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

await using var server = new Server(
    new Hello(),
    new SslServerAuthenticationOptions
    {
        ServerCertificate = new X509Certificate2("../../certs/server.p12", "password")
    },
    multiplexedServerTransport: new QuicServerTransport());

// Shuts down the server on Ctrl+C.
Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    _ = server.ShutdownAsync();
};

server.Listen();
await server.ShutdownComplete;
