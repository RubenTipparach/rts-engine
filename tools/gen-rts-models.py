#!/usr/bin/env python3
# Bake .obj models for every RTS building and unit to assets/models/. The
# .obj is the canonical on-disk geometry per the static-asset guidelines —
# the in-engine renderer's procedural box is a stand-in until a loader
# consumes these. A JSON sidecar carries per-group color hints and (for
# characters) bone parent/pivot info that the .obj format can't express.
#
# Y-up, world units = planet radius units (matches rts.yaml dimensions).
# stdlib only.

from __future__ import annotations
import json
import os

# Mirrors rts.yaml. Kept in sync by hand — both this and rts.yaml should be
# updated together when entity dimensions change.
BUILDINGS = [
    # id,             half_w, height, color
    ("command_center", 0.020, 0.035, (0.65, 0.70, 0.85)),
    ("barracks",       0.0175,0.025, (0.55, 0.35, 0.25)),
    ("factory",        0.0225,0.030, (0.40, 0.42, 0.48)),
]
VEHICLES = [
    ("tank", 0.007,  0.007,  (0.45, 0.45, 0.50)),
    ("apc",  0.0065, 0.0065, (0.50, 0.45, 0.30)),
]
CHARACTERS = [
    ("worker", 0.005,  0.009, (0.95, 0.85, 0.30)),
    ("marine", 0.0045, 0.010, (0.30, 0.65, 0.30)),
    ("scout",  0.004,  0.008, (0.70, 0.65, 0.35)),
]


# ── obj writer ────────────────────────────────────────────────────────────
class ObjBuilder:
    def __init__(self, name: str):
        self.name = name
        self.lines: list[str] = [f"# {name}.obj — baked by tools/gen-rts-models.py", f"o {name}"]
        self.v_count = 0
        self.n_count = 0

    def group(self, group_name: str) -> None:
        self.lines.append(f"g {group_name}")

    def box(self, cx: float, cy: float, cz: float,
            sx: float, sy: float, sz: float) -> None:
        # cx/cy/cz = bottom-center x, base y, center z. sx/sy/sz = full
        # extents along each axis. 24 verts (4 per face) for flat shading.
        x0, x1 = cx - sx * 0.5, cx + sx * 0.5
        y0, y1 = cy, cy + sy
        z0, z1 = cz - sz * 0.5, cz + sz * 0.5

        faces = [
            # (corners CCW when viewed from outside, normal)
            ((x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1), ( 0,  0,  1)),  # +Z
            ((x1, y0, z0), (x0, y0, z0), (x0, y1, z0), (x1, y1, z0), ( 0,  0, -1)),  # -Z
            ((x1, y0, z1), (x1, y0, z0), (x1, y1, z0), (x1, y1, z1), ( 1,  0,  0)),  # +X
            ((x0, y0, z0), (x0, y0, z1), (x0, y1, z1), (x0, y1, z0), (-1,  0,  0)),  # -X
            ((x0, y1, z1), (x1, y1, z1), (x1, y1, z0), (x0, y1, z0), ( 0,  1,  0)),  # +Y
            ((x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1), ( 0, -1,  0)),  # -Y
        ]
        for p0, p1, p2, p3, n in faces:
            for p in (p0, p1, p2, p3):
                self.lines.append(f"v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}")
            self.lines.append(f"vn {n[0]:.4f} {n[1]:.4f} {n[2]:.4f}")
            v = self.v_count + 1
            n_idx = self.n_count + 1
            self.lines.append(f"f {v}//{n_idx} {v + 1}//{n_idx} {v + 2}//{n_idx}")
            self.lines.append(f"f {v}//{n_idx} {v + 2}//{n_idx} {v + 3}//{n_idx}")
            self.v_count += 4
            self.n_count += 1

    def write(self, path: str) -> None:
        with open(path, "w") as f:
            f.write("\n".join(self.lines) + "\n")


# ── shape composers ───────────────────────────────────────────────────────
def build_command_center(b: ObjBuilder, hw, h):
    # Base + main body + tower + antenna.
    b.group("base")
    b.box(0, 0, 0, hw * 2.0, h * 0.25, hw * 2.0)
    b.group("body")
    b.box(0, h * 0.25, 0, hw * 1.6, h * 0.45, hw * 1.6)
    b.group("tower")
    b.box(0, h * 0.70, 0, hw * 0.9, h * 0.22, hw * 0.9)
    b.group("antenna")
    b.box(0, h * 0.92, 0, hw * 0.15, h * 0.20, hw * 0.15)


def build_barracks(b, hw, h):
    # Long rectangular hall with a narrower roof slab on top.
    b.group("walls")
    b.box(0, 0, 0, hw * 2.4, h * 0.85, hw * 1.6)
    b.group("roof")
    b.box(0, h * 0.85, 0, hw * 2.2, h * 0.20, hw * 1.4)


def build_factory(b, hw, h):
    # Main hall + two smokestacks.
    b.group("hall")
    b.box(0, 0, 0, hw * 2.0, h * 0.75, hw * 2.0)
    b.group("stack_a")
    b.box(-hw * 0.55, h * 0.75, -hw * 0.35, hw * 0.20, h * 0.40, hw * 0.20)
    b.group("stack_b")
    b.box( hw * 0.55, h * 0.75,  hw * 0.35, hw * 0.20, h * 0.55, hw * 0.20)


