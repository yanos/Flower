# Shared helpers for the scripts that run an iOS runner app on a simulator
# (ios-device-checks.sh, ios-tests.sh). Sourced, never run.
#
# A runner reports by writing a transcript into its own Documents directory,
# which this reads out of the simulator's data container, and its last line is
# a tally. Console.WriteLine from a .NET iOS app does not reliably reach
# `simctl launch --console-pty`, and a run that passes but prints nothing is
# indistinguishable from a hang - see Tests/iOSRunner/RunnerTranscript.cs.

# Sets IOS_SIMULATOR to the named simulator, or to the newest iPhone when no
# name is given, and boots it.
#
# Newest runtime last in simctl's output, so the last match is the most current
# iOS available rather than the oldest still installed - which is also the one
# that first shows what the next iOS breaks. iOS 27 killing both runners at
# launch, for not adopting the scene lifecycle, was found exactly that way.
ios_simulator_boot() {
  IOS_SIMULATOR="${1:-}"
  if [ -z "$IOS_SIMULATOR" ]; then
    IOS_SIMULATOR=$(xcrun simctl list devices available | grep -oE '^\s+iPhone [^(]+' | tail -1 | xargs)
  fi

  echo "==> Simulator: $IOS_SIMULATOR"

  # Booting an already-booted simulator is an error, not a no-op.
  xcrun simctl boot "$IOS_SIMULATOR" 2>/dev/null || true
  xcrun simctl bootstatus "$IOS_SIMULATOR" -b >/dev/null
}

# Builds a runner from clean: ios_simulator_build <project> <dir to clean>...
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
  local log
  log=$(mktemp)
  if ! dotnet build "$project" -c Debug -r iossimulator-arm64 >"$log" 2>&1; then
    cat "$log"
    rm -f "$log"
    exit 1
  fi
  rm -f "$log"
}

# Installs and launches a runner, waits for its tally, prints the transcript,
# and sets IOS_TALLY to the tally line:
#
#   ios_simulator_run <app> <bundle id> <transcript> <tally prefix> <timeout s>
#
# Exits 1 when no tally arrives in time - a run that did not finish, which is
# neither a pass nor any particular failure. Anything exported as
# SIMCTL_CHILD_<NAME> reaches the app as <NAME>.
ios_simulator_run() {
  local app="$1" bundle_id="$2" transcript="$3" tally_prefix="$4" timeout="$5"

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
  xcrun simctl launch "$IOS_SIMULATOR" "$bundle_id" >/dev/null

  local _
  for _ in $(seq "$timeout"); do
    if [ -f "$log" ] && grep -q "^$tally_prefix" "$log"; then
      break
    fi
    sleep 1
  done

  xcrun simctl terminate "$IOS_SIMULATOR" "$bundle_id" 2>/dev/null || true

  if [ ! -f "$log" ] || ! grep -q "^$tally_prefix" "$log"; then
    echo "==> No tally after ${timeout}s - the run did not finish. What there was:"
    cat "$log" 2>/dev/null || echo "(the app wrote nothing at all)"
    exit 1
  fi

  cat "$log"
  IOS_TALLY=$(grep "^$tally_prefix" "$log" | tail -1)
}
