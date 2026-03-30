#!/usr/bin/env python3
"""
Extract the canonical CS2 player hitbox primitive layout from a local game install.

Requires:
  - Counter-Strike 2 installed locally
  - Valve resourceinfo.exe available in the CS2 bin folder

Default output:
  tools/cs2_player_hitboxes_canonical.json

Source provenance:
  - This script is the reproducible path for regenerating the checked-in
    canonical hitbox layout used by S2FOW.
  - If the local install does not expose stock pak/resourceinfo assets, the
    checked-in JSON remains the primary local geometry source.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import struct
import subprocess
import tempfile
from pathlib import Path
from typing import Dict, Iterable, List, Tuple

DEFAULT_CS2_ROOT_CANDIDATES = [
    Path(r"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"),
    Path(r"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive 730"),
]

PHASE2_MODELS = [
    "phase2/characters/models/ctm_sas/ctm_sas_ag2.vmdl_c",
    "phase2/characters/models/ctm_swat/ctm_swat_variante_ag2.vmdl_c",
    "phase2/characters/models/tm_balkan/tm_balkan_variantf_ag2.vmdl_c",
]
LEGACY_MODEL = "characters/models/tm_phoenix/tm_phoenix.vmdl_c"

RUNTIME_ORDER = [
    "head_0",
    "neck_0",
    "spine_3",
    "spine_2",
    "spine_1",
    "spine_0",
    "pelvis",
    "ankle_l",
    "ankle_r",
    "leg_lower_l",
    "leg_lower_r",
    "arm_upper_l",
    "arm_upper_r",
    "arm_lower_l",
    "arm_lower_r",
    "hand_l",
    "hand_r",
    "leg_upper_l",
    "leg_upper_r",
]

SAMPLING = {
    "head_0": ("SupportAndEndpoints", True),
    "neck_0": ("SupportAndEndpoints", False),
    "spine_3": ("SupportAndEndpoints", False),
    "spine_2": ("SupportAndEndpoints", False),
    "spine_1": ("SupportAndEndpoints", False),
    "spine_0": ("SupportAndEndpoints", False),
    "pelvis": ("SupportAndEndpoints", False),
    "ankle_l": ("SupportAndEndpoints", False),
    "ankle_r": ("SupportAndEndpoints", False),
    "leg_lower_l": ("SupportMidAndDistal", False),
    "leg_lower_r": ("SupportMidAndDistal", False),
    "arm_upper_l": ("SupportMidAndDistal", False),
    "arm_upper_r": ("SupportMidAndDistal", False),
    "arm_lower_l": ("SupportMidAndDistal", False),
    "arm_lower_r": ("SupportMidAndDistal", False),
    "hand_l": ("SupportAndEndpoints", False),
    "hand_r": ("SupportAndEndpoints", False),
    "leg_upper_l": ("SupportMidAndDistal", False),
    "leg_upper_r": ("SupportMidAndDistal", False),
}

DISTAL_ENDPOINT_IS_POINT1 = {
    "head_0": True,
    "neck_0": True,
    "spine_3": True,
    "spine_2": True,
    "spine_1": True,
    "spine_0": True,
    "pelvis": True,
    "ankle_l": True,
    "ankle_r": False,
    "leg_lower_l": True,
    "leg_lower_r": True,
    "arm_upper_l": True,
    "arm_upper_r": True,
    "arm_lower_l": True,
    "arm_lower_r": True,
    "hand_l": True,
    "hand_r": True,
    "leg_upper_l": True,
    "leg_upper_r": True,
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--cs2-root",
        type=Path,
        default=None,
        help="Optional explicit CS2 install root. If omitted, known Steam install roots are autodetected.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).resolve().with_name("cs2_player_hitboxes_canonical.json"),
    )
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="Only verify install-root and required stock assets. Do not extract or write JSON.",
    )
    return parser.parse_args()


def resolve_cs2_root(explicit_root: Path | None) -> Path:
    if explicit_root is not None:
        return explicit_root

    for candidate in DEFAULT_CS2_ROOT_CANDIDATES:
        if candidate.exists():
            return candidate

    checked = "\n  - ".join(str(candidate) for candidate in DEFAULT_CS2_ROOT_CANDIDATES)
    raise SystemExit(
        "Could not autodetect a local CS2 install root. Checked:\n"
        f"  - {checked}\n"
        "Pass --cs2-root explicitly if your install lives elsewhere."
    )


def get_stock_asset_paths(cs2_root: Path) -> tuple[Path, Path]:
    return (
        cs2_root / "game" / "csgo" / "pak01_dir.vpk",
        cs2_root / "game" / "bin" / "win64" / "resourceinfo.exe",
    )


def validate_stock_assets(cs2_root: Path) -> tuple[Path, Path]:
    if not cs2_root.exists():
        raise SystemExit(f"CS2 install root does not exist: {cs2_root}")

    vpk_dir, resourceinfo = get_stock_asset_paths(cs2_root)
    missing: list[str] = []
    if not vpk_dir.exists():
        missing.append(f"pak01_dir.vpk not found at {vpk_dir}")
    if not resourceinfo.exists():
        missing.append(f"resourceinfo.exe not found at {resourceinfo}")

    if missing:
        checked_in_json = Path(__file__).resolve().with_name("cs2_player_hitboxes_canonical.json")
        detail = "\n  - ".join(missing)
        raise SystemExit(
            "Local CS2 install was found, but the stock extraction assets required for regeneration are unavailable:\n"
            f"  - {detail}\n"
            f"Checked install root: {cs2_root}\n"
            f"Use the checked-in canonical geometry instead: {checked_in_json}"
        )

    return vpk_dir, resourceinfo


def read_vpk_entries(vpk_dir: Path, wanted: Iterable[str]) -> Dict[str, Tuple[int, int, int, bytes]]:
    wanted = set(wanted)
    entries: Dict[str, Tuple[int, int, int, bytes]] = {}

    with vpk_dir.open("rb") as handle:
        signature, version, tree_size = struct.unpack("<III", handle.read(12))
        if signature != 0x55AA1234:
            raise RuntimeError(f"Unexpected VPK signature: 0x{signature:X}")

        if version != 2:
            raise RuntimeError(f"Unexpected VPK version: {version}")

        handle.read(16)
        tree_start = handle.tell()

        def read_cstr() -> str:
            out = bytearray()
            while True:
                chunk = handle.read(1)
                if not chunk:
                    raise EOFError("Unexpected EOF while reading VPK directory tree")
                if chunk == b"\x00":
                    return out.decode("utf-8", errors="ignore")
                out += chunk

        while handle.tell() < tree_start + tree_size and len(entries) < len(wanted):
            ext = read_cstr()
            if not ext:
                break

            while True:
                path = read_cstr()
                if not path:
                    break

                while True:
                    name = read_cstr()
                    if not name:
                        break

                    crc, preload, archive, offset, length, terminator = struct.unpack(
                        "<IHHIIH", handle.read(18)
                    )
                    full_path = ((path + "/" if path != " " else "") + name + "." + ext).replace("\\", "/")
                    preload_data = handle.read(preload) if preload else b""

                    if full_path in wanted:
                        entries[full_path] = (archive, offset, length, preload_data)

    missing = wanted.difference(entries.keys())
    if missing:
        raise FileNotFoundError(f"Missing VPK entries: {sorted(missing)}")

    return entries


def extract_temp_file(cs2_root: Path, vpk_entry: Tuple[int, int, int, bytes], name: str) -> Path:
    archive, offset, length, preload_data = vpk_entry
    archive_path = cs2_root / "game" / "csgo" / f"pak01_{archive:03d}.vpk"
    with archive_path.open("rb") as handle:
        handle.seek(offset)
        data = preload_data + handle.read(length)

    temp_path = Path(tempfile.gettempdir()) / name
    temp_path.write_bytes(data)
    return temp_path


def run_resourceinfo(cs2_root: Path, resourceinfo_exe: Path, input_path: Path) -> str:
    game_path = cs2_root / "game" / "csgo"
    result = subprocess.run(
        [str(resourceinfo_exe), "-game", str(game_path), "-i", str(input_path), "-all"],
        capture_output=True,
        text=True,
        errors="ignore",
        check=True,
    )
    return result.stdout


def qmul(a: List[float], b: List[float]) -> List[float]:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    ]


def qrot(q: List[float], v: List[float]) -> List[float]:
    x, y, z, w = q
    uv = [y * v[2] - z * v[1], z * v[0] - x * v[2], x * v[1] - y * v[0]]
    uuv = [y * uv[2] - z * uv[1], z * uv[0] - x * uv[2], x * uv[1] - y * uv[0]]
    return [v[i] + 2.0 * (w * uv[i] + uuv[i]) for i in range(3)]


def parse_model_text(text: str) -> Dict[str, dict]:
    lines = text.splitlines()

    hitboxes: Dict[int, dict] = {}
    current: dict | None = None
    in_hitboxsets = False

    for line in lines:
        s = line.strip()
        if s.startswith("m_hitboxsets ="):
            in_hitboxsets = True
            continue

        if not in_hitboxsets:
            continue

        if s == "{":
            current = {}
            continue

        if current is None:
            continue

        for key, pattern in [
            ("name", r'm_name = "([^"]+)"'),
            ("bone", r'm_sBoneName = "([^"]+)"'),
            ("radius", r"m_flShapeRadius = ([\-0-9.]+)"),
            ("shape", r"m_nShapeType = (\d+)"),
            ("index", r"m_nHitBoxIndex = (\d+)"),
        ]:
            match = re.match(pattern, s)
            if match:
                if key == "radius":
                    current[key] = float(match.group(1))
                elif key in ("shape", "index"):
                    current[key] = int(match.group(1))
                else:
                    current[key] = match.group(1)
                break
        else:
            minimum = re.match(r"m_vMinBounds = \[ ([^\]]+) \]", s)
            maximum = re.match(r"m_vMaxBounds = \[ ([^\]]+) \]", s)
            if minimum:
                current["min"] = [float(x.strip()) for x in minimum.group(1).split(",")]
            elif maximum:
                current["max"] = [float(x.strip()) for x in maximum.group(1).split(",")]
            else:
                continue

        if "index" in current and {"name", "bone", "min", "max", "radius", "shape", "index"} <= current.keys():
            hitboxes.setdefault(current["index"], current.copy())
            current = None
            if len(hitboxes) == 19:
                break

    start = text.find("m_modelSkeleton =")
    if start == -1:
        raise RuntimeError("Could not locate m_modelSkeleton in resource output")

    sub_lines = text[start:].splitlines()
    bone_names: List[str] = []
    parents: List[int] = []
    bone_pos: List[List[float]] = []
    bone_rot: List[List[float]] = []
    state = None

    for line in sub_lines:
        s = line.strip()
        if s.startswith("m_boneName =") and not bone_names:
            state = "bone_names"
            continue
        if s.startswith("m_nParent =") and bone_names and not parents:
            state = "parents"
            continue
        if s.startswith("m_bonePosParent =") and parents and not bone_pos:
            state = "bone_pos"
            continue
        if s.startswith("m_boneRotParent =") and bone_pos and not bone_rot:
            state = "bone_rot"
            continue

        if state == "bone_names":
            if s.startswith("]"):
                state = None
                continue
            bone_names.extend(re.findall(r'"([^"]+)"', s))
            continue

        if state == "parents":
            if s.startswith("]"):
                state = None
                continue
            parents.extend(int(x) for x in re.findall(r"-?\d+", s))
            continue

        if state == "bone_pos":
            if s.startswith("]"):
                state = None
                continue
            match = re.match(r"\[ ([^\]]+) \]", s)
            if match:
                bone_pos.append([float(x.strip()) for x in match.group(1).split(",")])
            continue

        if state == "bone_rot":
            if s.startswith("]"):
                state = None
                continue
            match = re.match(r"\[ ([^\]]+) \]", s)
            if match:
                bone_rot.append([float(x.strip()) for x in match.group(1).split(",")])

    world_pos = [None] * len(bone_names)
    world_rot = [None] * len(bone_names)
    for i in range(len(bone_names)):
        parent = parents[i]
        if parent == -1:
            world_pos[i] = bone_pos[i]
            world_rot[i] = bone_rot[i]
        else:
            rotated = qrot(world_rot[parent], bone_pos[i])
            world_pos[i] = [world_pos[parent][j] + rotated[j] for j in range(3)]
            world_rot[i] = qmul(world_rot[parent], bone_rot[i])

    name_to_idx = {name.lower(): i for i, name in enumerate(bone_names)}
    out: Dict[str, dict] = {}
    for hitbox in hitboxes.values():
        idx = name_to_idx[hitbox["bone"].lower()]
        origin = world_pos[idx]
        rotation = world_rot[idx]
        p0 = [origin[j] + qrot(rotation, hitbox["min"])[j] for j in range(3)]
        p1 = [origin[j] + qrot(rotation, hitbox["max"])[j] for j in range(3)]
        out[hitbox["name"].lower()] = {
            "name": hitbox["name"],
            "bone": hitbox["bone"],
            "radius": hitbox["radius"],
            "shape": hitbox["shape"],
            "point0": p0,
            "point1": p1,
            "center": [(p0[j] + p1[j]) * 0.5 for j in range(3)],
        }

    return out


def center_delta(a: dict, b: dict) -> float:
    return math.dist(a["center"], b["center"])


def round_point(values: List[float]) -> List[float]:
    return [round(v, 2) for v in values]


def build_output(models: Dict[str, Dict[str, dict]], cs2_root: Path) -> dict:
    phase2_base = models[PHASE2_MODELS[0]]

    phase2_identical = True
    for model_path in PHASE2_MODELS[1:]:
        for primitive_name in RUNTIME_ORDER:
            if center_delta(phase2_base[primitive_name], models[model_path][primitive_name]) > 0.001:
                phase2_identical = False
                break

    legacy_max_delta = 0.0
    legacy_max_name = ""
    for primitive_name in RUNTIME_ORDER:
        delta = center_delta(phase2_base[primitive_name], models[LEGACY_MODEL][primitive_name])
        if delta > legacy_max_delta:
            legacy_max_delta = delta
            legacy_max_name = primitive_name

    primitives = []
    for index, primitive_name in enumerate(RUNTIME_ORDER):
        source = phase2_base[primitive_name]
        sampling, use_fixed_head_origin = SAMPLING[primitive_name]
        primitives.append(
            {
                "index": index,
                "name": source["name"],
                "bone": source["bone"],
                "kind": "Capsule" if source["shape"] == 2 else f"Shape{source['shape']}",
                "sampling": sampling,
                "use_fixed_head_origin": use_fixed_head_origin,
                "distal_endpoint_is_point1": DISTAL_ENDPOINT_IS_POINT1[primitive_name],
                "local_point0": round_point(source["point0"]),
                "local_point1": round_point(source["point1"]),
                "radius": round(source["radius"], 2),
            }
        )

    return {
        "source": {
            "cs2_root": str(cs2_root),
            "phase2_models": PHASE2_MODELS,
            "legacy_reference_model": LEGACY_MODEL,
        },
        "validation": {
            "phase2_models_identical": phase2_identical,
            "legacy_max_center_delta": round(legacy_max_delta, 4),
            "legacy_max_center_delta_primitive": legacy_max_name,
        },
        "primitive_count": len(primitives),
        "primitives": primitives,
    }


def main() -> None:
    args = parse_args()
    cs2_root = resolve_cs2_root(args.cs2_root)
    vpk_dir, resourceinfo_exe = validate_stock_assets(cs2_root)

    if args.verify_only:
        print(f"CS2 root: {cs2_root}")
        print(f"pak01_dir.vpk: {vpk_dir}")
        print(f"resourceinfo.exe: {resourceinfo_exe}")
        print("Stock extraction assets are available.")
        return

    wanted_models = [*PHASE2_MODELS, LEGACY_MODEL]
    entries = read_vpk_entries(vpk_dir, wanted_models)

    models = {}
    for model_path in wanted_models:
        temp_path = extract_temp_file(cs2_root, entries[model_path], Path(model_path).name)
        models[model_path] = parse_model_text(run_resourceinfo(cs2_root, resourceinfo_exe, temp_path))

    output = build_output(models, cs2_root)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")

    print(f"Wrote {output['primitive_count']} primitives to {args.output}")
    print(
        "Phase2 identical:",
        output["validation"]["phase2_models_identical"],
        "| legacy max center delta:",
        output["validation"]["legacy_max_center_delta"],
        "at",
        output["validation"]["legacy_max_center_delta_primitive"],
    )


if __name__ == "__main__":
    main()
