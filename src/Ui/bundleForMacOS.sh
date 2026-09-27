#!/bin/bash

# ── circuitRF macOS App Bundle Script ─────────────────────────────────────────
# Adapted from splotRF/src/bundleForMacOS.sh. Run from src/Ui/:
#   chmod +x bundleForMacOS.sh && ./bundleForMacOS.sh

APP_NAME="circuitRF"
EXECUTABLE_NAME="circuitRF"           # the renamed host - see CircuitRF.Ui.csproj, CrfRenameApphost
# The version comes from the repo-root VERSION file — the one place it is written. It is
# STAMPED into the bundled Info.plist below, so the .app can never report a version the
# release does not carry (the plist's own value is only a placeholder).
source "$(dirname "${BASH_SOURCE[0]}")/../../packaging/version.sh"
VERSION="$CRF_VERSION"
BUNDLE_ID="com.circuitRF.circuitRF"
TARGET_FRAMEWORK="net10.0"
# ── Which Mac this bundle is for ──────────────────────────────────────────────
#
# Apple Silicon (osx-arm64) or Intel (osx-x64). CRF_RID names it; the default is THE MACHINE DOING
# THE BUILDING, and that default is a statement about what can work rather than a convenience.
#
# The .NET side of this bundle cross-publishes to either RID happily. Everything else in it does
# not: the helper programs that land beside the assemblies (crf-vmhost, osdi-worker) are compiled
# by their own native toolchains for the host's own architecture, and the Linux VM image is built
# for the guest THIS host's Virtualization.framework can run — an aarch64 guest on Apple Silicon,
# an x86-64 one on Intel. So a cross-architecture bundle is a working application with a set of
# helpers that cannot load a single compiled device model. Build the Intel .dmg on an Intel Mac.
#
# packaging/macos/build-macos.sh sets CRF_RID and checks the result; setting it by hand here is for
# producing a .NET-only bundle deliberately.
if [ -z "${CRF_RID:-}" ]; then
    case "$(uname -m)" in
        arm64)  CRF_RID="osx-arm64" ;;
        x86_64) CRF_RID="osx-x64"   ;;
        *) echo "❌ Unsupported macOS architecture: $(uname -m)"; exit 1 ;;
    esac
fi
RID="$CRF_RID"
CUSTOM_PLIST="./Assets/macOS/Info.plist"
ENTITLEMENTS="./Assets/macOS/Entitlements.plist"

PUBLISH_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/publish"
BUNDLE_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/${APP_NAME}.app"
CONTENTS_DIR="${BUNDLE_DIR}/Contents"
MAC_OS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

echo "🚀 Starting macOS bundling for circuitRF ${VERSION}..."

if [ ! -f "$CUSTOM_PLIST" ]; then
    echo "❌ Info.plist not found at: $CUSTOM_PLIST"
    exit 1
fi

if [ ! -f "$ENTITLEMENTS" ]; then
    echo "❌ Entitlements.plist not found at: $ENTITLEMENTS"
    exit 1
fi

echo "📦 Publishing .NET application..."
dotnet publish -r $RID -c Release --self-contained
if [ $? -ne 0 ]; then echo "❌ Publish failed."; exit 1; fi

if [ -d "$BUNDLE_DIR" ]; then
    echo "🧹 Removing old bundle..."
    rm -rf "$BUNDLE_DIR"
fi

echo "📁 Creating ${APP_NAME}.app structure..."
mkdir -p "$MAC_OS_DIR" "$RESOURCES_DIR"

echo "🚚 Copying published files..."
cp -R "${PUBLISH_DIR}/." "$MAC_OS_DIR/"
chmod +x "${MAC_OS_DIR}/${EXECUTABLE_NAME}"

# -- The OTHER TWO APPLICATIONS' HOSTS, which dotnet publish leaves sitting right here ---------
#
# circuitRF, harmonicaRF and wBond are one project with three Mains, and all three publish to the
# SAME directory: CrfApp changes the StartupObject and the renamed host, not $(PublishDir). Nothing
# ever deletes from a publish tree, so once all three have been bundled on a machine the tree holds
# all three hosts - and each is a SELF-CONTAINED SINGLE-FILE binary carrying the whole application,
# ~131 MB of it. Copying the tree wholesale therefore put harmonicaRF and wBond inside circuitRF.app,
# and the only symptom was the disk image's size: measured 2026-09-16, the arm64 circuitRF .dmg was
# 234 MB against the x64 one's 122 MB, purely because that machine had never bundled the other two
# for Intel. Everything installed, launched and ran correctly the whole time.
#
# So the host this bundle is FOR is kept and the other two are dropped. It is done after the copy
# rather than by filtering it because the set to remove is named by what this script is building,
# and because a stale host from a previous run is removed either way.
for _crf_other in circuitRF harmonicaRF wBond; do
    if [ "$_crf_other" != "$EXECUTABLE_NAME" ] && [ -f "${MAC_OS_DIR}/${_crf_other}" ]; then
        echo "   dropping ${_crf_other} (another application's host, left in the shared publish tree)"
        rm -f "${MAC_OS_DIR}/${_crf_other}"
    fi
