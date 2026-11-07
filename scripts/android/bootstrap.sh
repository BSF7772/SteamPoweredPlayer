#!/usr/bin/env bash
# Fetch a private Android SDK (cmdline-tools, platform-tools, platform, build-tools) and Gradle
# into $ANDROID_ROOT (default: ./.tools/android). Linux x86_64 only. Needs JDK 17+ on PATH.
# Nothing is installed system-wide. Re-runnable: skips what is already there.
set -euo pipefail
ROOT="${ANDROID_ROOT:-$PWD/.tools/android}"
CMDLINE_BUILD="${CMDLINE_BUILD:-16111833}"     # from https://dl.google.com/android/repository/repository2-3.xml
GRADLE_VER="${GRADLE_VER:-9.9.0}"
PLATFORM="${PLATFORM:-android-36}"
BUILD_TOOLS="${BUILD_TOOLS:-36.0.0}"
mkdir -p "$ROOT"
cd "$ROOT"
export ANDROID_HOME="$ROOT/sdk"
if [ ! -x "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" ]; then
  curl -fsSLo cmdline.zip "https://dl.google.com/android/repository/commandlinetools-linux-${CMDLINE_BUILD}_latest.zip"
  rm -rf ct && mkdir ct && unzip -q cmdline.zip -d ct
  mkdir -p "$ANDROID_HOME/cmdline-tools"
  mv ct/cmdline-tools "$ANDROID_HOME/cmdline-tools/latest"
  rm -rf ct cmdline.zip
fi
SDKM="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
yes | "$SDKM" --sdk_root="$ANDROID_HOME" --licenses >/dev/null || true
"$SDKM" --sdk_root="$ANDROID_HOME" "platform-tools" "platforms;$PLATFORM" "build-tools;$BUILD_TOOLS"
if [ ! -x "$ROOT/gradle-$GRADLE_VER/bin/gradle" ]; then
  curl -fsSLo gradle.zip "https://services.gradle.org/distributions/gradle-${GRADLE_VER}-bin.zip"
  unzip -q gradle.zip && rm gradle.zip
fi
echo "ANDROID_HOME=$ANDROID_HOME"
echo "GRADLE=$ROOT/gradle-$GRADLE_VER/bin/gradle"
