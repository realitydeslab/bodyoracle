"""Export, archive, sign and upload the Body Oracle iOS app to TestFlight.

App Store Connect rejects development builds, so this drives the release entry point
(`BodyOracleBuild.IOSRelease`) rather than the one the device tests use, then archives with
xcodebuild and hands the archive to App Store Connect. Signing and upload both rely on the Apple
account configured in Xcode; `-allowProvisioningUpdates` lets Xcode fetch or create the
distribution certificate and App Store profile for the team.

Usage:
    python3 Tools/testflight.py --team KR9H35SQQ9 --build-number 2
    python3 Tools/testflight.py --team KR9H35SQQ9 --build-number 2 --skip-upload
"""

from __future__ import annotations

import argparse
import os
import plistlib
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXPORT = ROOT / "Builds/iOSRelease"
ARCHIVE = ROOT / "Builds/BodyOracle.xcarchive"
EXPORTED = ROOT / "Builds/iOSRelease-export"
RESULTS = ROOT / "Logs/testflight"


def run(command: list[str], log: Path, env: dict[str, str] | None = None) -> None:
    """Run a build step, streaming its output to a log file."""
    print("Running:", " ".join(command), flush=True)
    log.parent.mkdir(parents=True, exist_ok=True)
    merged = {**os.environ, **(env or {})}
    with log.open("w") as stream:
        subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, check=True, env=merged)


def write_export_options(team: str, upload: bool) -> Path:
    """Write the export options plist; `upload` sends the build straight to App Store Connect."""
    options = {
        "method": "app-store-connect",
        "teamID": team,
        "signingStyle": "automatic",
        "uploadSymbols": True,
        "manageAppVersionAndBuildNumber": False,
        "destination": "upload" if upload else "export",
    }
    path = RESULTS / "ExportOptions.plist"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(plistlib.dumps(options))
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--team", required=True, help="Apple developer team id")
    parser.add_argument("--build-number", required=True, help="Build number; must be new for this version")
    parser.add_argument("--skip-build", action="store_true", help="Reuse the existing Unity export")
    parser.add_argument("--skip-upload", action="store_true", help="Stop after producing a signed ipa")
    args = parser.parse_args()

    RESULTS.mkdir(parents=True, exist_ok=True)

    if not args.skip_build:
        # `unity build` accepts no trailing arguments, so the build number travels in the environment.
        run(["unity", "build", ".", "--target", "iOS",
             "--execute-method", "BodyOracleBuild.IOSRelease",
             "--output-path", str(EXPORT), "--allow-dirty-build",
             "--log-file", str(RESULTS / "unity-export.log")], RESULTS / "unity-cli.log",
            env={"BODYORACLE_BUILD_NUMBER": args.build_number})

    run(["xcodebuild", "-project", str(EXPORT / "Unity-iPhone.xcodeproj"),
         "-scheme", "Unity-iPhone", "-configuration", "Release", "-sdk", "iphoneos",
         "-archivePath", str(ARCHIVE), "-allowProvisioningUpdates",
         "CODE_SIGN_STYLE=Automatic", f"DEVELOPMENT_TEAM={args.team}",
         "archive"], RESULTS / "xcode-archive.log")

    options = write_export_options(args.team, not args.skip_upload)
    run(["xcodebuild", "-exportArchive", "-archivePath", str(ARCHIVE),
         "-exportPath", str(EXPORTED), "-exportOptionsPlist", str(options),
         "-allowProvisioningUpdates"], RESULTS / "xcode-export.log")

    print("PASS:", "uploaded to App Store Connect" if not args.skip_upload else f"signed ipa in {EXPORTED}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
