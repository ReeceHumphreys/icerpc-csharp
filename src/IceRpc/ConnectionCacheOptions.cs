// Copyright (c) ZeroC, Inc. All rights reserved.

namespace IceRpc;

/// <summary>A property bag used to configure a <see cref="ConnectionCache"/>.</summary>
public record class ConnectionCacheOptions
{
    /// <summary>Gets or sets the client connection options used for connections created by the connection cache.
    /// </summary>
    public ClientConnectionOptions ClientConnectionOptions { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether or not the cache prefers an existing connection over
    /// creating a new one.</summary>
    public bool PreferExistingConnection { get; set; } = true;
}