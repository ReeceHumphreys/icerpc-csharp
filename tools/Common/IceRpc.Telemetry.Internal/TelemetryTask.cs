// Copyright (c) ZeroC, Inc.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace IceRpc.Telemetry.Internal;

/// TODO
public class TelemetryTask : ToolTask
{

    /// TODO
    [Required]
    public string Version { get; set; }

    /// TODO
    [Required]
    public string Hash { get; set; }

    /// TODO
    [Required]
    public string UpdatedFiles { get; set; }

    /// TODO
    [Required]
    public string Source { get; set; }

    /// TODO
    [Required]
    public string WorkingDirectory { get; set; } = "";

    /// <inheritdoc/>
    protected override string ToolName => "dotnet";

    /// <inheritdoc/>
    protected override string GetWorkingDirectory() => WorkingDirectory;

    /// <inheritdoc/>
    protected override string GenerateFullPathToTool()
    {
        return ToolName;
    }

    /// <inheritdoc/>
    protected override string GenerateCommandLineCommands()
    {
        CommandLineBuilder commandLine = new CommandLineBuilder();
        commandLine.AppendFileNameIfNotNull("IceRpc.Telemetry.Internal.dll");
        commandLine.AppendSwitch("--version");
        commandLine.AppendSwitch(Version);
        commandLine.AppendSwitch("--source");
        commandLine.AppendSwitch(Source);
        commandLine.AppendSwitch("--hash");
        commandLine.AppendSwitch(Hash);
        commandLine.AppendSwitch("--updated-files");
        commandLine.AppendSwitch(UpdatedFiles);

        return commandLine.ToString();
    }
}
