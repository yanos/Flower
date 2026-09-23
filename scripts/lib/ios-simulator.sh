# Shared helpers for the scripts that run an iOS runner app on a simulator
# (ios-device-checks.sh, ios-tests.sh). Sourced, never run.
#
# A runner reports by writing a transcript into its own Documents directory,
# which this reads out of the simulator's data container, and its last line is
# a tally. Console.WriteLine from a .NET iOS app does not reliably reach
# `simctl launch --console-pty`, and a run that passes but prints nothing is
# indistinguishable from a hang - see Tests/iOSRunner/RunnerTranscript.cs.

# "4m07s" from a number of seconds - for the build and run timings below.
ios_simulator_duration() {
  printf '%dm%02ds' $(( $1 / 60 )) $(( $1 % 60 ))
}

# Sets IOS_SIMULATOR to a simulator's UDID and boots it: the newest iPhone,
# or the one named by the argument, on the newest iOS runtime - or, when
# IOS_SIMULATOR_RUNTIME is set (a major version, "26"), on the newest runtime of
# that version, failing rather than falling back when there is none.
#
# By UDID rather than by name, because a name is not one simulator: "iPhone 17"
# exists once per installed runtime, and booting it by name took whichever
# simctl resolved first, so which iOS a run was on was not something the run
# said. CI names its iOS legs after the version, and pins it here.
#
# Newest by default because the newest runtime is the one that first shows
# what the next iOS breaks. iOS 27 killing both runners at launch, for not
# adopting the scene lifecycle, was found exactly that way.
ios_simulator_boot() {
  local name="${1:-}" major="${IOS_SIMULATOR_RUNTIME:-}" chosen

  chosen=$(xcrun simctl list devices available -j | jq -r --arg name "$name" --arg major "$major" '
    [ .devices | to_entries[]
      | select(.key | test("SimRuntime\\.iOS-[0-9]"))
      | (.key | capture("iOS-(?<v>[0-9-]+)$").v | split("-") | map(tonumber)) as $version
      | select($major == "" or ($version[0] | tostring) == $major)
      | .value[]
      | select(.name | startswith("iPhone"))
      | select($name == "" or .name == $name)
      | { version: $version, udid, name } ]
    | sort_by(.version) | last
    | if . == null then empty else "\(.udid)\t\(.name) on iOS \(.version | map(tostring) | join("."))" end')

  if [ -z "$chosen" ]; then
    echo "==> No available iPhone simulator${name:+ named \"$name\"}${major:+ on iOS $major}. Installed runtimes:"
    xcrun simctl list runtimes | grep -i 'ios' || true
    exit 1
  fi

  IOS_SIMULATOR="${chosen%%$'\t'*}"
  echo "==> Simulator: ${chosen#*$'\t'}"

  # Booting an already-booted simulator is an error, not a no-op.
  xcrun simctl boot "$IOS_SIMULATOR" 2>/dev/null || true
  xcrun simctl bootstatus "$IOS_SIMULATOR" -b >/dev/null
}

# Builds a runner from clean, in Release: ios_simulator_build <project> <dir to clean>...
#
# Release because it is optimized and compiled ahead of time, which is what a
# release runs, and running anything on iOS is only worth it for running what
# ships. The runner lands in bin/Release/<tfm>/iossimulator-arm64/.
#
# Always from clean, for the reason Flower.iOS/deploy.sh gives at length: an
# incremental iOS build here reliably launches into a Mono AOT crash ("Managed
# Stacktrace: at <unknown> <0xffffffff>") that a clean rebuild always fixes.
# Costs a few minutes; the alternative is a crash that reads exactly like a
# failing run.
ios_simulator_build() {
  local project="$1"
  shift

  echo "==> Cleaning"
  local dir
  for dir in "$@"; do
    rm -rf "$dir/obj" "$dir/bin"
  done

  echo "==> Building"
  local log started=$SECONDS
  log=$(mktemp)
  if ! dotnet build "$project" -c Release -r iossimulator-arm64 >"$log" 2>&1; then
    cat "$log"
    rm -f "$log"
    echo "==> Build failed after $(ios_simulator_duration $(( SECONDS - started )))"
    exit 1
  fi
  rm -f "$log"
  echo "==> Built in $(ios_simulator_duration $(( SECONDS - started )))"
}

# Installs and launches a runner, waits for its tally, prints the transcript,
# and sets IOS_TALLY to the tally line:
#
#   ios_simulator_run <app> <bundle id> <transcript> <tally prefix> <timeout s> [summarize]
#
# The transcript is printed through `summarize <file>` - `cat` unless another
# function is named - both for a finished run and for one that timed out.
#
# Exits 1 when no tally arrives in time - a run that did not finish, which is
# neither a pass nor any particular failure. Anything exported as
# SIMCTL_CHILD_<NAME> reaches the app as <NAME>.
ios_simulator_run() {
  local app="$1" bundle_id="$2" transcript="$3" tally_prefix="$4" timeout="$5" summarize="${6:-cat}"

  echo "==> Installing"
  xcrun simctl install "$IOS_SIMULATOR" "$app"

  # The container only exists once the app has been installed, and its path
  # changes with every reinstall - so ask for it now rather than remembering
  # one.
  local container log
  container=$(xcrun simctl get_app_container "$IOS_SIMULATOR" "$bundle_id" data)
  log="$container/Documents/$transcript"
  rm -f "$log"

  echo "==> Running"
  local started=$SECONDS
  xcrun simctl launch "$IOS_SIMULATOR" "$bundle_id" >/dev/null

  local _
  for _ in $(seq "$timeout"); do
    if [ -f "$log" ] && grep -q "^$tally_prefix" "$log"; then
      break
    fi
    sleep 1
  done

  xcrun simctl terminate "$IOS_SIMULATOR" "$bundle_id" 2>/dev/null || true

  local ran
  ran=$(ios_simulator_duration $(( SECONDS - started )))

  if [ ! -f "$log" ] || ! grep -q "^$tally_prefix" "$log"; then
    echo "==> No tally after ${timeout}s - the run did not finish. What there was:"
    if [ -f "$log" ]; then
      "$summarize" "$log"
    else
      echo "(the app wrote nothing at all)"
    fi
    exit 1
  fi

  "$summarize" "$log"
  echo "==> Ran in $ran"
  IOS_TALLY=$(grep "^$tally_prefix" "$log" | tail -1)
}
