"""Export, archive, sign and upload the Body Oracle iOS app to TestFlight.

App Store Connect rejects development builds, so this drives the release entry point
(`BodyOracleBuild.IOSRelease`) rather than the one the device tests use, then archives with
xcodebuild, exports a signed ipa locally and only then uploads it. Signing and upload use the
Apple account configured in Xcode; `-allowProvisioningUpdates` lets Xcode fetch or create the
distribution certificate and App Store profile for the team.

Without an Xcode account session, pass an App Store Connect API key instead (flags or env
ASC_KEY_ID / ASC_ISSUER_ID / ASC_KEY_PATH; the key defaults to
~/.appstoreconnect/private_keys/AuthKey_<id>.p8). Key material stays outside the repo.

Usage:
    python3 Tools/testflight.py --team KR9H35SQQ9 --build-number 5
    python3 Tools/testflight.py --team KR9H35SQQ9 --build-number 5 --skip-upload
    python3 Tools/testflight.py --team KR9H35SQQ9 --build-number 5 --skip-build --skip-archive \\
        --api-key-id KEYID --api-issuer ISSUER
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
BUNDLE_ID = "org.realitydeslab.body-oracle"


def run(command: list[str], log: Path, env: dict[str, str] | None = None) -> None:
    """Run a build step, streaming its output to a log file."""
    print("Running:", " ".join(command), flush=True)
    log.parent.mkdir(parents=True, exist_ok=True)
    merged = {**os.environ, **(env or {})}
    with log.open("w") as stream:
        result = subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, env=merged)
    if result.returncode != 0:
        raise SystemExit(f"FAIL ({result.returncode}): see {log}")


def write_export_options(team: str, upload: bool, profile: str | None = None) -> Path:
    """Write the export options plist; `upload` sends the build straight to App Store Connect.

    With `profile` (an App Store provisioning profile name) the export signs manually with the
    keychain's Apple Distribution identity; this is needed when the API key lacks cloud-managed
    signing permission."""
    signing = ({"signingStyle": "manual", "signingCertificate": "Apple Distribution",
                "provisioningProfiles": {BUNDLE_ID: profile}} if profile else {"signingStyle": "automatic"})
    options = {
        "method": "app-store-connect",
        "teamID": team,
        **signing,
        "uploadSymbols": True,
        "manageAppVersionAndBuildNumber": False,
        "destination": "upload" if upload else "export",
    }
    path = RESULTS / ("ExportOptions-upload.plist" if upload else "ExportOptions-export.plist")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(plistlib.dumps(options))
    return path


def auth_args(key_id: str | None, issuer: str | None, key_path: str | None) -> list[str]:
    """xcodebuild flags for App Store Connect API-key auth, or none to use the Xcode account."""
    if not key_id:
        return []
    if not issuer:
        raise SystemExit("--api-issuer (or ASC_ISSUER_ID) is required with --api-key-id")
    path = Path(key_path or f"~/.appstoreconnect/private_keys/AuthKey_{key_id}.p8").expanduser()
    if not path.is_file():
        raise SystemExit(f"API key file not found: {path}")
    return ["-authenticationKeyPath", str(path), "-authenticationKeyID", key_id,
            "-authenticationKeyIssuerID", issuer]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--team", required=True, help="Apple developer team id")
    parser.add_argument("--build-number", required=True, help="Build number; must be new for this version")
    parser.add_argument("--skip-build", action="store_true", help="Reuse the existing Unity export")
    parser.add_argument("--skip-archive", action="store_true", help="Reuse the existing xcarchive")
    parser.add_argument("--skip-upload", action="store_true", help="Stop after exporting a signed ipa locally")
    parser.add_argument("--api-key-id", default=os.environ.get("ASC_KEY_ID"), help="App Store Connect API key id")
    parser.add_argument("--api-issuer", default=os.environ.get("ASC_ISSUER_ID"), help="App Store Connect issuer id")
    parser.add_argument("--api-key-path", default=os.environ.get("ASC_KEY_PATH"), help="Path to AuthKey_<id>.p8")
    parser.add_argument("--manual-profile", help="App Store profile name for manual export signing")
    args = parser.parse_args()
    auth = auth_args(args.api_key_id, args.api_issuer, args.api_key_path)

    RESULTS.mkdir(parents=True, exist_ok=True)

    if not args.skip_build:
        # `unity build` accepts no trailing arguments, so the build number travels in the environment.
        run(["unity", "build", ".", "--target", "iOS",
             "--execute-method", "BodyOracleBuild.IOSRelease",
             "--output-path", str(EXPORT), "--allow-dirty-build", "--no-tail",
             "--log-file", str(RESULTS / "unity-export.log")], RESULTS / "unity-cli.log",
            env={"BODYORACLE_BUILD_NUMBER": args.build_number})

    if not args.skip_archive:
        run(["xcodebuild", "-project", str(EXPORT / "Unity-iPhone.xcodeproj"),
             "-scheme", "Unity-iPhone", "-configuration", "Release", "-destination", "generic/platform=iOS",
             "-archivePath", str(ARCHIVE), "-allowProvisioningUpdates", *auth,
             "CODE_SIGN_STYLE=Automatic", f"DEVELOPMENT_TEAM={args.team}",
             f"CURRENT_PROJECT_VERSION={args.build_number}",
             "archive"], RESULTS / "xcode-archive.log")

    # Always export locally first: it signs for distribution and surfaces signing problems
    # before anything reaches App Store Connect.
    run(["xcodebuild", "-exportArchive", "-archivePath", str(ARCHIVE),
         "-exportPath", str(EXPORTED), "-exportOptionsPlist", str(write_export_options(args.team, False, args.manual_profile)),
         "-allowProvisioningUpdates", *auth], RESULTS / "xcode-export.log")

    if not args.skip_upload:
        run(["xcodebuild", "-exportArchive", "-archivePath", str(ARCHIVE),
             "-exportPath", str(EXPORTED), "-exportOptionsPlist", str(write_export_options(args.team, True, args.manual_profile)),
             "-allowProvisioningUpdates", *auth], RESULTS / "xcode-upload.log")

    print("PASS:", "uploaded to App Store Connect" if not args.skip_upload else f"signed ipa in {EXPORTED}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
