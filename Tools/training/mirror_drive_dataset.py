"""Mirror a link-shared Google Drive dataset folder to a local directory.

The Body Oracle pose dataset lives in a public Drive folder. This script walks the folder
tree through Drive's `embeddedfolderview` listing endpoint and downloads every file, so the
training set can be rebuilt without a Drive client or API key.

Usage:
    python3 Tools/training/mirror_drive_dataset.py FOLDER_ID OUTPUT_DIR [--workers 8]
"""

from __future__ import annotations

import argparse
import logging
import re
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import List, Tuple

logger = logging.getLogger(__name__)

LIST_URL = "https://drive.google.com/embeddedfolderview?id={folder_id}#list"
FILE_URL = "https://drive.google.com/uc?export=download&id={file_id}"
ENTRY_RE = re.compile(
    r'flip-entry"\s+id="entry-([^"]+)".*?flip-entry-title">([^<]+)</div>',
    re.DOTALL,
)
USER_AGENT = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/131.0 Safari/537.36"
FILE_SUFFIXES = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".txt", ".yaml", ".yml", ".json", ".cache"}


def _fetch(url: str, attempts: int = 5) -> bytes:
    """GET a URL with linear backoff on transient Drive errors."""
    last_error: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.read()
        except (urllib.error.URLError, TimeoutError) as error:
            last_error = error
            time.sleep(2 * attempt)
    raise RuntimeError(f"failed to fetch {url}: {last_error}")


def list_folder(folder_id: str) -> List[Tuple[str, str]]:
    """Return (entry_id, entry_name) pairs for one Drive folder."""
    html = _fetch(LIST_URL.format(folder_id=folder_id)).decode("utf-8", "replace")
    entries = ENTRY_RE.findall(html)
    return [(entry_id, name.strip()) for entry_id, name in entries]


def _is_file(name: str) -> bool:
    return Path(name).suffix.lower() in FILE_SUFFIXES


def collect(folder_id: str, destination: Path) -> List[Tuple[str, Path]]:
    """Walk the folder tree and return download jobs, creating local directories."""
    jobs: List[Tuple[str, Path]] = []
    destination.mkdir(parents=True, exist_ok=True)
    for entry_id, name in list_folder(folder_id):
        if name.startswith("."):
            continue
        if _is_file(name):
            jobs.append((entry_id, destination / name))
        else:
            logger.info("Descending into %s", destination / name)
            jobs.extend(collect(entry_id, destination / name))
    return jobs


def download(job: Tuple[str, Path]) -> bool:
    """Download one file unless it already exists locally."""
    file_id, path = job
    if path.exists() and path.stat().st_size > 0:
        return False
    payload = _fetch(FILE_URL.format(file_id=file_id))
    path.write_bytes(payload)
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder_id", help="Drive folder id of the dataset root")
    parser.add_argument("output_dir", type=Path, help="Local directory to mirror into")
    parser.add_argument("--workers", type=int, default=8, help="Parallel downloads")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format="%(message)s")

    logger.info("Listing %s ...", args.folder_id)
    jobs = collect(args.folder_id, args.output_dir)
    logger.info("Found %d files", len(jobs))

    downloaded = 0
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for index, was_new in enumerate(pool.map(download, jobs), start=1):
            downloaded += int(was_new)
            if index % 100 == 0 or index == len(jobs):
                logger.info("  %d/%d processed (%d newly downloaded)", index, len(jobs), downloaded)

    logger.info("Mirrored %d files into %s", len(jobs), args.output_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
