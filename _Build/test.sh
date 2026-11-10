#!/usr/bin/env bash
# Builds Debug, runs an app's tests and prints only what is not a pass. Usage: test.sh [Suite] [--app=Name] [--baseline]
root="$(cd "$(dirname "$0")/.." && pwd)"
suite=""
baseline=0
app=Carbon
[ -d "$root/Thorium" ] && app=Thorium
for a in "$@"; do
	case "$a" in
		--baseline) baseline=1 ;;
		--app=*) app="${a#--app=}" ;;
		*) suite="$a" ;;
	esac
done
out="${TEMP:-/tmp}/aurora-test"
mkdir -p "$out"
log="$out/run.txt"
base="$root/$app/test-baseline.txt"

linux=0
[ "$(uname -s)" = "Linux" ] && linux=1
bin="$root/$app/bin/Debug/net10.0-windows10.0.22621.0"

sln="$root/$app/$app.sln"
[ -f "$sln" ] || sln="$root/AuroraEngine/ArctisAurora.sln"
if [ $linux -eq 1 ]; then
	bin="$root/$app/bin/Debug/net10.0"
	build=$(dotnet build "$root/$app/$app.csproj" -f net10.0 -v q -nologo -p:EnableWindowsTargeting=true 2>&1)
else
	build=$(dotnet build "$sln" -v q -nologo 2>&1)
fi
if [ $? -ne 0 ]; then
	echo "$build" | grep -E ' error ' | sort -u | head -20
	echo "BUILD FAILED"
	exit 255
fi

arg="--test"
[ -n "$suite" ] && arg="--test=$suite"
if [ $linux -eq 1 ]; then
	VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json xvfb-run -a -s "-screen 0 1920x1080x24" "$bin/$app" "$arg" > "$log" 2>&1
else
	"$bin/$app.exe" "$arg" > "$log" 2>&1
fi
code=$?

grep -aE '\[Test\] (FAIL|NEW|APPROVED) ' "$log" | sed -E 's/^.*\[Test\] //; s/  @[^ ]+$//' | cut -c1-250

kinds=$(grep -aE ' (ERROR|FATAL) ' "$log" | tr -d '\r' | sed -E 's/^[0-9:.]+ +(ERROR|FATAL) +[^ ]+ /\1 /' | sort -u)
if [ $baseline -eq 1 ]; then
	printf '%s\n' "$kinds" > "$base"
	echo "baseline written: $(grep -c . "$base") error kind(s)"
elif [ -f "$base" ]; then
	comm -13 <(tr -d '\r' < "$base" | sort -u) <(printf '%s\n' "$kinds") | grep . | sed 's/^/new error: /'
fi

summary=$(grep -aE '\[Test\] [0-9]+ passed' "$log" | sed -E 's/^.*\[Test\] //')
[ -z "$summary" ] && summary="no results line - the run died, log: $log"
echo "$summary"
exit $code
