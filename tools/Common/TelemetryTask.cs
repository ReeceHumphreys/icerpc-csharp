using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.IO;
using System.Runtime.InteropServices;

public class TelemetryTask : ToolTask
{

    /// <summary>Gets or sets the directory containing the telemetry scripts.</summary>
    [Required]
    public string ScriptPath { get; set; } = "";

    /// <summary>Gets or sets additional arguments to pass to the script.</summary>
    public string Arguments { get; set; } = "";

    /// <inheritdoc/>
    protected override string ToolName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
                "telemetry.bat" : "telemetry.sh";


    /// <inheritdoc/>
    protected override string GenerateFullPathToTool()
    {
        // Constructs the full path to the script file
        return Path.Combine(ScriptPath, ToolName);
    }

    /// <inheritdoc/>
    protected override string GenerateCommandLineCommands()
    {
        // Returns the additional command-line arguments to be passed to the script
        // Assumes that any required arguments are set to the Arguments property
        return Arguments;
    }
}
