# Task Plan: Retrain Body Oracle pose model on YOLO26

## Goal
Replace the bundled `yolov8n-pose`-derived `3-1-epoch120-last.onnx` (20 classes, 17 keypoints) with a
`yolo26n-pose` model trained on the same `pose_dataset3-1` data, exported so the ONNX output layout stays
`[1, 75, 8400]` and the Unity GPU decoder needs zero code changes.

## Fixed decisions (user, 2026-09-22)
- Train locally on this Mac (Apple M1 Max, MPS), not Colab.
- Scale: `yolo26n-pose` (same size class as current model, iPhone-friendly).
- Export head: default one-to-many -> `[1, 4 + 20 + 51, 8400]` = `[1, 75, 8400]`. No NMS-free export in this pass.

## Phases
- [x] P0-1: Mirror dataset `pose_dataset3-1` from the shared Drive folder to `Training/pose_dataset3-1`
- [x] P0-2: Python env via uv + ultralytics 8.4.159 (torch 2.14.0, MPS available)
- [x] P0-3: Train `yolo26n-pose`, 20 classes, imgsz 640, seed 42 (120 epochs, 2.53 h on MPS)
- [x] P0-4: Validate (val + test split), record metrics
- [x] P0-5: Export ONNX, output shape asserted `[1, 75, 8400]`
- [x] P1-1: Head-to-head class accuracy + mAP vs old ONNX on val/test (onnxruntime)
- [ ] P1-2: Integrate into Unity (`Assets/Models/`), run GPU validation + pose-video replay
- [ ] P2-1: Refresh README model description / training provenance

## Key facts (verified)
- Dataset: 813 train / 232 valid / 117 test images; `kpt_shape: [17, 3]`; `flip_idx` present; classes `pose1..pose20`.
- Original pipeline: `yolo11s-pose` pseudo-labelling -> albumentations offline aug -> `yolo train task=pose model=yolov8n-pose.pt epochs=50`.
- ultralytics 8.4.159 on PyPI; `yolo26n-pose.pt` / `yolo26s-pose.pt` published in ultralytics/assets.
- YOLO26 ONNX default export = raw one-to-many head, pose shape `(N, nc+4+51, 8400)`; `nms=False` switches to one-to-one `(N, 300, 57)`.

## Known issues in the original data pipeline
- `data_augmentation.ipynb` applies `A.HorizontalFlip` to keypoints without swapping left/right keypoint
  indices. Any pre-augmented images baked into the dataset carry mirrored-limb labels.
  YOLO26 training does not need offline augmentation: `fliplr` + `flip_idx` in `data.yaml` handles it correctly.

## Decisions Made
- Keep the old ONNX files in `Assets/Models/` (non-destructive); add the new model alongside.

## Errors Encountered
- Drive MCP `search_files` cannot enumerate the link-shared folder (not "shared with me"); mirrored via
  `embeddedfolderview` listing + public file download instead (`Tools/training/mirror_drive_dataset.py`).

## Verified compatibility
- Shipped ONNX metadata: ultralytics 8.3.221, `yolov8n-pose`, `pose_dataset3-1/data.yaml`, opset 22,
  `dynamic=True`, exported 2025-10-24. Same dataset as this retrain, so metrics are comparable.
- `GpuYoloPostprocessor.Process` only requires `rank==3`, `shape[0]==1`, `shape[1] >= 24`; the compute
  shader reads channels 0..23 with the candidate count as stride. YOLO26's default one-to-many export
  `[1, 75, 8400]` is therefore a drop-in.
- ultralytics 8.4.159 `exporter.py`: `model.end2end = self.args.nms is False`. Leaving `nms` unset (None)
  keeps the one-to-many head. Do NOT pass `nms=False` (that is what 8.3 used for "no embedded NMS").

## Status
**Phase P1-2 blocked on a decision** - YOLO26n did NOT beat the shipped model on the metrics the
application uses, so nothing in `Assets/Models/` or the scenes has been touched.

## Final result (2026-09-22)

Run: `runs/pose/Training/runs/yolo26n-pose/` (120 epochs, 2.53 h, MPS, seed 42, batch 16, imgsz 640).
Converged: val mAP50(B) flat from ~epoch 85, best epoch 107.

Same-pipeline ONNX validation (ultralytics `val`, CPU, identical splits):

| model | split | mAP50(B) | mAP50-95(B) | mAP50(P) | mAP50-95(P) |
|---|---|---:|---:|---:|---:|
| old yolov8n best | val | 0.9688 | 0.9179 | 0.7121 | 0.6516 |
| old yolov8n last | val | 0.9610 | 0.9099 | 0.6909 | 0.6437 |
| yolo26n best | val | 0.9463 | 0.8738 | **0.7208** | **0.6854** |
| old yolov8n best | test | 0.9260 | 0.8646 | 0.6287 | 0.5949 |
| old yolov8n last | test | 0.9345 | 0.8755 | 0.6300 | 0.5687 |
| yolo26n best | test | 0.9204 | 0.8363 | **0.6871** | **0.6588** |

