#!/usr/bin/env bash
# Builds Debug and prints only what is not a pass. Usage: build.sh [Project.csproj|Solution.sln] [--rebuild]
root="$(cd "$(dirname "$0")/.." && pwd)"
target="$root/AuroraEngine/ArctisAurora.sln"
extra=""
for a in "$@"; do
	case "$a" in
		--rebuild) extra="--no-incremental" ;;
		*) target="$a" ;;
	esac
done
out="${TEMP:-/tmp}/aurora-build"
mkdir -p "$out"
log="$out/build.txt"

start=$SECONDS
dotnet build "$target" $extra -v:q -nologo -clp:ErrorsOnly > "$log" 2>&1
code=$?
if [ $code -eq 0 ]; then
	echo "BUILD OK ($((SECONDS - start))s)"
	exit 0
fi

# A running app holding a DLL is not a code error; one line says which process to close.
locked=$(grep -oE 'The file is locked by: "[^"]+"' "$log" | sort -u | sed -E 's/.*by: "([^"]+)"/\1/')
if [ -n "$locked" ]; then
	echo "LOCKED: $locked holds the output; close it and rebuild"
fi
# Each error repeats once per target framework and project; the [project::TFM] suffix is what differs.
grep -E ': error ' "$log" | grep -vE 'MSB3027|MSB3021' | sed -E 's/ \[[^]]*\]$//' | sort -u | head -20
echo "BUILD FAILED (log: $log)"
exit $code
