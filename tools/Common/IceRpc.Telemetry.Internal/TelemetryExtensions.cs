// Copyright (c) ZeroC, Inc.

using System.Diagnostics;

namespace IceRpc.Telemetry.Internal;

public partial record struct Telemetry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Telemetry" /> struct using the specified command-line arguments.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    public Telemetry(string[] args)
    {
        // Parse command-line arguments to get the version
        string version = args
            .SkipWhile(arg => arg != "--version")
            .Skip(1)
            .FirstOrDefault() ?? "unknown";

        IceRpcVersion = version;
        OperatingSystem = Environment.OSVersion.ToString();
        ProcessorCount = Environment.ProcessorCount;
        Memory = Process.GetCurrentProcess().Threads.Count;
    }
}
