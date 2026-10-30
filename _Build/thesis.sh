#!/usr/bin/env bash
# Builds Debug, exports the thesis headless and diffs it against the LuaLaTeX reference. Usage: thesis.sh [pdfdiff args]
root="$(cd "$(dirname "$0")/.." && pwd)"
dir="C:/Projects-Repositories/Thorium Notes/BakisTest"
ref="$dir/Real_Time_Global_Illumination_in_Video_Games.pdf"
ours="$dir/Sablonas.pdf"
bin="$root/Thorium/bin/Debug/net10.0-windows10.0.22621.0"
log="${TEMP:-/tmp}/aurora-thesis.txt"

build=$(dotnet build "$root/AuroraEngine/ArctisAurora.sln" -v q -nologo 2>&1)
if [ $? -ne 0 ]; then
	echo "$build" | grep -E ' error ' | sort -u | head -20
	echo "BUILD FAILED"
	exit 255
fi

before=$(stat -c %Y "$ours" 2>/dev/null || echo 0)
"$bin/Thorium.exe" "$dir/Sablonas.tex" --exec "Wait 6000; Focus; Tex.ExportPdf; Wait 6000; Quit" > "$log" 2>&1
grep -a '\[Vulkan\]' "$log" | head -5
after=$(stat -c %Y "$ours" 2>/dev/null || echo 0)
if [ "$after" = "$before" ]; then
	echo "EXPORT FAILED: Sablonas.pdf not rewritten, log: $log"
	exit 254
fi

PYTHONIOENCODING=utf-8 python "$root/_Build/pdfdiff.py" "$ref" "$ours" "$@"
