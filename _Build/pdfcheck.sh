#!/usr/bin/env bash
# Builds Debug, exports a .tex headless and diffs it against its LaTeX reference. Usage: pdfcheck.sh <file.tex> <reference.pdf> [pdfdiff args]
root="$(cd "$(dirname "$0")/.." && pwd)"
if [ $# -lt 2 ]; then
	echo "usage: pdfcheck.sh <file.tex> <reference.pdf> [pdfdiff args]"
	exit 253
fi
tex="$1"
ref="$2"
shift 2
ours="${tex%.*}.pdf"
bin="$root/Thorium/bin/Debug/net10.0-windows10.0.22621.0"
log="${TEMP:-/tmp}/aurora-pdfcheck.txt"

if [ ! -f "$tex" ] || [ ! -f "$ref" ]; then
	echo "MISSING: $([ -f "$tex" ] || echo "$tex ")$([ -f "$ref" ] || echo "$ref")"
	exit 253
fi
if [ "$(cd "$(dirname "$ours")" && pwd)/$(basename "$ours")" = "$(cd "$(dirname "$ref")" && pwd)/$(basename "$ref")" ]; then
	echo "REFUSED: the export would overwrite the reference $ref"
	exit 253
fi

build=$(dotnet build "$root/Thorium/Thorium.sln" -v q -nologo 2>&1)
if [ $? -ne 0 ]; then
	echo "$build" | grep -E ' error ' | sort -u | head -20
	echo "BUILD FAILED"
	exit 255
fi

before=$(stat -c %Y "$ours" 2>/dev/null || echo 0)
"$bin/Thorium.exe" "$tex" --exec "Wait 6000; Focus; Tex.ExportPdf; Wait 6000; Quit" > "$log" 2>&1
grep -a '\[Vulkan\]' "$log" | head -5
after=$(stat -c %Y "$ours" 2>/dev/null || echo 0)
if [ "$after" = "$before" ]; then
	echo "EXPORT FAILED: $(basename "$ours") not rewritten, log: $log"
	exit 254
fi

PYTHONIOENCODING=utf-8 python "$root/_Build/pdfdiff.py" "$ref" "$ours" "$@"
