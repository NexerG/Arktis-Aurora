#!/usr/bin/env bash
# Builds Release with the profiler compiled in and runs a host's Perf suite. Usage: PerfTests.sh [Host]
root="$(cd "$(dirname "$0")/.." && pwd)"
host="${1:-Thorium}"

sln="$root/$host/$host.sln"
[ -f "$sln" ] || sln="$root/AuroraEngine/ArctisAurora.sln"
if [ "$(uname -s)" = "Linux" ]; then
	dotnet build "$root/$host/$host.csproj" -f net10.0 -c Release "-p:DefineConstants=TRACE%3BPROFILE" -p:EnableWindowsTargeting=true -v q -nologo || exit 1
	bin="$root/$host/bin/Release/net10.0"
	cd "$bin" && VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json xvfb-run -a -s "-screen 0 1920x1080x24" "./$host" --test=Perf
else
	dotnet build "$sln" -c Release "-p:DefineConstants=TRACE%3BPROFILE" -v q -nologo || exit 1
	bin="$root/$host/bin/Release/net10.0-windows10.0.22621.0"
	cd "$bin" && "./$host.exe" --test=Perf
fi