Top-1 class accuracy of the highest-scoring candidate (what the app consumes):

| model | test (n=109) | valid (n=218) | mean best score (test) |
|---|---:|---:|---:|
| old yolov8n best | **0.862** | **0.931** | 0.880 |
| old yolov8n last | 0.853 | 0.927 | 0.897 |
| yolo26n best | 0.844 | 0.913 | 0.848 |

McNemar (paired, old-best vs yolo26n-best): test p=0.727 (5 vs 3), valid p=0.454 (10 vs 6).
=> The box/class gap is not statistically significant; the keypoint gain is consistent across splits.

## Interpretation
- YOLO26's win is in keypoints (RLE head): +3-6 pt mAP50-95(P). The app renders no keypoints.
- On box + class, which the app does use, YOLO26n is level-to-slightly-behind on ~1k images.
- Confounds: the new run trained on 765 images vs the old model's 813 (70 malformed samples removed);
  the old checkpoint was also selected against this same valid split.
- Likely leakage in both directions: offline `*_new` augmented copies of a source photo can sit in
  `train` while the original sits in `test`, which inflates both models' numbers equally.

## Option 4 executed (2026-09-22): split forensics + leakage-free re-measurement

### Leakage in the original split
Grouping every file by its source photo id (`IMG_<number>`): **508 source groups, 235 of which
spanned more than one split**. Of the 349 old val+test images, **269 (77%) share a source photo with
the old training set**. `IMG_1444 Medium` sits in `test` while `IMG_1444 Medium_new` sits in `train`.

### Re-measurement on the leakage-free subset (n=74, no source photo seen in training by either model)
| model | top1 (all) | top1 excluding classes 0/1 (n=65) | errors |
|---|---:|---:|---|
| old yolov8n best | 0.946 | **0.985** | 1->0 x2, 0->1 x1, 8->11 x1 |
| old yolov8n last | 0.932 | 0.969 | 1->0 x2, 8->11 x2, 0->1 x1 |
| yolo26n best | 0.905 | 0.969 | 0->1 x3, 1->0 x2, 5->7 x1, 8->11 x1 |

McNemar on the clean subset: old-only-correct=3, new-only-correct=0, p=0.250 (still not significant).

### Root cause of essentially all remaining error: a chirality-sensitive class pair
Mirror-matching every image against every other image (32x32 rms < 0.02) finds 223 mirrored pairs:
- **class 0 (pose1) <-> class 1 (pose2): 70 mirrored pairs labelled as DIFFERENT classes.**
- every other class maps to *itself* under mirroring (the dataset contains same-class flipped copies).

Both training runs used ultralytics' default **`fliplr: 0.5`** (confirmed in
`runs/.../yolo26n-pose/args.yaml` and `cfg/default.yaml`). Horizontal flip keeps the class label, so
half of the class-0 samples are shown as class-1-looking images still labelled class 0, and vice
versa. The confusion tables match exactly: 12 of 15-17 test errors are 0<->1 for every model tested.
Excluding that pair, every model is at 0.97-0.99.

### New split produced
`Training/pose_dataset3-1-grouped/` (symlinks, `data.yaml` included), built by
`Tools/training/regroup_split.py`: 757 train / 225 valid / 110 test images, 508 groups,
**0 groups spanning splits**, all 20 classes present in every split.

## Open options (need user decision)
1. **Retrain with `fliplr=0.0` on the grouped split** - the only change with a large expected effect,
   since 0<->1 accounts for ~80% of all errors. Applies equally to yolov8n and yolo26n.
2. Keep the shipped yolov8n model; archive the YOLO26 run as evidence. No Unity change.
3. Repair the 70 malformed labels to recover 6% of the data.
4. Train `yolo26s-pose` (~3x params).

## Baseline measured (2026-09-22, cleaned 109-image test split)
`Tools/training/compare_models.py`, top-1 class of the highest-scoring candidate:
- `Assets/Models/3-1-epoch120-last.onnx`: **0.853** (mean best score 0.897)
- `Assets/Models/3-1-epoch120-best.onnx`: **0.862** (mean best score 0.880)

## Dataset cleaning result
70 of 1,162 samples (6%) quarantined to `Training/pose_dataset3-1/_quarantine/` for malformed
keypoint columns (44/47/50/53 instead of 56), all from the offline `*_new` augmentation.
Training set after cleaning: 765 train / 218 valid / 109 test; ultralytics reports 0 corrupt labels.
