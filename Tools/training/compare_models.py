"""Compare Body Oracle ONNX pose models by top-1 class accuracy on a labelled split.

The Unity application only consumes the box and the class id, so the practical quality
measure is: for the highest-confidence detection in an image, does the predicted class match
the label? This script runs any number of `[1, 4 + nc + 3 * kpt, 8400]` ONNX models over the
same images with identical letterbox preprocessing and reports that accuracy.

Usage:
    Training/.venv/bin/python Tools/training/compare_models.py \
        --images Training/pose_dataset3-1/images/test \
        --labels Training/pose_dataset3-1/labels/test \
        --model old=Assets/Models/3-1-epoch120-last.onnx \
        --model yolo26=Training/runs/yolo26n-pose/weights/best.onnx
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
from pathlib import Path
from typing import Dict, List, Tuple

import cv2
import numpy as np
import onnxruntime as ort

logger = logging.getLogger(__name__)

IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
NUM_CLASSES = 20
IMGSZ = 640


def letterbox(image: np.ndarray, size: int = IMGSZ) -> np.ndarray:
    """Resize with unchanged aspect ratio and grey padding, as Ultralytics does at inference."""
    height, width = image.shape[:2]
    scale = min(size / height, size / width)
    new_width, new_height = int(round(width * scale)), int(round(height * scale))
    resized = cv2.resize(image, (new_width, new_height), interpolation=cv2.INTER_LINEAR)
    canvas = np.full((size, size, 3), 114, dtype=np.uint8)
    top, left = (size - new_height) // 2, (size - new_width) // 2
    canvas[top : top + new_height, left : left + new_width] = resized
    return canvas


def preprocess(path: Path) -> np.ndarray:
    """Load an image as a normalized NCHW RGB tensor."""
    image = cv2.imread(str(path))
    if image is None:
        raise FileNotFoundError(f"cannot read image {path}")
    padded = letterbox(image)
    rgb = cv2.cvtColor(padded, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
    return np.ascontiguousarray(rgb.transpose(2, 0, 1)[None])


def read_label(path: Path) -> int:
    """Return the class id of the first annotation line."""
    line = path.read_text().strip().split("\n")[0]
    return int(float(line.split()[0]))


def best_detection(output: np.ndarray) -> Tuple[int, float]:
    """Return (class id, score) of the highest-scoring candidate in a [1, C, 8400] output."""
    scores = output[0, 4 : 4 + NUM_CLASSES, :]  # class channels, channel-major
    flat_index = int(np.argmax(scores))
    class_id, anchor = divmod(flat_index, scores.shape[1])
    return class_id, float(scores[class_id, anchor])


def evaluate(session: ort.InferenceSession, pairs: List[Tuple[Path, int]]) -> Dict[str, float]:
    """Run one model over the split and return accuracy statistics."""
    input_name = session.get_inputs()[0].name
    correct = 0
    scores: List[float] = []
    confusions: Dict[str, int] = {}
    for image_path, truth in pairs:
        output = session.run(None, {input_name: preprocess(image_path)})[0]
        predicted, score = best_detection(output)
        scores.append(score)
        if predicted == truth:
            correct += 1
        else:
            key = f"{truth}->{predicted}"
            confusions[key] = confusions.get(key, 0) + 1
    ranked = sorted(confusions.items(), key=lambda item: item[1], reverse=True)[:10]
    return {
        "images": len(pairs),
        "top1_accuracy": correct / len(pairs) if pairs else 0.0,
        "mean_best_score": float(np.mean(scores)) if scores else 0.0,
        "top_confusions": dict(ranked),
    }


def collect_pairs(images_dir: Path, labels_dir: Path) -> List[Tuple[Path, int]]:
    """Match every image with its YOLO label file."""
    pairs: List[Tuple[Path, int]] = []
    for image_path in sorted(images_dir.iterdir()):
        if image_path.suffix.lower() not in IMAGE_SUFFIXES:
            continue
        label_path = labels_dir / f"{image_path.stem}.txt"
        if not label_path.exists():
            logger.warning("No label for %s, skipping", image_path.name)
            continue
        pairs.append((image_path, read_label(label_path)))
    return pairs


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--images", type=Path, required=True)
    parser.add_argument("--labels", type=Path, required=True)
    parser.add_argument("--model", action="append", required=True, help="name=path/to/model.onnx")
    parser.add_argument("--output", type=Path, default=None, help="Optional JSON report path")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(message)s")

    pairs = collect_pairs(args.images, args.labels)
    logger.info("Evaluating %d images", len(pairs))

    report: Dict[str, Dict[str, float]] = {}
    for spec in args.model:
        name, _, path = spec.partition("=")
        session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        report[name] = evaluate(session, pairs)
        report[name]["path"] = path
        logger.info(
            "%-10s top1=%.3f  mean_score=%.3f", name, report[name]["top1_accuracy"], report[name]["mean_best_score"]
        )

    if args.output:
        args.output.write_text(json.dumps(report, indent=2))
        logger.info("Wrote %s", args.output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