done

# -- Nothing under Contents/MacOS may have a DOT in a DIRECTORY name --------------------------
#
# codesign reads every directory under Contents/MacOS as code, and a dot in a directory's name
# makes it read that directory as a NESTED BUNDLE. It looks for an Info.plist, does not find one,
# and rejects THE WHOLE APP:
#
#     circuitRF.app: bundle format unrecognized, invalid, or unsuitable
#     In subcomponent: .../Contents/MacOS/examples/PDK PCells/.generated-cells
#
# The message names the directory and nothing else - not the rule, not the remedy - so the first
# guess is always that the directory's CONTENTS are wrong. They are not; the name is. Measured
# 2026-09-16: an empty directory called `.foo` fails identically, `--deep` changes nothing (the
# rule is the LOCATION, not the flag), a directory called `results` signs fine, and the same
# `.generated-cells` tree under Contents/RESOURCES signs fine too.
#
# Dot FILES are unaffected. `.cws`, `.ccell`, `.gitignore` - every dotfile the example workspaces
# carry - sign without comment. Only directories are read as bundles.
#
# What put one here was src/Ui/CircuitRF.Ui.csproj sweeping a workspace's local `.generated-cells`
# PCell cache into the publish tree; that copy now excludes it. This refusal stays because the
# next such directory will arrive the same way, and because the publish tree NEVER DELETES a stale
# file - a folder excluded from the copy today is still sitting in bin/ from yesterday's build.
STRAY_DIRS=$(find "$MAC_OS_DIR" -type d -name '*.*')
if [ -n "$STRAY_DIRS" ]; then
    echo "❌ A directory name under Contents/MacOS has a dot in it; codesign reads it as a"
    echo "   nested bundle and will reject the whole .app:"
    echo "$STRAY_DIRS" | sed "s|^${MAC_OS_DIR}/|     |"
    echo ""
    echo "   Nothing is wrong with what is inside it - the NAME is what codesign objects to."
    echo "   If it is build output (a .generated-cells cache is the usual one), delete it from"
    echo "   the publish tree and build again; dotnet publish leaves stale files behind, so an"
    echo "   exclusion added to the .csproj does not remove one already there:"
    echo ""
    echo "     rm -rf \"${PUBLISH_DIR}\""
    exit 1
fi

echo "📄 Copying Info.plist..."
cp "$CUSTOM_PLIST" "${CONTENTS_DIR}/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${CRF_VERSION}" "${CONTENTS_DIR}/Info.plist"
# CFBundleVersion must be purely numeric, so it gets the version's numeric head.
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${CRF_VERSION_CORE}" "${CONTENTS_DIR}/Info.plist"

if [ -f "./Assets/circuitRFIcon.icns" ]; then
    echo "🎨 Copying icon..."
    cp "./Assets/circuitRFIcon.icns" "$RESOURCES_DIR/"
else
    echo "⚠️  circuitRFIcon.icns not found — application will use default icon."
fi

# ── Who signs it, and what that decides ───────────────────────────────────────
#
# CRF_SIGN_IDENTITY names a Developer ID Application certificate; the default "-" is AD-HOC.
#
# THIS IS THE ONLY KNOB THAT DECIDES WHETHER USERS SEE A GATEKEEPER PROMPT. An ad-hoc signature has
# no identity behind it, so Gatekeeper cannot trust it however it is built — `spctl -a` rejects an
# ad-hoc bundle, measured, and since macOS 15 the old Control-click ▸ Open bypass is gone, leaving
# the user a trip through System Settings ▸ Privacy & Security ▸ Open Anyway. No entitlement, plist
# key or codesign flag changes that. Signing with a real identity AND notarising does, and nothing
# else does.
#
# The two paths differ in more than the identity string, which is why they are computed here rather
# than substituted into one command:
#
#   --options runtime   the hardened runtime, which notarisation REQUIRES. It is off for ad-hoc
#                       because it buys nothing there and would impose the JIT/library-validation
#                       restrictions on a developer build for no reason. The entitlements that make
#                       .NET survive it are already in Entitlements.plist.
#   --timestamp         a SECURE timestamp from Apple, which notarisation also requires and which
#                       needs the network. An ad-hoc signature cannot carry one at all, hence
#                       --timestamp=none there; leaving it out of a real signing is a notarisation
#                       rejection ("The signature does not include a secure timestamp").
SIGN_IDENTITY="${CRF_SIGN_IDENTITY:--}"

if [ "$SIGN_IDENTITY" = "-" ]; then
    TIMESTAMP_FLAG="--timestamp=none"
    RUNTIME_FLAG=""
    echo "🔐 Code signing (ad-hoc)..."
    echo "   Users will meet Gatekeeper. Set CRF_SIGN_IDENTITY to a Developer ID certificate and"
    echo "   notarise to ship an app that just opens — see BUILDING.md."
