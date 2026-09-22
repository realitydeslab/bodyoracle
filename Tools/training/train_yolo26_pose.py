"""Train the Body Oracle 20-class pose model on YOLO26 and export it for Unity.

The Unity runtime decodes a channel-major tensor of shape [1, 4 + nc + 3 * kpt, 8400].
YOLO26 keeps that layout when ONNX export uses the one-to-many head, which is the default
(`nms=None`). Passing `nms=False` would switch to the NMS-free one-to-one head and change the
output to [1, 300, 57], which the current GPU postprocessor cannot read.

Usage:
    Training/.venv/bin/python Tools/training/train_yolo26_pose.py \
        --data Training/pose_dataset3-1/data.yaml --epochs 120
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import platform
import random
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Dict

import numpy as np
import torch
from ultralytics import YOLO

logger = logging.getLogger(__name__)

EXPECTED_CLASSES = 20
EXPECTED_KEYPOINTS = 17
EXPECTED_ANCHORS = 8400


@dataclass(frozen=True)
class TrainConfig:
    """Immutable training configuration."""

    data: Path
    model: str = "yolo26n-pose.pt"
    epochs: int = 120
    imgsz: int = 640
    batch: int = 16
    patience: int = 30
    seed: int = 42
    device: str = "mps"
    project: Path = Path("Training/runs")
    name: str = "yolo26n-pose"
    workers: int = 8
    # Classes 0 (pose1) and 1 (pose2) are mirror images of each other, and a horizontal flip does
    # not change the label, so the default fliplr=0.5 teaches the model to confuse exactly that
    # pair. Keep this at 0.0 for this dataset.
    fliplr: float = 0.0


def set_seed(seed: int) -> None:
    """Set random seeds for reproducibility."""
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)
    os.environ["PYTHONHASHSEED"] = str(seed)


def log_environment() -> Dict[str, Any]:
    """Record environment information for reproducibility."""
    import ultralytics

    return {
        "python_version": platform.python_version(),
        "platform": platform.platform(),
        "torch_version": torch.__version__,
        "ultralytics_version": ultralytics.__version__,
        "mps_available": torch.backends.mps.is_available(),
        "cuda_available": torch.cuda.is_available(),
    }


def train(cfg: TrainConfig) -> Path:
    """Train the pose model and return the run directory."""
    set_seed(cfg.seed)
    model = YOLO(cfg.model)
    results = model.train(
        task="pose",
        data=str(cfg.data.resolve()),
        epochs=cfg.epochs,
        imgsz=cfg.imgsz,
        batch=cfg.batch,
        device=cfg.device,
        seed=cfg.seed,
        deterministic=True,
        patience=cfg.patience,
        workers=cfg.workers,
        fliplr=cfg.fliplr,
        project=str(cfg.project),
        name=cfg.name,
        exist_ok=True,
        plots=True,
        val=True,
    )
    return Path(results.save_dir)


def validate(weights: Path, cfg: TrainConfig, split: str) -> Dict[str, float]:
    """Validate one dataset split and return the summary metrics."""
    model = YOLO(str(weights))
    metrics = model.val(
        data=str(cfg.data.resolve()),
        split=split,
        imgsz=cfg.imgsz,
        batch=cfg.batch,
        device=cfg.device,
        project=str(cfg.project),
        name=f"{cfg.name}-val-{split}",
        exist_ok=True,
        plots=False,
    )
    return {key: float(value) for key, value in metrics.results_dict.items()}


def export_onnx(weights: Path, cfg: TrainConfig) -> Path:
    """Export ONNX with the one-to-many head, matching the shipped model's layout."""
    model = YOLO(str(weights))
    # nms is deliberately left unset: nms=False would select YOLO26's end-to-end one-to-one head.
    exported = model.export(format="onnx", imgsz=cfg.imgsz, dynamic=True, simplify=False, half=False)
    return Path(exported)


def verify_onnx(onnx_path: Path, imgsz: int) -> Dict[str, Any]:
    """Run the exported graph once and assert the Unity-facing output layout."""
    import onnxruntime as ort

    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    input_meta = session.get_inputs()[0]
    dummy = np.zeros((1, 3, imgsz, imgsz), dtype=np.float32)
    outputs = session.run(None, {input_meta.name: dummy})
    shape = list(outputs[0].shape)

    expected = [1, 4 + EXPECTED_CLASSES + 3 * EXPECTED_KEYPOINTS, EXPECTED_ANCHORS]
    if shape != expected:
        raise ValueError(f"Exported ONNX output shape {shape} does not match the Unity decoder's {expected}")

    return {
        "input_name": input_meta.name,
        "input_shape": list(input_meta.shape),
        "output_name": session.get_outputs()[0].name,
        "output_shape": shape,
        "output_count": len(outputs),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=Path("Training/pose_dataset3-1/data.yaml"))
    parser.add_argument("--model", default="yolo26n-pose.pt")
    parser.add_argument("--epochs", type=int, default=120)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--device", default="mps")
    parser.add_argument("--name", default="yolo26n-pose")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--fliplr", type=float, default=0.0, help="Horizontal flip probability")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    cfg = TrainConfig(
        data=args.data,
        model=args.model,
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch,
        device=args.device,
        name=args.name,
        seed=args.seed,
        fliplr=args.fliplr,
    )
    logger.info("Environment: %s", json.dumps(log_environment(), indent=2))
    logger.info("Config: %s", json.dumps({k: str(v) for k, v in asdict(cfg).items()}, indent=2))

    try:
        run_dir = train(cfg)
    except (RuntimeError, FileNotFoundError, ValueError):
        logger.exception("Training failed")
        raise

    best = run_dir / "weights" / "best.pt"
    last = run_dir / "weights" / "last.pt"
    logger.info("Training finished, best weights at %s", best)

    summary: Dict[str, Any] = {
        "environment": log_environment(),
        "config": {key: str(value) for key, value in asdict(cfg).items()},
        "run_dir": str(run_dir),
        "metrics": {},
    }
    for split in ("val", "test"):
        try:
            summary["metrics"][split] = validate(best, cfg, split)
        except (RuntimeError, FileNotFoundError, ValueError) as error:
            logger.warning("Validation on split '%s' failed: %s", split, error)

    for tag, weights in (("best", best), ("last", last)):
        onnx_path = export_onnx(weights, cfg)
        summary[f"onnx_{tag}"] = {"path": str(onnx_path), **verify_onnx(onnx_path, cfg.imgsz)}
        logger.info("Exported and verified %s", onnx_path)

    summary_path = run_dir / "bodyoracle_summary.json"
    summary_path.write_text(json.dumps(summary, indent=2))
    logger.info("Wrote %s", summary_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
