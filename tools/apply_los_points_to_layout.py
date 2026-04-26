#!/usr/bin/env python3
"""
Generate Plugin/Core/Cs2VisibilityPrimitiveLayout.cs from tuned LOS point JSON.

Default input:
  tools/sas_blue_tools_preview_los_points.json

The canonical CS2 hitbox JSON remains the provenance source. This script applies
the manually tuned single-point layout used by the runtime.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INPUT = ROOT / "tools" / "sas_blue_tools_preview_los_points.json"
DEFAULT_OUTPUT = ROOT / "Plugin" / "Core" / "Cs2VisibilityPrimitiveLayout.cs"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--check",
        action="store_true",
        help="Verify the generated file matches the output path without writing.",
    )
    return parser.parse_args()


def require_point(value: Any, index: int) -> tuple[float, float, float]:
    if not isinstance(value, list) or len(value) != 3:
        raise SystemExit(f"Point {index} local_point must be a 3-number array.")

    try:
        return (float(value[0]), float(value[1]), float(value[2]))
    except (TypeError, ValueError) as exc:
        raise SystemExit(f"Point {index} local_point contains a non-number value.") from exc


def format_float(value: float) -> str:
    text = f"{value:.3f}".rstrip("0").rstrip(".")
    if text == "-0":
        text = "0"
    if "." not in text:
        text += ".0"
    return f"{text}f"


def validate_points(data: dict[str, Any]) -> list[dict[str, Any]]:
    points = data.get("points")
    if not isinstance(points, list):
        raise SystemExit("Input JSON must contain a points array.")

    primitive_count = int(data.get("primitive_count", len(points)))
    if primitive_count != len(points):
        raise SystemExit(f"primitive_count={primitive_count} does not match points={len(points)}.")
    if len(points) < 19:
        raise SystemExit(f"Expected at least 19 points, got {len(points)}.")

    names: set[str] = set()
    for expected_index, point in enumerate(points):
        if not isinstance(point, dict):
            raise SystemExit(f"Point {expected_index} must be an object.")

        index = int(point.get("index", -1))
        if index != expected_index:
            raise SystemExit(f"Point index mismatch at array position {expected_index}: got {index}.")

        name = str(point.get("name", "")).strip()
        if not name:
            raise SystemExit(f"Point {index} is missing name.")
        if name in names:
            raise SystemExit(f"Duplicate point name: {name}")
        names.add(name)

        point["local_point"] = require_point(point.get("local_point"), index)
        point["use_fixed_head_origin"] = index < 19 or bool(point.get("use_fixed_head_origin", False))
        required_weapon_class = str(point.get("required_weapon_class", "")).strip()
        if not required_weapon_class:
            required_weapon_class = infer_required_weapon_class(name)
        if required_weapon_class == "Awp":
            required_weapon_class = "Sniper"
        if required_weapon_class not in {"None", "Pistol", "Rifle", "Sniper"}:
            raise SystemExit(
                f"Point {index} has invalid required_weapon_class={required_weapon_class!r}."
            )
        point["required_weapon_class"] = required_weapon_class

    return points


def infer_required_weapon_class(name: str) -> str:
    normalized = name.strip().lower()
    if normalized == "pistol":
        return "Pistol"
    if normalized == "rifle":
        return "Rifle"
    if normalized in {"awp", "sniper"}:
        return "Sniper"
    return "None"


def generate_layout(points: list[dict[str, Any]]) -> str:
    lines: list[str] = [
        "using System.Numerics;",
        "using S2FOW.Models;",
        "",
        "namespace S2FOW.Core;",
        "",
        "internal readonly struct VisibilityPrimitive",
        "{",
        "    public required Vector3 LocalPoint { get; init; }",
        "    public bool UseFixedHeadOrigin { get; init; }",
        "    public WeaponLosClass RequiredWeaponClass { get; init; }",
        "}",
        "",
        "internal static class Cs2VisibilityPrimitiveLayout",
        "{",
        f"    public const int PrimitiveCount = {len(points)};",
        "    public const int AabbPointCount = 8;",
        "    public const int MaxVisibilityTestPoints = PrimitiveCount + AabbPointCount;",
        "",
        "    private static readonly VisibilityPrimitive[] _primitives =",
        "    [",
    ]

    for i, point in enumerate(points):
        x, y, z = point["local_point"]
        suffix = "," if i < len(points) - 1 else ""
        required_weapon_class = point["required_weapon_class"]
        property_lines: list[str] = []
        if point["use_fixed_head_origin"]:
            property_lines.append("            UseFixedHeadOrigin = true")
        if required_weapon_class != "None":
            property_lines.append(f"            RequiredWeaponClass = WeaponLosClass.{required_weapon_class}")

        lines.extend(
            [
                "        new()",
                "        {",
                f"            LocalPoint = new Vector3({format_float(x)}, {format_float(y)}, {format_float(z)})"
                + ("," if property_lines else ""),
            ]
        )
        for property_index, property_line in enumerate(property_lines):
            comma = "," if property_index < len(property_lines) - 1 else ""
            lines.append(f"{property_line}{comma}")
        lines.extend(
            [
                f"        }}{suffix}",
            ]
        )

    lines.extend(
        [
            "    ];",
            "",
            "    public static ReadOnlySpan<VisibilityPrimitive> Primitives => _primitives;",
            "}",
            "",
        ]
    )
    return "\n".join(lines)


def main() -> None:
    args = parse_args()
    data = json.loads(args.input.read_text(encoding="utf-8"))
    points = validate_points(data)
    generated = generate_layout(points)

    if args.check:
        current = args.output.read_text(encoding="utf-8") if args.output.exists() else ""
        if current.replace("\r\n", "\n") != generated:
            raise SystemExit(f"{args.output} is not up to date with {args.input}")
        return

    args.output.write_text(generated, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
