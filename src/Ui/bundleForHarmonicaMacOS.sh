#!/bin/bash

# ── harmonicaRF macOS App Bundle Script ───────────────────────────────────────
#
# The standalone harmonicaRF binary (docs/design/harmonicarf.md §3.1). It is the SAME assembly as
# circuitRF built with a different Main — `-p:CrfApp=harmonica` selects the StartupObject and the
# macOS bundle identity — so this script is bundleForMacOS.sh with a different app name, plist,
# icon and bundle id, and deliberately nothing else.
#
# THE PUBLISH COMMAND IS RECORDED HERE, IN THE REPOSITORY, NOT IN A SHELL HISTORY (M3's own gate).
# Run from src/Ui/:
#   chmod +x bundleForHarmonicaMacOS.sh && ./bundleForHarmonicaMacOS.sh
#
# For a non-macOS RID the publish line alone is the whole of it:
#   dotnet publish -c Release -r win-x64   --self-contained -p:CrfApp=harmonica
#   dotnet publish -c Release -r linux-x64 --self-contained -p:CrfApp=harmonica

APP_NAME="harmonicaRF"
EXECUTABLE_NAME="harmonicaRF"         # the renamed host - see CircuitRF.Ui.csproj, CrfRenameApphost
# The version comes from the repo-root VERSION file — the one place it is written. It is
# STAMPED into the bundled Info.plist below, so the .app can never report a version the
# release does not carry (the plist's own value is only a placeholder).
source "$(dirname "${BASH_SOURCE[0]}")/../../packaging/version.sh"
VERSION="$CRF_VERSION"
BUNDLE_ID="com.circuitRF.harmonicaRF"
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
CUSTOM_PLIST="./Assets/macOS/Harmonica-Info.plist"
ENTITLEMENTS="./Assets/macOS/Entitlements.plist"
ICON_SVG="./Assets/artwork/harmonicaRF-app-icon.svg"
ICON_ICNS="./Assets/harmonicaRFIcon.icns"

PUBLISH_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/publish"
BUNDLE_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/${APP_NAME}.app"
CONTENTS_DIR="${BUNDLE_DIR}/Contents"
MAC_OS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

echo "🚀 Starting macOS bundling for harmonicaRF ${VERSION}..."

if [ ! -f "$CUSTOM_PLIST" ]; then
    echo "❌ Info.plist not found at: $CUSTOM_PLIST"
    exit 1
fi

if [ ! -f "$ENTITLEMENTS" ]; then
    echo "❌ Entitlements.plist not found at: $ENTITLEMENTS"
    exit 1
fi

# R-h8-9's three-place trap: CFBundleIdentifier lives in the plist, BUNDLE_ID lives here, and
# codesign is given the entitlements separately. Nothing derives one from another, so this is the
# one place the first two can be compared — and a mismatch is checked rather than described.
PLIST_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$CUSTOM_PLIST" 2>/dev/null)
if [ "$PLIST_ID" != "$BUNDLE_ID" ]; then
    echo "❌ Bundle id mismatch: this script says '${BUNDLE_ID}', ${CUSTOM_PLIST} says '${PLIST_ID}'."
    echo "   They must agree — macOS keys Launch Services, the Dock and quarantine off the plist's."
    exit 1
fi

# A second guard for the failure that actually costs something: two bundles claiming ONE identifier.
CIRCUITRF_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "./Assets/macOS/Info.plist" 2>/dev/null)
if [ "$PLIST_ID" == "$CIRCUITRF_ID" ]; then
    echo "❌ harmonicaRF and circuitRF both claim '${PLIST_ID}'. macOS will treat them as one app."
    exit 1
fi

echo "📦 Publishing .NET application (CrfApp=harmonica)..."
dotnet publish -r $RID -c Release --self-contained -p:CrfApp=harmonica
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

# -- The geometry kernel is circuitRF's, not harmonicaRF's (brief-em3d-62 R-em3d62-5c) ---------------
#
# The shared publish tree carries geometry-kernel/ (OpenCASCADE and its worker) because circuitRF
# ships it. harmonicaRF never loads it, so it is dropped rather than signed: shipping it here would put
# ~56 MB of libraries in a bundle that does not use them, and an LGPL component in a package whose
# notices were never written for it.
if [ -d "${MAC_OS_DIR}/geometry-kernel" ]; then
    echo "   dropping geometry-kernel (circuitRF's; harmonicaRF does not load it)"
    rm -rf "${MAC_OS_DIR}/geometry-kernel"
fi

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

# The icon is BUILT from the committed SVG rather than committed as a binary — the repository holds
# circuitRF's brand artwork as SVG for the same reason. Warn-and-continue, exactly like circuitRF's
# own script: a missing icon must never be why somebody cannot produce a bundle.
if [ ! -f "$ICON_ICNS" ] && [ -f "$ICON_SVG" ]; then
    if command -v rsvg-convert >/dev/null 2>&1 || command -v qlmanage >/dev/null 2>&1; then
        echo "🎨 Building ${ICON_ICNS} from ${ICON_SVG}..."
        ICONSET=$(mktemp -d)/harmonicaRF.iconset
        mkdir -p "$ICONSET"
        for SZ in 16 32 64 128 256 512 1024; do
            if command -v rsvg-convert >/dev/null 2>&1; then
                rsvg-convert -w $SZ -h $SZ "$ICON_SVG" -o "${ICONSET}/icon_${SZ}x${SZ}.png" 2>/dev/null
            else
                qlmanage -t -s $SZ -o "$ICONSET" "$ICON_SVG" >/dev/null 2>&1
                mv "${ICONSET}/$(basename "$ICON_SVG").png" "${ICONSET}/icon_${SZ}x${SZ}.png" 2>/dev/null
            fi
        done
        # iconutil wants the @2x names too; the plain set is enough for a usable icon.
        iconutil -c icns "$ICONSET" -o "$ICON_ICNS" 2>/dev/null \
            || echo "⚠️  iconutil could not build the icon — continuing without one."
    else
        echo "⚠️  No SVG rasteriser (rsvg-convert / qlmanage) — continuing without an icon."
    fi
fi

if [ -f "$ICON_ICNS" ]; then
    echo "🎨 Copying icon..."
    cp "$ICON_ICNS" "${RESOURCES_DIR}/harmonicaRFIcon.icns"
else
    echo "⚠️  harmonicaRFIcon.icns not found — application will use the default icon."
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
VMHOST="${MAC_OS_DIR}/crf-vmhost"
VMHOST_ENTITLEMENTS="../../tools/macos-vmhost/crf-vmhost.entitlements"
if [ -f "$VMHOST" ] && [ -f "$VMHOST_ENTITLEMENTS" ]; then
    echo "🔐 Re-signing crf-vmhost with its virtualization entitlement..."
    # --options runtime unconditionally here, matching tools/macos-vmhost/build.sh: this binary
    # asks for a privileged entitlement, so it carries the hardened runtime even in a dev build.
    codesign --force --sign "$SIGN_IDENTITY" --entitlements "$VMHOST_ENTITLEMENTS" \
             --options runtime $TIMESTAMP_FLAG "$VMHOST" || {
        echo "❌ Could not sign crf-vmhost."; exit 1; }

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
