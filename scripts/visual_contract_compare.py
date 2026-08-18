#!/usr/bin/env python3
"""Build repeatable Paint/Output visual-contract comparison artifacts.

The approved reference is a vertical composite: Paint occupies the upper
482 pixels and Output occupies the remainder. Fixed chrome bands retain their
native pixel height while the flexible canvas grows to the captured viewport.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageChops, ImageEnhance, ImageStat


REFERENCE_WIDTH = 1672
PAINT_SPLIT_Y = 482
PAINT_CONTROLS_BOTTOM = 141
PAINT_TRANSPORT_TOP = 423
OUTPUT_TABS_BOTTOM = 87
OUTPUT_CANVAS_TOP = 99
OUTPUT_CANVAS_BOTTOM = 335
OUTPUT_SETTINGS_LEFT = 1154


def normalize_paint(reference: Image.Image, size: tuple[int, int]) -> Image.Image:
    width, height = size
    source = reference.crop((0, 0, reference.width, PAINT_SPLIT_Y)).resize(
        (width, PAINT_SPLIT_Y), Image.Resampling.LANCZOS
    )
    transport_height = PAINT_SPLIT_Y - PAINT_TRANSPORT_TOP
    canvas_bottom = height - transport_height
    output = Image.new("RGB", size, source.getpixel((0, 200)))
    output.paste(source.crop((0, 0, width, PAINT_CONTROLS_BOTTOM)), (0, 0))

    left_width = round(width * OUTPUT_SETTINGS_LEFT / REFERENCE_WIDTH)
    canvas = source.crop((0, PAINT_CONTROLS_BOTTOM, left_width, PAINT_TRANSPORT_TOP))
    canvas = canvas.resize((left_width, canvas_bottom - PAINT_CONTROLS_BOTTOM), Image.Resampling.BICUBIC)
    output.paste(canvas, (0, PAINT_CONTROLS_BOTTOM))

    inspector = source.crop((left_width, OUTPUT_TABS_BOTTOM, width, PAINT_TRANSPORT_TOP))
    output.paste(inspector, (left_width, OUTPUT_TABS_BOTTOM))
    fill = Image.new("RGB", (width - left_width, max(0, canvas_bottom - OUTPUT_TABS_BOTTOM)), inspector.getpixel((1, 1)))
    output.paste(fill, (left_width, OUTPUT_TABS_BOTTOM))
    output.paste(inspector, (left_width, OUTPUT_TABS_BOTTOM))

    transport = source.crop((0, PAINT_TRANSPORT_TOP, width, PAINT_SPLIT_Y))
    output.paste(transport, (0, canvas_bottom))
    return output


def normalize_output(reference: Image.Image, size: tuple[int, int]) -> Image.Image:
    width, height = size
    source = reference.crop((0, PAINT_SPLIT_Y, reference.width, reference.height)).resize(
        (width, reference.height - PAINT_SPLIT_Y), Image.Resampling.LANCZOS
    )
    output = Image.new("RGB", size, source.getpixel((0, 120)))
    output.paste(source.crop((0, 0, width, OUTPUT_CANVAS_TOP)), (0, 0))

    settings_left = round(width * OUTPUT_SETTINGS_LEFT / REFERENCE_WIDTH)
    bottom_slice_height = source.height - OUTPUT_CANVAS_BOTTOM
    canvas_bottom = height - bottom_slice_height
    canvas = source.crop((0, OUTPUT_CANVAS_TOP, settings_left, OUTPUT_CANVAS_BOTTOM))
    canvas = canvas.resize((settings_left, canvas_bottom - OUTPUT_CANVAS_TOP), Image.Resampling.BICUBIC)
    output.paste(canvas, (0, OUTPUT_CANVAS_TOP))
    output.paste(source.crop((0, OUTPUT_CANVAS_BOTTOM, settings_left, source.height)), (0, canvas_bottom))

    settings = source.crop((settings_left, OUTPUT_TABS_BOTTOM, width, source.height))
    output.paste(settings, (settings_left, OUTPUT_TABS_BOTTOM))
    return output


def compare(reference: Image.Image, current_path: Path, stem: str, output_dir: Path) -> dict[str, float | str]:
    current = Image.open(current_path).convert("RGB")
    normalized = normalize_paint(reference, current.size) if stem == "paint" else normalize_output(reference, current.size)
    difference = ImageChops.difference(normalized, current)
    stat = ImageStat.Stat(difference)
    mean_absolute_error = sum(stat.mean) / (len(stat.mean) * 255)
    changed_fraction = sum(1 for value in difference.convert("L").getdata() if value > 12) / (current.width * current.height)

    normalized.save(output_dir / f"{stem}-reference-normalized.png")
    current.save(output_dir / f"{stem}-current.png")
    Image.blend(normalized, current, 0.5).save(output_dir / f"{stem}-overlay-50.png")
    ImageEnhance.Contrast(difference).enhance(2.5).save(output_dir / f"{stem}-diff-enhanced.png")
    return {
        "current": str(current_path),
        "mean_absolute_error": round(mean_absolute_error, 6),
        "changed_fraction_over_12": round(changed_fraction, 6),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--paint", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    args.out.mkdir(parents=True, exist_ok=True)
    reference = Image.open(args.reference).convert("RGB")
    if reference.width != REFERENCE_WIDTH or reference.height < PAINT_SPLIT_Y + 1:
        raise ValueError(f"Unexpected reference size: {reference.size}")

    report = {
        "reference": str(args.reference),
        "paint": compare(reference, args.paint, "paint", args.out),
        "output": compare(reference, args.output, "output", args.out),
    }
    (args.out / "visual-contract-report.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(json.dumps(report, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
