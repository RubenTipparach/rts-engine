#!/usr/bin/env python3
# Bake idle + walk animations for the humanoid units (worker, marine, scout)
# to assets/animations/. Per-bone Euler rotations + optional translation;
# frame count is deterministic so re-runs match byte-for-byte.
#
# Animation file is the source of truth — engine code may layer procedural
# tweaks on top, but the base motion comes from these JSONs.
#
# stdlib only.

from __future__ import annotations
import json, math, os

# Same character set as gen-rts-models.py. Per-entity stride/style tweaks
# customize the same base motion so they don't all march in lockstep.
CHARACTERS = [
    # id,     leg_swing,  arm_swing,  bounce,   walk_fps
    ("worker", 0.55,      0.40,       0.0010,   10),
    ("marine", 0.65,      0.55,       0.0008,   12),
    ("scout",  0.80,      0.70,       0.0014,   14),
]

# Bone order matches the .obj groups + JSON bones list. Hand-edits here MUST
# match tools/gen-rts-models.py.
BONES = ["torso", "head", "armL", "armR", "legL", "legR"]


def make_frame(transforms: dict[str, dict[str, list[float]]]) -> dict:
    # Always emit every bone, even if transform is zero, so the loader can
    # rely on a fixed key set.
    out: dict[str, dict[str, list[float]]] = {}
    for b in BONES:
        t = transforms.get(b, {})
        out[b] = {
            "rotate": list(t.get("rotate", [0.0, 0.0, 0.0])),
            "translate": list(t.get("translate", [0.0, 0.0, 0.0])),
        }
    return out


def build_idle(ent_id: str, bounce: float, frames: int = 16, fps: int = 8) -> dict:
    # Subtle breathing: torso bobs up, head sways slightly. Symmetric so the
    # cycle reads at low frame rates.
    out = []
    for i in range(frames):
        t = i / frames
        bob = math.sin(t * math.tau) * bounce * 0.6
        sway = math.sin(t * math.tau) * 0.04
        head_yaw = math.sin(t * math.tau * 0.5) * 0.06
        out.append(make_frame({
            "torso": {"translate": [0.0, bob, 0.0], "rotate": [0.0, 0.0, sway]},
            "head":  {"rotate": [0.0, head_yaw, 0.0]},
            "armL":  {"rotate": [sway * 0.5, 0.0, 0.0]},
            "armR":  {"rotate": [-sway * 0.5, 0.0, 0.0]},
        }))
    return {
        "name": f"{ent_id}_idle",
        "fps": fps,
        "loop": True,
        "bones": BONES,
        "frames": out,
    }


def build_walk(ent_id: str, leg_swing: float, arm_swing: float, bounce: float,
               fps: int, frames: int = 12) -> dict:
    # Classic 4-phase cycle baked at higher fidelity. Legs lead, arms swing
    # opposite, torso bounces twice per stride.
    out = []
    for i in range(frames):
        t = i / frames
        ang = t * math.tau
        leg_l = math.sin(ang) * leg_swing
        leg_r = -leg_l
        arm_l = -math.sin(ang) * arm_swing  # arms opposite same-side leg
        arm_r = -arm_l
        # Bounce hits twice per stride (when both feet planted between swings).
        bob = (math.cos(ang * 2.0) * 0.5 + 0.5) * bounce
        torso_lean = math.sin(ang) * 0.05
        head_counter = -torso_lean * 0.5  # head stabilizes against torso lean
        out.append(make_frame({
            "torso": {"translate": [0.0, bob, 0.0], "rotate": [0.0, 0.0, torso_lean]},
            "head":  {"rotate": [0.0, 0.0, head_counter]},
            "armL":  {"rotate": [arm_l, 0.0, 0.0]},
            "armR":  {"rotate": [arm_r, 0.0, 0.0]},
            "legL":  {"rotate": [leg_l, 0.0, 0.0]},
            "legR":  {"rotate": [leg_r, 0.0, 0.0]},
        }))
    return {
        "name": f"{ent_id}_walk",
        "fps": fps,
        "loop": True,
        "bones": BONES,
        "frames": out,
    }


def write_anim(path: str, anim: dict) -> None:
    with open(path, "w") as f:
        json.dump(anim, f, indent=2, sort_keys=False)
        f.write("\n")


def main() -> None:
    out_dir = os.path.normpath(os.path.join(os.path.dirname(__file__),
                                            "..", "assets", "animations"))
    os.makedirs(out_dir, exist_ok=True)
    for ent_id, leg_swing, arm_swing, bounce, walk_fps in CHARACTERS:
        idle = build_idle(ent_id, bounce)
        walk = build_walk(ent_id, leg_swing, arm_swing, bounce, walk_fps)
        write_anim(os.path.join(out_dir, f"{ent_id}_idle.anim.json"), idle)
        write_anim(os.path.join(out_dir, f"{ent_id}_walk.anim.json"), walk)
        print(f"wrote {ent_id} idle+walk")


if __name__ == "__main__":
    main()
