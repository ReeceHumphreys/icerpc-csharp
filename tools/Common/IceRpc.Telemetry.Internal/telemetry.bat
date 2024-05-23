@echo off
setlocal

REM Change directory to the location of this script
cd /d %~dp0

REM Call the dotnet command with all passed arguments and redirect output to null
call dotnet IceRpc.Telemetry.Internal.dll %* > nul 2>&1

endlocal
