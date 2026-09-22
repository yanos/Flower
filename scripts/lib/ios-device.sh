# Shared helpers for the iOS device scripts. Sourced, never run.
#
# Both things in here exist for the same reason: a CoreDevice identifier and a
# provisioning profile each belong to one specific phone, and both go stale
# silently when that phone is replaced. The failures they produce name neither
# the phone nor the profile, so they read as the device in your hand refusing
# to cooperate:
#
#   - a stale device id resolves to something paired-but-absent, and devicectl
#     answers "The device is not able to fulfill the requested usage assertion
#     requirements" (CoreDeviceError 4016)
#   - a stale profile installs and then fails verification with "This
#     provisioning profile cannot be installed on this device" (0xe8008012)
#
# So neither is written down anywhere. The device is whichever one is plugged
# in, and the profile is whichever installed one actually covers that device.

# The app these scripts build, install and sign.
FLOWER_BUNDLE_ID="com.yanos.flower"

# Every connected physical device, as "<udid> <coredevice-id>" per line.
#
# Transport is what separates them from everything else devicectl remembers:
# a simulator connects over `sameMachine`, a real phone over `wired` or
# `localNetwork`, and a device that is merely paired-and-absent has no
# transport at all. devicectl fixes the column order itself, so the fields are
# read as first and last rather than by position.
ios_connected_devices() {
  xcrun devicectl list devices \
    --hide-headers --hide-default-columns \
    --columns "Identifier" --columns "CoreDevice ID" \
    --filter 'connectionProperties.transportType IN {"wired","localNetwork"}' \
    2>/dev/null | awk 'NF >= 2 { print $1, $NF }'
}

# Sets IOS_DEVICE_UDID and IOS_DEVICE_ID for the one connected device, or the
# one matching the optional argument (a UDID or a CoreDevice id). Exits with a
# message if there is no such device, or more than one and no way to choose.
ios_resolve_device() {
  local preferred="${1:-}" rows count

  rows=$(ios_connected_devices)
  if [ -n "$preferred" ]; then
    rows=$(printf '%s\n' "$rows" | grep -i -- "$preferred" || true)
  fi

  count=$(printf '%s' "$rows" | grep -c . || true)
  case "$count" in
    1)
      IOS_DEVICE_UDID=$(printf '%s' "$rows" | awk '{ print $1 }')
      IOS_DEVICE_ID=$(printf '%s' "$rows" | awk '{ print $2 }')
      ;;
    0)
      if [ -n "$preferred" ]; then
        echo "No connected iOS device matches '$preferred'." >&2
      else
        echo "No connected iOS device. Plug one in, unlock it, and trust this Mac." >&2
      fi
      exit 1
      ;;
    *)
      echo "More than one device is connected - pass the one you want:" >&2
      xcrun devicectl list devices \
        --filter 'connectionProperties.transportType IN {"wired","localNetwork"}' >&2
      exit 1
      ;;
  esac
}

# Prints the path of an installed provisioning profile that covers both this
# app and this device; returns non-zero when there is none.
#
# Two directories, because Xcode moved: it writes profiles to its own UserData
# directory now, and the .NET-for-iOS build reads both. The one the build will
# actually embed is decided by that build, not here - this only answers whether
# a usable one exists at all, which is the question worth asking before
# spending four minutes on a build that cannot install.
ios_profile_for_device() {
  local bundle_id="$1" udid="$2" plist f appid

  plist=$(mktemp)
  for f in "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"/*.mobileprovision \
           "$HOME/Library/MobileDevice/Provisioning Profiles"/*.mobileprovision; do
    [ -f "$f" ] || continue
    security cms -D -i "$f" -o "$plist" 2>/dev/null || continue

    appid=$(plutil -extract Entitlements.application-identifier raw -o - "$plist" 2>/dev/null) || continue
    case "$appid" in
      *".$bundle_id" | *".*") ;;
      *) continue ;;
    esac

    if plutil -extract ProvisionedDevices json -o - "$plist" 2>/dev/null | grep -q "$udid"; then
      rm -f "$plist"
      printf '%s\n' "$f"
      return 0
    fi
  done

  rm -f "$plist"
  return 1
}
