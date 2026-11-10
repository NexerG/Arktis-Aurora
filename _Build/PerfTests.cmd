@echo off
rem Builds Release with the profiler compiled in and runs a host's Perf suite. Usage: PerfTests.cmd [Host]
set SOLUTION_DIR=%~dp0..
set HOST=%~1
if "%HOST%"=="" set HOST=Thorium
set SLN=%SOLUTION_DIR%\%HOST%\%HOST%.sln
if not exist "%SLN%" set SLN=%SOLUTION_DIR%\AuroraEngine\ArctisAurora.sln
dotnet build "%SLN%" -c Release "-p:DefineConstants=TRACE%%3BPROFILE" -v q -nologo || exit /b 1
"%SOLUTION_DIR%\%HOST%\bin\Release\net10.0-windows10.0.22621.0\%HOST%.exe" --test=Perf
exit /b %ERRORLEVEL%
