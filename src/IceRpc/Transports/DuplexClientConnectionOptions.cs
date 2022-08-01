// Copyright (c) ZeroC, Inc. All rights reserved.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Security;

namespace IceRpc.Transports;

/// <summary>A property bag used to configure a client <see cref="IDuplexConnection"/>.</summary>
public sealed record class DuplexClientConnectionOptions : DuplexConnectionOptions
{
    /// <summary>Gets or sets the SSL client authentication options.</summary>
    /// <value>The SSL client authentication options. When not null, <see
    /// cref="IDuplexConnection.ConnectAsync(CancellationToken)"/> will either establish a secure connection or
    /// fail.</value>
    public SslClientAuthenticationOptions? ClientAuthenticationOptions { get; set; }

    /// <summary>Gets or sets the connection's endpoint. The endpoint of a connection is the address of the server-end
    /// of that connection.</summary>
    public Endpoint Endpoint { get; set; }
}