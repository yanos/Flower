#!/bin/bash
# Registers the connected iPhone with this Mac's Apple developer account and
# reissues the provisioning profile for the app, so a new phone can be deployed
# to without opening Xcode. Idempotent: if a profile already covers the device,
# it does nothing.
#
#   scripts/register-ios-device.sh                # the connected device
#   scripts/register-ios-device.sh <device-id>    # when several are plugged in
#
# A provisioning profile names the exact devices it may install on, so the
# first deploy to a new phone fails verification - "This provisioning profile
# cannot be installed on this device" (0xe8008012) - after the whole build has
# already run. The fix is to add that UDID to the account and reissue the
# profile, and the usual advice is to open Xcode and let it prepare the device.
# This is that, from a terminal.
#
# The obvious automated route is not available here: registering devices
# through the developer portal (an App Store Connect API key, fastlane) needs a
# paid membership, and the account this signs with is a free Personal Team,
# which has no portal presence at all. What it does have is Xcode's own signing
# machinery, which xcodebuild exposes - `-allowProvisioningUpdates` lets it talk
# to Apple as the account in Xcode's settings, and
# `-allowProvisioningDeviceRegistration` lets it register the device it is
# building for.
#
# Both act on an Xcode target, and Flower.iOS is not one. `ProvisioningType`
# in Flower.iOS.csproj is read by Rider and Visual Studio, not by MSBuild: the
# .NET-for-iOS SDK has no notion of automatic provisioning and simply signs
# with the best profile already installed. So what gets built here is a
# throwaway Xcode project that exists only to carry the same bundle id and
# team - Apple reissues the profile as a side effect of signing it, Xcode
# installs the new one where the .NET build already looks, and the next build
# embeds it. The stub's own product is deleted with the temp directory.
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lib/ios-device.sh

ios_resolve_device "${1:-}"

if PROFILE=$(ios_profile_for_device "$FLOWER_BUNDLE_ID" "$IOS_DEVICE_UDID"); then
  echo "==> Device $IOS_DEVICE_UDID is already covered by $(basename "$PROFILE")"
  exit 0
fi

# The team is discovered rather than written down, for the same reason the
# device id is: one hardcoded identifier per phone is what this script exists
# to undo. FLOWER_TEAM_ID overrides for an account signed into more than one.
TEAM_ID="${FLOWER_TEAM_ID:-}"
if [ -z "$TEAM_ID" ]; then
  TEAM_ID=$(defaults read com.apple.dt.Xcode IDEProvisioningTeamByIdentifier 2>/dev/null \
    | sed -n 's/.*teamID = \([A-Za-z0-9]*\);.*/\1/p' | sort -u)
fi

case "$(printf '%s' "$TEAM_ID" | grep -c . || true)" in
  1) ;;
  0)
    echo "No Apple developer account in Xcode. Add one in Xcode > Settings > Accounts." >&2
    exit 1
    ;;
  *)
    echo "More than one team is signed in - set FLOWER_TEAM_ID to one of:" >&2
    printf '  %s\n' $TEAM_ID >&2
    exit 1
    ;;
esac

echo "==> Registering $IOS_DEVICE_UDID with team $TEAM_ID"

STUB=$(mktemp -d)
trap 'rm -rf "$STUB"' EXIT
mkdir -p "$STUB/Stub.xcodeproj/xcshareddata/xcschemes"

cat > "$STUB/main.swift" <<'STUB_SOURCE_EOF'
// Never runs, never installed. This target exists so that xcodebuild has
// something to sign, which is what makes it reissue the profile.
print("stub")
STUB_SOURCE_EOF

