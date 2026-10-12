#!/usr/bin/env python3
"""Unity CLI export, Xcode build, install and validate replay or camera on an iPhone."""
import argparse
import json
import os
import re
from pathlib import Path
import signal
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]


def require_available_device(device, bundle):
    # A second devicectl console session can steal foreground focus and invalidate
    # camera/video evidence. Leave unrelated app tests alone instead of fighting them.
    processes = subprocess.check_output(["ps", "-axo", "pid=,comm=,args="], text=True)
    for line in processes.splitlines():
        fields = line.split(None, 2)
        if len(fields) != 3:
            continue
        pid, executable, command = fields
        if executable.endswith("/devicectl") and "device process launch" in command and device in command and "--console" in command and bundle not in command:
            raise RuntimeError(f"Another device test (PID {pid}) is using this iPhone. Finish that test before launching Body Oracle.")


def run(command, log):
    print("Running:", " ".join(map(str, command)), flush=True)
    with log.open("w") as stream:
        subprocess.run(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", required=True, help="Paired physical iPhone UDID")
    parser.add_argument("--mode", choices=("replay", "camera"), default="replay")
    parser.add_argument("--team", default="KR9H35SQQ9", help="Apple development signing team")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--stereo", action="store_true", help="Camera mode only: run in HoloKit stereo rendering")
    parser.add_argument("--plane-distance", type=float, help="Override the characters' plane distance in metres")
    parser.add_argument("--install-only", action="store_true", help="Build and install without launching (useful while the phone is locked)")
    args = parser.parse_args()
    replay = args.mode == "replay"
    results = ROOT / ("Logs/ios-device-" + args.mode)
    results.mkdir(parents=True, exist_ok=True)
    export = "Builds/iOSDeviceReplay" if replay else "Builds/iOSDeviceValidation"
    derived = export + "Derived"
    bundle = "org.realitydeslab.body-oracle.replay" if replay else "org.realitydeslab.body-oracle"
    report_name = "simulator-replay-report.json" if replay else "device-camera-report.json"
    if not args.skip_build:
        method = "BodyOracleSimulatorBuild.BuildDeviceReplay" if replay else "BodyOracleBuild.IOSDeviceValidation"
        unity_log = results / "unity-export.log"
        run(["unity", "build", ".", "--target", "iOS", "--execute-method", method,
             "--output-path", export, "--log-file", str(unity_log)], results / "unity-cli.log")
        marker = "BODYORACLE_SIMULATOR_BUILD_PASS" if replay else "BODYORACLE_BUILD_PASS"
        if marker not in unity_log.read_text():
            raise RuntimeError(f"Unity export did not succeed: {unity_log}")
        run(["xcodebuild", "-project", export + "/Unity-iPhone.xcodeproj", "-scheme", "Unity-iPhone",
             "-configuration", "Debug", "-sdk", "iphoneos", "-destination", "id=" + args.device,
             "-derivedDataPath", derived, "-allowProvisioningUpdates", "CODE_SIGN_STYLE=Automatic",
             "DEVELOPMENT_TEAM=" + args.team, "build"], results / "xcode-build.log")
    apps = list((ROOT / derived / "Build/Products/Debug-iphoneos").glob("*.app"))
    if len(apps) != 1:
        raise RuntimeError(f"Expected one built app, found {apps}")
    if not args.install_only:
        require_available_device(args.device, bundle)
    run(["xcrun", "devicectl", "device", "install", "app", "--device", args.device, str(apps[0])], results / "install.log")
    if args.install_only:
        print("Installed:", bundle, flush=True)
        return
    run_id = str(uuid.uuid4())
    launch_env = dict(os.environ, DEVICECTL_CHILD_BODYORACLE_RUN_ID=run_id)
    launch_env["DEVICECTL_CHILD_BODYORACLE_AUTOTEST" if replay else "DEVICECTL_CHILD_BODYORACLE_CAMERA_TEST"] = "1"
    if args.stereo and not replay:
        launch_env["DEVICECTL_CHILD_BODYORACLE_STEREO"] = "1"
    if args.plane_distance:
        launch_env["DEVICECTL_CHILD_BODYORACLE_PLANE_DISTANCE"] = str(args.plane_distance)
    local_report = results / "report.json"
    incoming_report = results / "incoming-report.json"
    if local_report.exists():
        local_report.replace(results / "previous-report.json")
    recording_path = results / "replay.mp4"
    if recording_path.exists():
        recording_path.replace(results / "previous-replay.mp4")
    with (results / "app-console.log").open("w") as console:
        process = subprocess.Popen(["xcrun", "devicectl", "device", "process", "launch", "--device", args.device,
                                    "--terminate-existing", "--console", bundle, "--",
                                    "-bodyOracleAutoTest" if replay else "-bodyOracleCameraTest",
                                    "-bodyOracleRunId", run_id] + (["-bodyOracleStereo"] if args.stereo and not replay else []),
                                   stdout=console, stderr=subprocess.STDOUT, env=launch_env)
        recorder = None
        try:
            # Capture only this application's test run, after launching it.
            time.sleep(3)
            with (results / "recording.log").open("w") as recording:
                recorder = subprocess.Popen(["xcrun", "devicectl", "device", "capture", "screen-record",
                                             "--device", args.device, "--destination", str(results / "replay.mp4"),
                                             "--duration", "180"], stdout=recording, stderr=subprocess.STDOUT)
            deadline = time.monotonic() + 240
            report = None
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"Application exited before validation; inspect {results / 'app-console.log'}")
                copy = subprocess.run(["xcrun", "devicectl", "device", "copy", "from", "--device", args.device,
                                       "--domain-type", "appDataContainer", "--domain-identifier", bundle,
                                       "--source", "Documents/" + report_name, "--destination", str(incoming_report)],
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                if copy.returncode == 0:
                    try:
                        candidate = json.loads(incoming_report.read_text())
                        if candidate.get("runId") == run_id:
                            report = candidate
                            local_report.write_text(json.dumps(report, indent=2))
                            print("Progress:", report.get("processedFrames", [c["processedFrames"] for c in report.get("clips", [])]), flush=True)
                            if report.get("complete"):
                                break
                    except json.JSONDecodeError:
                        pass
                time.sleep(3)
            run(["xcrun", "devicectl", "device", "copy", "from", "--device", args.device,
                 "--domain-type", "appDataContainer", "--domain-identifier", bundle,
                 "--source", "Documents", "--destination", str(results / "Documents")], results / "copy.log")
            run(["xcrun", "devicectl", "device", "capture", "screenshot", "--device", args.device,
                 "--destination", str(results / "final-screen.png")], results / "screenshot.log")
            # Catch initialization failures emitted before the MonoBehaviour's log hook.
            console_errors = re.findall(r"^.*\b\w*Exception:.*$", (results / "app-console.log").read_text(errors="replace"), re.MULTILINE)
            if report and console_errors:
                report["startupLogErrors"] = list(dict.fromkeys(console_errors))
                report["passed"] = False
                local_report.write_text(json.dumps(report, indent=2))
            if not report or not report.get("complete") or not report.get("passed"):
                raise RuntimeError(f"Device {args.mode} test did not pass. Inspect {results}")
            print("PASS:", local_report, flush=True)
        finally:
            incoming_report.unlink(missing_ok=True)
            if recorder is not None and recorder.poll() is None:
                recorder.send_signal(signal.SIGINT)
                try:
                    recorder.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    recorder.kill()
                    recorder.wait()
            recording_saved = recording_path.is_file() and recording_path.stat().st_size > 0
            (results / "recording-status.json").write_text(json.dumps({
                "runId": run_id,
                "saved": recording_saved,
                "recorderExitCode": recorder.returncode if recorder else None,
                "log": "recording.log",
            }, indent=2))
            if not recording_saved:
                print("Screen recording unavailable; screenshots and JSON results are retained. See recording.log.", flush=True)
            # devicectl forwards SIGINT to the app; only terminate our own test process.
            if process.poll() is None:
                process.send_signal(signal.SIGINT)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


if __name__ == "__main__":
    main()