else
    TIMESTAMP_FLAG="--timestamp"
    RUNTIME_FLAG="--options runtime"
    echo "🔐 Code signing as: ${SIGN_IDENTITY}"
fi

codesign --force --deep --sign "$SIGN_IDENTITY" --entitlements "$ENTITLEMENTS" \
         $RUNTIME_FLAG $TIMESTAMP_FLAG "$BUNDLE_DIR"
if [ $? -ne 0 ]; then echo "❌ Code signing failed."; exit 1; fi

# ── crf-vmhost's OWN entitlement, and why this runs AFTER the bundle is signed ────────────────
#
# `--deep` re-signs every nested executable with the entitlements given HERE, and circuitRF's are
# not crf-vmhost's. So the deep pass silently replaced com.apple.security.virtualization with
# circuitRF's own set, and the packaged VM host — correctly signed by tools/macos-vmhost/build.sh
# minutes earlier — arrived unable to create a virtual machine:
#
#     the virtual machine configuration was rejected: Invalid virtual machine configuration.
#     The process doesn't have the "com.apple.security.virtualization" entitlement.
#
# Nothing about the build said so. It only shows in a bundle that has been through this script, and
# only when a compiled device model is actually run — so `dotnet run` worked and every .dmg shipped
# a VM host that could not start. (Measured 2026-08-22 with `codesign -d --entitlements`.)
#
# Inside-out is the fix, and the order is the whole of it: sign the nested binary with its own
# entitlements, then RE-SEAL the bundle without `--deep` so the outer signature records the new
# cdhash and does not touch the inner binary again.
RESEAL=0
VMHOST="${MAC_OS_DIR}/crf-vmhost"
VMHOST_ENTITLEMENTS="../../tools/macos-vmhost/crf-vmhost.entitlements"
if [ -f "$VMHOST" ] && [ -f "$VMHOST_ENTITLEMENTS" ]; then
    echo "🔐 Re-signing crf-vmhost with its virtualization entitlement..."
    # --options runtime unconditionally here, matching tools/macos-vmhost/build.sh: this binary
    # asks for a privileged entitlement, so it carries the hardened runtime even in a dev build.
    codesign --force --sign "$SIGN_IDENTITY" --entitlements "$VMHOST_ENTITLEMENTS" \
             --options runtime $TIMESTAMP_FLAG "$VMHOST" || {
        echo "❌ Could not sign crf-vmhost."; exit 1; }
    RESEAL=1
fi

# ── The geometry kernel, inside-out, for the same reason (brief-em3d-62 R-em3d62-5c) ─────────────
#
# Contents/MacOS/geometry-kernel/ holds the worker and OpenCASCADE's libraries. Each is signed ON ITS
# OWN, with the bundle's identity: `--deep` above re-signed the worker with circuitRF's entitlements,
# which are not the worker's (it needs none), and it is not a substitute for signing nested code in
# order. With a real identity the worker carries the hardened runtime, whose LIBRARY VALIDATION then
# accepts these libraries because every one carries the same team identity; notarisation covers them
# with the bundle. The re-seal below records the new signatures without `--deep`.
#
# Only this script does this. harmonicaRF and wBond do not load the kernel, so their bundle scripts
# drop the folder instead of signing it.
KERNEL_DIR="${MAC_OS_DIR}/geometry-kernel"
if [ -d "$KERNEL_DIR" ]; then
    echo "🔐 Signing the geometry kernel's libraries and worker individually..."
    for lib in "$KERNEL_DIR"/*.dylib; do
        [ -f "$lib" ] || continue
        codesign --force --sign "$SIGN_IDENTITY" $RUNTIME_FLAG $TIMESTAMP_FLAG "$lib" || {
            echo "❌ Could not sign $(basename "$lib")."; exit 1; }
    done
    if [ -f "$KERNEL_DIR/geometry-worker" ]; then
        codesign --force --sign "$SIGN_IDENTITY" $RUNTIME_FLAG $TIMESTAMP_FLAG "$KERNEL_DIR/geometry-worker" || {
            echo "❌ Could not sign geometry-worker."; exit 1; }
    fi
    RESEAL=1
fi

if [ "$RESEAL" = 1 ]; then
    codesign --force --sign "$SIGN_IDENTITY" --entitlements "$ENTITLEMENTS" \
             $RUNTIME_FLAG $TIMESTAMP_FLAG "$BUNDLE_DIR" || {
        echo "❌ Could not re-seal the bundle."; exit 1; }
fi

echo ""
echo "✅ Bundle created: ${BUNDLE_DIR}"
if [ "$SIGN_IDENTITY" = "-" ]; then
    echo "   To distribute without a Gatekeeper prompt, sign and notarise:"
    echo "     CRF_SIGN_IDENTITY=\"Developer ID Application: NAME (TEAMID)\" $0"
    echo "   then notarise the disk image — packaging/macos/build-macos.sh does it for you."
fi
