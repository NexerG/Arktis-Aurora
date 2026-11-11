#!/usr/bin/env bash
# Builds Debug, then writes the generated sections of the vault pages. Usage: codedocs.sh [--check] [--only Project]
root="$(cd "$(dirname "$0")/.." && pwd)"
sln="$root/AuroraEngine/ArctisAurora.sln"
out="${TEMP:-/tmp}/aurora-codedocs"
mkdir -p "$out"
log="$out/build.txt"

targets="$sln"
for p in Thorium Carbon _Build; do
	[ -f "$root/$p/$p.csproj" ] || continue
	grep -q "$p\\.csproj" "$sln" || targets="$targets $root/$p/$p.csproj"
done
: > "$log"
for t in $targets; do
	if ! dotnet build "$t" -v:q -nologo -clp:ErrorsOnly >> "$log" 2>&1; then
		grep -E ': error ' "$log" | sed -E 's/ \[[^]]*\]$//' | sort -u | head -20
		echo "BUILD FAILED (log: $log)"
		exit 255
	fi
done

winroot="$(cd "$root" && pwd -W 2>/dev/null || pwd)"
exe="$root/_Build/bin/Debug/net10.0-windows10.0.22621.0/_Build.exe"
[ -f "$exe" ] || exe="$root/_Build/bin/Debug/net10.0-windows10.0.22621.0/_Build"
"$exe" codedocs "$winroot" "$@"