# Hand-written rather than copied from a checked-in project, so there is no
# second place holding a bundle id or a team. Object ids are arbitrary; they
# only have to be unique within the file and 24 hex digits wide.
cat > "$STUB/Stub.xcodeproj/project.pbxproj" <<'STUB_PBXPROJ_EOF'
// !$*UTF8*$!
{
	archiveVersion = 1;
	classes = {
	};
	objectVersion = 56;
	objects = {

/* Begin PBXBuildFile section */
		AA0000000000000000000002 /* main.swift in Sources */ = {isa = PBXBuildFile; fileRef = AA0000000000000000000001 /* main.swift */; };
/* End PBXBuildFile section */

/* Begin PBXFileReference section */
		AA0000000000000000000001 /* main.swift */ = {isa = PBXFileReference; lastKnownFileType = sourcecode.swift; path = main.swift; sourceTree = "<group>"; };
		AA0000000000000000000003 /* Stub.app */ = {isa = PBXFileReference; explicitFileType = wrapper.application; includeInIndex = 0; path = Stub.app; sourceTree = BUILT_PRODUCTS_DIR; };
/* End PBXFileReference section */

/* Begin PBXFrameworksBuildPhase section */
		AA0000000000000000000004 /* Frameworks */ = {isa = PBXFrameworksBuildPhase; buildActionMask = 2147483647; files = (); runOnlyForDeploymentPostprocessing = 0; };
/* End PBXFrameworksBuildPhase section */

/* Begin PBXGroup section */
		AA0000000000000000000005 = {isa = PBXGroup; children = (AA0000000000000000000001, AA0000000000000000000006); sourceTree = "<group>"; };
		AA0000000000000000000006 /* Products */ = {isa = PBXGroup; children = (AA0000000000000000000003); name = Products; sourceTree = "<group>"; };
/* End PBXGroup section */

/* Begin PBXNativeTarget section */
		AA0000000000000000000007 /* Stub */ = {isa = PBXNativeTarget; buildConfigurationList = AA000000000000000000000B /* Build configuration list for PBXNativeTarget "Stub" */; buildPhases = (AA0000000000000000000008 /* Sources */, AA0000000000000000000004 /* Frameworks */); buildRules = (); dependencies = (); name = Stub; productName = Stub; productReference = AA0000000000000000000003 /* Stub.app */; productType = "com.apple.product-type.application"; };
/* End PBXNativeTarget section */

/* Begin PBXProject section */
		AA0000000000000000000009 /* Project object */ = {isa = PBXProject; attributes = {BuildIndependentTargetsInParallel = 1; TargetAttributes = {AA0000000000000000000007 = {}; }; }; buildConfigurationList = AA000000000000000000000A /* Build configuration list for PBXProject "Stub" */; compatibilityVersion = "Xcode 15.0"; developmentRegion = en; hasScannedForEncodings = 0; knownRegions = (en, Base); mainGroup = AA0000000000000000000005; productRefGroup = AA0000000000000000000006 /* Products */; projectDirPath = ""; projectRoot = ""; targets = (AA0000000000000000000007 /* Stub */); };
/* End PBXProject section */

/* Begin PBXSourcesBuildPhase section */
		AA0000000000000000000008 /* Sources */ = {isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = (AA0000000000000000000002 /* main.swift in Sources */); runOnlyForDeploymentPostprocessing = 0; };
/* End PBXSourcesBuildPhase section */

/* Begin XCBuildConfiguration section */
		AA000000000000000000000C /* Debug */ = {isa = XCBuildConfiguration; buildSettings = {ALWAYS_SEARCH_USER_PATHS = NO; CLANG_ENABLE_MODULES = YES; CLANG_ENABLE_OBJC_ARC = YES; IPHONEOS_DEPLOYMENT_TARGET = 15.0; ONLY_ACTIVE_ARCH = YES; SDKROOT = iphoneos; SWIFT_OPTIMIZATION_LEVEL = "-Onone"; SWIFT_VERSION = 5.0; }; name = Debug; };
		AA000000000000000000000D /* Debug */ = {isa = XCBuildConfiguration; buildSettings = {CODE_SIGN_STYLE = Automatic; CURRENT_PROJECT_VERSION = 1; GENERATE_INFOPLIST_FILE = YES; INFOPLIST_KEY_UILaunchScreen_Generation = YES; MARKETING_VERSION = 1.0; PRODUCT_NAME = "$(TARGET_NAME)"; TARGETED_DEVICE_FAMILY = "1,2"; }; name = Debug; };
/* End XCBuildConfiguration section */

/* Begin XCConfigurationList section */
		AA000000000000000000000A /* Build configuration list for PBXProject "Stub" */ = {isa = XCConfigurationList; buildConfigurations = (AA000000000000000000000C /* Debug */); defaultConfigurationIsVisible = 0; defaultConfigurationName = Debug; };
		AA000000000000000000000B /* Build configuration list for PBXNativeTarget "Stub" */ = {isa = XCConfigurationList; buildConfigurations = (AA000000000000000000000D /* Debug */); defaultConfigurationIsVisible = 0; defaultConfigurationName = Debug; };
/* End XCConfigurationList section */
	};
	rootObject = AA0000000000000000000009 /* Project object */;
}
STUB_PBXPROJ_EOF

# A scheme, because -destination (which is how xcodebuild learns which device
# to register) belongs to a scheme-based build.
cat > "$STUB/Stub.xcodeproj/xcshareddata/xcschemes/Stub.xcscheme" <<'STUB_SCHEME_EOF'
<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion = "1600" version = "1.7">
   <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
      <BuildActionEntries>
         <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
            <BuildableReference
               BuildableIdentifier = "primary"
               BlueprintIdentifier = "AA0000000000000000000007"
               BuildableName = "Stub.app"
               BlueprintName = "Stub"
               ReferencedContainer = "container:Stub.xcodeproj">
            </BuildableReference>
         </BuildActionEntry>
      </BuildActionEntries>
   </BuildAction>
</Scheme>
STUB_SCHEME_EOF

BUILD_LOG=$(mktemp)
trap 'rm -rf "$STUB"; rm -f "$BUILD_LOG"' EXIT

if ! xcodebuild -project "$STUB/Stub.xcodeproj" -scheme Stub \
     -destination "platform=iOS,id=$IOS_DEVICE_UDID" \
     -derivedDataPath "$STUB/DerivedData" \
     -allowProvisioningUpdates -allowProvisioningDeviceRegistration \
     PRODUCT_BUNDLE_IDENTIFIER="$FLOWER_BUNDLE_ID" DEVELOPMENT_TEAM="$TEAM_ID" \
     build >"$BUILD_LOG" 2>&1; then
  tail -40 "$BUILD_LOG" >&2
  echo >&2
  echo "Registration failed. If it is asking to authenticate, open Xcode once" >&2
  echo "(Settings > Accounts) - two-factor sign-in cannot happen from here." >&2
  exit 1
fi

# The build succeeding is not the claim worth making; a profile that covers
# this device is.
if ! PROFILE=$(ios_profile_for_device "$FLOWER_BUNDLE_ID" "$IOS_DEVICE_UDID"); then
  echo "xcodebuild succeeded but no profile covers $IOS_DEVICE_UDID." >&2
  echo "Check that the device is unlocked, trusted, and in Developer Mode." >&2
  exit 1
fi

echo "==> Registered. Profile: $(basename "$PROFILE")"
