using IceRpc;
using Microsoft.Extensions.Logging;
using IceRpc.Telemetry.Internal;
using System.Diagnostics;
using System.Reflection;
// Pull some information from the system / environment such as the OS version,
// IceRPC version, etc for telemetry purposes.

try
{
    // Default value for version
    string version = "unknown";

    // Parse command-line arguments to get the version
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--version" && i + 1 < args.Length)
        {
            version = args[i + 1];
            break;
        }
    }

    string osVersion = Environment.OSVersion.ToString();
    int processorCount = Environment.ProcessorCount;
    int threadCount = Process.GetCurrentProcess().Threads.Count;
    var telemetry = new Telemetry(version, osVersion, processorCount, threadCount);

    // Create a simple console logger factory and configure the log level for category IceRpc.
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        builder
            .AddSimpleConsole()
            .AddFilter("IceRpc", LogLevel.Information));

    // Create a client connection that logs messages to a logger with category IceRpc.ClientConnection.
    await using var connection = new ClientConnection(
        new Uri("icerpc://localhost"),
        logger: loggerFactory.CreateLogger<ClientConnection>());

    // Create an invocation pipeline with two interceptors.
    Pipeline pipeline = new Pipeline()
        .UseLogger(loggerFactory)
        .UseDeadline(defaultTimeout: TimeSpan.FromSeconds(10))
        .Into(connection);

    // Create a greeter proxy with this invocation pipeline.
    var reporter = new ReporterProxy(pipeline);

    // Upload the telemetry to the server.
    await reporter.UploadAsync(telemetry);

    // Shutdown the connection.
    await connection.ShutdownAsync();
}
catch (Exception ex)
{
    Console.WriteLine("Error: {0}", ex.Message);
}
