#!/usr/bin/env python3
"""Build via Unity CLI, then install and test video replay in Apple's iOS Simulator."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
RESULTS = ROOT / "Logs/ios-simulator"
BUNDLE = "org.realitydeslab.body-oracle.simulator"


def run(command, log=None, env=None):
    print("Running:", " ".join(map(str, command)), flush=True)
    if log:
        with Path(log).open("w") as output:
            subprocess.run(command, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT, check=True, env=env)
    else:
        subprocess.run(command, cwd=ROOT, check=True, env=env)


def output(command):
    return subprocess.check_output(command, cwd=ROOT, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--video-dir", type=Path, default=ROOT / "Logs/simulator-inputs")
    parser.add_argument("--device", help="simctl device UUID; defaults to an available iPhone")
    parser.add_argument("--skip-build", action="store_true", help="Use the existing simulator app")
    args = parser.parse_args()
    RESULTS.mkdir(parents=True, exist_ok=True)
    if not args.skip_build:
        inputs = ROOT / "Logs/simulator-inputs"
        inputs.mkdir(parents=True, exist_ok=True)
        for name in ("pose_1.mp4", "pose_squats.mp4"):
            src = args.video_dir.resolve() / name
            if not src.is_file():
                raise FileNotFoundError(f"Missing {src}. Run Tools/fetch-test-videos.sh first.")
            if src != inputs / name:
                shutil.copy2(src, inputs / name)
        unity_log = RESULTS / "unity-export.log"
        run(["unity", "build", ".", "--target", "iOS", "--execute-method", "BodyOracleSimulatorBuild.Build",
             "--output-path", "Builds/iOSSimulator", "--log-file", str(unity_log)], RESULTS / "unity-cli.log")
        if "BODYORACLE_SIMULATOR_BUILD_PASS" not in unity_log.read_text():
            raise RuntimeError(f"Unity did not confirm a successful export. See {unity_log}")
        run(["xcodebuild", "-project", "Builds/iOSSimulator/Unity-iPhone.xcodeproj", "-scheme", "Unity-iPhone",
             "-configuration", "Debug", "-sdk", "iphonesimulator", "-destination", "generic/platform=iOS Simulator",
             "-derivedDataPath", "Builds/iOSSimulatorDerived", "CODE_SIGNING_ALLOWED=NO", "ARCHS=arm64",
             "ONLY_ACTIVE_ARCH=YES", "build"], RESULTS / "xcode-build.log")
    devices = json.loads(output(["xcrun", "simctl", "list", "devices", "available", "-j"]))["devices"]
    candidates = [d for runtime, group in devices.items() if ".iOS-" in runtime for d in group if d["name"].startswith("iPhone")]
    if not candidates:
        raise RuntimeError("Install an iOS Simulator runtime in Xcode first.")
    candidates.sort(key=lambda d: d["state"] != "Booted")
    device = args.device or candidates[0]["udid"]
    state = next(d["state"] for group in devices.values() for d in group if d["udid"] == device)
    if state != "Booted":
        run(["xcrun", "simctl", "boot", device])
    run(["xcrun", "simctl", "bootstatus", device, "-b"], RESULTS / "simulator-boot.log")
    products = ROOT / "Builds/iOSSimulatorDerived/Build/Products/Debug-iphonesimulator"
    apps = list(products.glob("*.app"))
    if len(apps) != 1:
        raise RuntimeError(f"Expected one built app in {products}, found {apps}")
    run(["xcrun", "simctl", "install", device, str(apps[0])])
    container = Path(output(["xcrun", "simctl", "get_app_container", device, BUNDLE, "data"]))
    report_path = container / "Documents/simulator-replay-report.json"
    # Preserve a previous run; a stale success must never count as the new result.
    if report_path.exists():
        shutil.move(str(report_path), str(RESULTS / "previous-replay-report.json"))
    for name in ("app-stdout.log", "app-stderr.log"):
        (RESULTS / name).write_text("")
    run_id = str(uuid.uuid4())
    launch_env = dict(os.environ, SIMCTL_CHILD_BODYORACLE_AUTOTEST="1", SIMCTL_CHILD_BODYORACLE_RUN_ID=run_id)
    simulator_app = Path(output(["xcode-select", "-p"])) / "Applications/Simulator.app"
    if simulator_app.exists():
        run(["open", "-a", str(simulator_app), "--args", "-CurrentDeviceUDID", device])
    else:
        print("Standalone Simulator UI is unavailable; running via CoreSimulator.", flush=True)
    with (RESULTS / "recording.log").open("w") as record_log:
        recorder = subprocess.Popen(["xcrun", "simctl", "io", device, "recordVideo", "--codec=h264", "--force",
                                     str(RESULTS / "replay.mp4")], stdout=record_log, stderr=subprocess.STDOUT)
        try:
            run(["xcrun", "simctl", "launch", "--terminate-running-process",
                 "--stdout=" + str(RESULTS / "app-stdout.log"), "--stderr=" + str(RESULTS / "app-stderr.log"),
                 device, BUNDLE, "-bodyOracleAutoTest"], env=launch_env)
            deadline = time.monotonic() + 240
            report = None
            previous = None
            while time.monotonic() < deadline:
                if report_path.exists():
                    try:
                        report = json.loads(report_path.read_text())
                    except json.JSONDecodeError:
                        time.sleep(1)
                        continue
                    progress = [(Path(c["source"]).name, c["processedFrames"], c["audioStarted"]) for c in report["clips"]]
                    if progress != previous:
                        print("Replay progress:", progress, flush=True)
                        previous = progress
                    if report.get("runId") == run_id and report["complete"]:
                        break
                time.sleep(2)
            if report_path.exists():
                shutil.copy2(report_path, RESULTS / "report.json")
            for screenshot in (container / "Documents").glob("*.png"):
                if screenshot.name.startswith("replay-") or screenshot.name == "audio-selection.png":
                    shutil.copy2(screenshot, RESULTS / screenshot.name)
            run(["xcrun", "simctl", "io", device, "screenshot", str(RESULTS / "final-screen.png")])
            if not report or report.get("runId") != run_id or not report.get("complete") or not report.get("passed"):
                raise RuntimeError(f"Simulator replay did not pass. Inspect {RESULTS}")
            print("PASS:", RESULTS / "report.json", flush=True)
        finally:
            recorder.send_signal(signal.SIGINT)
            try:
                recorder.wait(timeout=15)
            except subprocess.TimeoutExpired:
                recorder.kill()
                recorder.wait()


if __name__ == "__main__":
    main()
