#!/bin/bash
set -euo pipefail

arch=$1
version=${2:?Release version is required.}
APP_NAME="./哔哩下载姬.app"
if [ -z "${PUBLISH_OUTPUT_DIRECTORY:-}" ]; then
  target_framework="$(dotnet msbuild ../../DownKyi/DownKyi.csproj -nologo -verbosity:quiet -getProperty:TargetFramework -p:Configuration=Release)"
  if [ -z "$target_framework" ] || [[ "$target_framework" == *$'\n'* ]]; then
    echo 'Could not resolve the DownKyi target framework.' >&2
    exit 1
  fi
  PUBLISH_OUTPUT_DIRECTORY="../../DownKyi/bin/Release/$target_framework/osx-$arch/publish/."
fi

INFO_PLIST="./Info.plist"
ICON_FILE="./logo.icns"

if [ -d "$APP_NAME" ]; then
  rm -rf "$APP_NAME"
fi

mkdir "$APP_NAME"

mkdir "$APP_NAME/Contents"
mkdir "$APP_NAME/Contents/MacOS"
mkdir "$APP_NAME/Contents/Resources"

cp "$INFO_PLIST" "$APP_NAME/Contents/Info.plist"
/bin/bash ./set-bundle-version.sh "$APP_NAME/Contents/Info.plist" "$version"
cp "$ICON_FILE" "$APP_NAME/Contents/Resources/$ICON_FILE"
cp -a "$PUBLISH_OUTPUT_DIRECTORY" "$APP_NAME/Contents/MacOS"
if [ ! -x "$APP_NAME/Contents/MacOS/aria2/aria2c" ]; then
  chmod +x "$APP_NAME/Contents/MacOS/aria2/aria2c"
fi
if [ ! -x "$APP_NAME/Contents/MacOS/ffmpeg/ffmpeg" ]; then
  chmod +x "$APP_NAME/Contents/MacOS/ffmpeg/ffmpeg"
fi

/bin/bash ./prepare-app-layout.sh "$APP_NAME"
