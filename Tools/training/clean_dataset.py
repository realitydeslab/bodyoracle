"""Quarantine Body Oracle pose samples whose label rows have the wrong column count.

The offline albumentations augmentation in `data_augmentation.ipynb` drops keypoints that fall
outside the frame after rotation, so some `*_new` labels carry fewer than `5 + 17 * 3 = 56`
columns and the surviving keypoints are shifted out of their COCO index slots. Ultralytics
rejects such rows, and their keypoint semantics cannot be recovered, so the affected
image/label pairs are moved aside instead of being trained on.

Usage:
    Training/.venv/bin/python Tools/training/clean_dataset.py Training/pose_dataset3-1
"""

from __future__ import annotations

import argparse
import logging
import shutil
import sys
from pathlib import Path
from typing import List, Optional

logger = logging.getLogger(__name__)

EXPECTED_COLUMNS = 5 + 17 * 3
IMAGE_SUFFIXES = (".jpg", ".jpeg", ".png", ".bmp", ".webp")


def find_image(images_root: Path, split: str, stem: str) -> Optional[Path]:
    """Locate the image that belongs to a label stem."""
    for suffix in IMAGE_SUFFIXES:
        candidate = images_root / split / f"{stem}{suffix}"
        if candidate.exists():
            return candidate
    return None


def malformed(label_path: Path) -> bool:
    """True when any annotation row has an unexpected column count."""
    rows = [row for row in label_path.read_text().strip().split("\n") if row.strip()]
    return any(len(row.split()) != EXPECTED_COLUMNS for row in rows)


def quarantine(dataset_root: Path, dry_run: bool) -> List[Path]:
    """Move malformed image/label pairs into `<dataset_root>/_quarantine/`."""
    labels_root = dataset_root / "labels"
    images_root = dataset_root / "images"
    quarantine_root = dataset_root / "_quarantine"
    moved: List[Path] = []

    for label_path in sorted(labels_root.rglob("*.txt")):
        if not malformed(label_path):
            continue
        split = label_path.parent.name
        image_path = find_image(images_root, split, label_path.stem)
        moved.append(label_path)
        logger.info("Quarantining %s (%s)", label_path.name, split)
        if dry_run:
            continue
        for source, kind in ((label_path, "labels"), (image_path, "images")):
            if source is None:
                logger.warning("No image found for %s", label_path.name)
                continue
            target_dir = quarantine_root / kind / split
            target_dir.mkdir(parents=True, exist_ok=True)
            shutil.move(str(source), str(target_dir / source.name))

    return moved


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset_root", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(message)s")
    moved = quarantine(args.dataset_root, args.dry_run)
    logger.info("%s %d malformed sample(s)", "Would quarantine" if args.dry_run else "Quarantined", len(moved))
    return 0


if __name__ == "__main__":
    sys.exit(main())
