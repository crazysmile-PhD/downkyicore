# FFmpeg source build

## Current version

`script/ffmpeg-source/vcpkg.json` pins one vcpkg registry baseline. That baseline selects the FFmpeg release and the source versions of x264, LAME and platform headers. `script/ffmpeg-source/ffmpeg-source.tar.gz` holds one current copy of the official FFmpeg source archive. The six RID features define the required encoders. `script/ffmpeg-source/build.py` verifies the stored archive against the pinned port's SHA-512, builds FFmpeg and ffprobe with vcpkg binary caching disabled, checks the executables and required encoders, then installs only the current result into `DownKyi.Core/Binary/<RID>/ffmpeg` for packaging.

The release jobs build for Windows x86/x64, Linux x64/ARM64 and macOS x64/ARM64. Linux ARM64 uses a native Ubuntu 22.04 ARM64 runner so its binaries retain the same minimum glibc generation as the x64 build. The final DownKyi packages include FFmpeg, ffprobe and their dependency license texts. No separate FFmpeg binary release or versioned binary manifest is published.

## Updates

The weekly `update-ffmpeg-source.yml` workflow checks the newest vcpkg FFmpeg port. When it selects a newer FFmpeg release, it downloads the official source archive, verifies the port's SHA-512 and opens a PR replacing the one stored archive and baseline together. This follows FFmpeg releases once the vcpkg port has adopted them; it does not promise to pick up a new official FFmpeg tag on its publication day. If several versions arrive before the next successful update, the candidate jumps to the newest version available in vcpkg. The working tree holds one source archive and no standalone FFmpeg binaries. Git history still records previous source archive revisions, as it does other project files.

`build.yml` runs the six release builds and package checks on the source PR. The separate auto-merge workflow accepts only a successful Build run for the exact current updater PR head and a PR that changes exactly the stored archive and baseline; repository branch protection still requires its normal checks. A failed build leaves the existing source and baseline in `main`. A new DownKyi tag compiles the source selected by that tag and bundles it in each final package.

Once the source PR has merged, rebuilding the current version no longer needs the official FFmpeg archive to remain online. The build still needs the pinned vcpkg registry and the sources of its other dependencies. Old DownKyi commits can be inspected through Git history; a deliberate historical rebuild may use the currently maintained FFmpeg source if that old code remains compatible with it.

## Local check

From the repository root on Windows with Visual Studio C++ Build Tools and Python installed:

```powershell
pwsh ./script/ffmpeg.ps1 x64
```

On Linux or macOS, run `./script/ffmpeg.sh linux x64` or `./script/ffmpeg.sh mac arm64` from `script/`. The first build downloads the pinned vcpkg toolchain and compiles its dependencies, so it takes substantially longer than the former binary download.
