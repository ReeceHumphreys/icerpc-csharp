// Copyright (c) ZeroC, Inc.

using IceRpc;

namespace GreeterCore;

/// <summary>Reports a remote dispatch error: the server or the service could not dispatch the request successfully.
/// </summary>
public class DispatchException : Exception
{
    public DispatchException(StatusCode statusCode, string message)
        : base($"The dispatch failed with status code {statusCode} and message: {message}")
    {
    }
}