def build_tank(b, hw, h):
    # Hull + turret + barrel.
    b.group("hull")
    b.box(0, 0, 0, hw * 2.4, h * 0.45, hw * 1.6)
    b.group("turret")
    b.box(0, h * 0.45, 0, hw * 1.4, h * 0.30, hw * 1.0)
    b.group("barrel")
    b.box(hw * 1.1, h * 0.55, 0, hw * 1.6, h * 0.10, hw * 0.16)


def build_apc(b, hw, h):
    # Long body + cabin + antenna.
    b.group("body")
    b.box(0, 0, 0, hw * 2.6, h * 0.55, hw * 1.4)
    b.group("cabin")
    b.box(-hw * 0.4, h * 0.55, 0, hw * 1.0, h * 0.30, hw * 1.2)
    b.group("antenna")
    b.box(hw * 0.8, h * 0.55, 0, hw * 0.10, h * 0.45, hw * 0.10)


def build_character(b, hw, h):
    # Stacked boxes named to match the character animation bones. Hand-edits
    # to bone counts here MUST match tools/gen-rts-animations.py.
    leg_h = h * 0.45
    torso_h = h * 0.30
    head_h = h * 0.18
    arm_h = h * 0.30
    arm_w = hw * 0.45
    leg_w = hw * 0.50
    torso_w = hw * 1.5
    torso_d = hw * 1.0
    head_w = hw * 1.0

    b.group("legL")
    b.box(-leg_w * 0.6, 0, 0, leg_w, leg_h, leg_w)
    b.group("legR")
    b.box( leg_w * 0.6, 0, 0, leg_w, leg_h, leg_w)
    b.group("torso")
    b.box(0, leg_h, 0, torso_w, torso_h, torso_d)
    b.group("head")
    b.box(0, leg_h + torso_h, 0, head_w, head_h, head_w)
    b.group("armL")
    b.box(-(torso_w * 0.5 + arm_w * 0.5), leg_h, 0, arm_w, arm_h, arm_w)
    b.group("armR")
    b.box( (torso_w * 0.5 + arm_w * 0.5), leg_h, 0, arm_w, arm_h, arm_w)


# ── sidecar JSON ──────────────────────────────────────────────────────────
def write_sidecar(path: str, kind: str, color, groups: list[str],
                  bones: list[dict] | None = None) -> None:
    data = {
        "kind": kind,
        "color": list(color),
        "groups": groups,
    }
    if bones is not None:
        data["bones"] = bones
    with open(path, "w") as f:
        json.dump(data, f, indent=2)
        f.write("\n")


def main():
    out_dir = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "assets", "models"))
    os.makedirs(out_dir, exist_ok=True)

    for ent_id, hw, h, col in BUILDINGS:
        b = ObjBuilder(ent_id)
        {
            "command_center": build_command_center,
            "barracks": build_barracks,
            "factory": build_factory,
        }[ent_id](b, hw, h)
        b.write(os.path.join(out_dir, f"{ent_id}.obj"))
        write_sidecar(os.path.join(out_dir, f"{ent_id}.json"), "building", col,
                      [ln.split()[1] for ln in b.lines if ln.startswith("g ")])
        print(f"wrote {ent_id}")

    for ent_id, hw, h, col in VEHICLES:
        b = ObjBuilder(ent_id)
        {"tank": build_tank, "apc": build_apc}[ent_id](b, hw, h)
        b.write(os.path.join(out_dir, f"{ent_id}.obj"))
        write_sidecar(os.path.join(out_dir, f"{ent_id}.json"), "vehicle", col,
                      [ln.split()[1] for ln in b.lines if ln.startswith("g ")])
        print(f"wrote {ent_id}")

    for ent_id, hw, h, col in CHARACTERS:
        b = ObjBuilder(ent_id)
        build_character(b, hw, h)
        # Bone hierarchy + pivots used by anim files. Pivot is the bone's local
        # origin in the rest pose — matches where build_character anchored each
        # group so animations rotating around the pivot don't shear the mesh.
        leg_h = h * 0.45
        torso_h = h * 0.30
        bones = [
            {"name": "torso", "parent": None,    "pivot": [0.0, leg_h, 0.0]},
            {"name": "head",  "parent": "torso", "pivot": [0.0, leg_h + torso_h, 0.0]},
            {"name": "armL",  "parent": "torso", "pivot": [-(hw * 1.5 * 0.5), leg_h + torso_h, 0.0]},
            {"name": "armR",  "parent": "torso", "pivot": [ (hw * 1.5 * 0.5), leg_h + torso_h, 0.0]},
            {"name": "legL",  "parent": "torso", "pivot": [-(hw * 0.50 * 0.6), leg_h, 0.0]},
            {"name": "legR",  "parent": "torso", "pivot": [ (hw * 0.50 * 0.6), leg_h, 0.0]},
        ]
        b.write(os.path.join(out_dir, f"{ent_id}.obj"))
        write_sidecar(os.path.join(out_dir, f"{ent_id}.json"), "character", col,
                      [ln.split()[1] for ln in b.lines if ln.startswith("g ")],
                      bones=bones)
        print(f"wrote {ent_id}")


if __name__ == "__main__":
    main()
