"""Summarize completed Outpost Player receipts without launching or building Unity.

Visual review is an explicit human/agent attestation supplied by --visually-reviewed.
Without it the summary remains awaiting-visual-review and cannot pass packaging.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


SWARM_FLAGS = ("swarmShaderSupported", "swarmMaterialInstancing", "systemSupportsInstancing")
PIXEL_KEYS = (
    "enemyRenderedPixels", "enemyControlColorPixels",
    "projectileRenderedPixels", "projectileControlColorPixels",
)
VISUAL_KEYS = (
    "emplacementShader", "shaderFindResult", "turretFoundation", "turretCore",
    "armorBraceCount", "foundationVertexCount", "swarmShader", *SWARM_FLAGS,
    "enemySnapshotCount", "projectileSnapshotCount", *PIXEL_KEYS,
)
PERSISTENCE_KEYS = (
    "callsign", "runs", "score", "wave", "locale", "heatmap",
    "expansionInstalled", "expansionBgm",
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def read_receipt(path: Path) -> dict:
    require(path.is_file(), f"Receipt does not exist: {path}")
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    require(isinstance(value, dict), f"Receipt is not an object: {path}")
    return value


def digest(path: Path) -> str:
    require(path.is_file(), f"Required binary does not exist: {path}")
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def check_passed(receipt: dict, mode: str, label: str) -> None:
    require(receipt.get("mode") == mode, f"{label}: expected {mode} receipt")
    require(receipt.get("status") == "passed", f"{label}: terminal status is not passed")
    require(type(receipt.get("errorCount")) is int and receipt["errorCount"] == 0,
            f"{label}: errorCount is missing or nonzero")
    require(not receipt.get("error"), f"{label}: receipt contains an error")
    require(receipt.get("developmentBuild") is False, f"{label}: not a formal Player")
    require(bool(receipt.get("completedUtc")), f"{label}: completion timestamp is missing")


def check_visuals(receipt: dict, expected_backend: str, label: str) -> dict:
    check_passed(receipt, "exercise", label)
    require(receipt.get("backend") == expected_backend,
            f"{label}: expected actual backend {expected_backend}, got {receipt.get('backend')!r}")
    require(receipt.get("turretFoundation") is True and receipt.get("turretCore") is True and
            receipt.get("armorBraceCount") == 6 and
            type(receipt.get("foundationVertexCount")) is int and receipt["foundationVertexCount"] >= 6 and
            receipt.get("emplacementShader") == "Universal Render Pipeline/Unlit",
            f"{label}: turret geometry/material checks have not passed")
    require(receipt.get("swarmShader") == "Outpost/SwarmUnlit" and
            all(receipt.get(key) is True for key in SWARM_FLAGS),
            f"{label}: actual swarm shader/material/instancing checks have not passed")
    require(all(type(receipt.get(key)) is int and receipt[key] >= 2 for key in PIXEL_KEYS),
            f"{label}: enemy/projectile color pixel checks have not passed")
    require(all(type(receipt.get(key)) is int and receipt[key] > 0
                for key in ("enemySnapshotCount", "projectileSnapshotCount")),
            f"{label}: actual simulation enemy/projectile samples are missing")
    require(type(receipt.get("score")) is int and receipt["score"] > 0 and
            type(receipt.get("wave")) is int and receipt["wave"] >= 2 and
            type(receipt.get("runs")) is int and receipt["runs"] >= 1,
            f"{label}: completed battle record is missing")
    screenshots = receipt.get("screenshots")
    require(isinstance(screenshots, list), f"{label}: screenshot list is missing")
    by_name = {Path(path).name: Path(path) for path in screenshots if isinstance(path, str)}
    required_names = (
        "03-battle.png", "03-enemy-instancing-proof.png", "03-projectile-instancing-proof.png",
    )
    for name in required_names:
        require(name in by_name, f"{label}: required screenshot is missing: {name}")
        image = by_name[name]
        require(image.is_file() and image.stat().st_size > 1000,
                f"{label}: screenshot is missing or empty: {image}")
        with image.open("rb") as stream:
            require(stream.read(8) == b"\x89PNG\r\n\x1a\n", f"{label}: not a PNG: {image}")
    return {"backend": expected_backend, **{key: receipt[key] for key in VISUAL_KEYS},
            "screenshots": [str(by_name[name].resolve()) for name in required_names]}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", type=Path, required=True)
    parser.add_argument("--exercise", type=Path, required=True, help="Completed Ecs exercise receipt")
    parser.add_argument("--reload", type=Path, required=True, help="Reload of the same Ecs storage")
    parser.add_argument("--reference", type=Path, help="Optional completed Reference exercise receipt")
    parser.add_argument("--baseline", type=Path, help="Optional failed old-bundle A/B exercise receipt")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--visually-reviewed", action="store_true",
                        help="Explicitly attest review of actual battle and isolated proof PNGs")
    args = parser.parse_args()

    build = read_receipt(args.build / "build-receipt.json")
    require(build.get("status") == "built" and build.get("result") == "Succeeded" and
            build.get("originalSettingsRestored") is True and
            type(build.get("errors")) is int and build["errors"] == 0 and not build.get("error"),
            "Build or restoration did not finish successfully")
    require(build.get("instancingVariants") == "KeepAll", "Build does not record KeepAll instancing variants")
    assembly_sha = digest(args.build / "GameAssembly.dll")
    exercise = read_receipt(args.exercise)
    reload_receipt = read_receipt(args.reload)
    primary_visuals = check_visuals(exercise, "Ecs", "Ecs exercise")
    check_passed(reload_receipt, "reload", "Ecs reload")
    for label, receipt in (("Ecs exercise", exercise), ("Ecs reload", reload_receipt)):
        require(receipt.get("unityVersion") == build.get("unityVersion"),
                f"{label}: Unity version differs from the build")
        requested = receipt.get("storageDirectory")
        actual = receipt.get("actualStorageDirectory")
        require(isinstance(requested, str) and isinstance(actual, str) and
                bool(requested) and bool(actual) and
                Path(requested).resolve() == Path(actual).resolve(),
                f"{label}: requested and actual storage paths differ")
    require(Path(exercise["actualStorageDirectory"]).resolve() ==
            Path(reload_receipt["actualStorageDirectory"]).resolve(),
            "Ecs reload used a different actual storage directory")
    require(all(key in exercise and key in reload_receipt and exercise[key] == reload_receipt[key]
                for key in PERSISTENCE_KEYS), "Exercise and reload persisted values differ")
    require(isinstance(exercise.get("masterVolume"), (int, float)) and
            isinstance(reload_receipt.get("masterVolume"), (int, float)) and
            abs(exercise["masterVolume"] - reload_receipt["masterVolume"]) < 0.001,
            "Exercise and reload master volume differs or is missing")

    reference_result = None
    if args.reference:
        reference = read_receipt(args.reference)
        reference_result = check_visuals(reference, "Reference", "Reference exercise")
        require(reference.get("unityVersion") == build.get("unityVersion"),
                "Reference exercise Unity version differs from the build")
        reference_result["receipt"] = str(args.reference.resolve())

    baseline_result = None
    if args.baseline:
        baseline = read_receipt(args.baseline)
        require(baseline.get("mode") == "exercise" and baseline.get("status") == "failed",
                "Old-bundle A/B baseline must be a failed exercise receipt")
        require(any(type(baseline.get(key)) is int and baseline[key] == 0
                    for key in ("enemyRenderedPixels", "enemyControlColorPixels")),
                "Old-bundle A/B baseline must record zero enemy actual or control color pixels")
        baseline_result = {
            "receipt": str(args.baseline.resolve()), "status": baseline["status"],
            "enemyRenderedPixels": baseline.get("enemyRenderedPixels"),
            "enemyControlColorPixels": baseline.get("enemyControlColorPixels"),
            "error": baseline.get("error"),
            "scope": "Caller-supplied old-bundle A/B receipt; variant causality also requires the documented bundle comparison.",
        }

    reviewed = args.visually_reviewed
    summary = {
        "status": "passed" if reviewed else "awaiting-visual-review",
        "build": args.build.name, "buildDirectory": str(args.build.resolve()),
        "resourceVersion": build.get("resourceVersion"),
        "unityVersion": build.get("unityVersion"), "platform": "Windows x64",
        "scriptingBackend": "IL2CPP", "developmentBuild": False,
        "instancingVariants": build["instancingVariants"],
        "gameAssemblySha256": assembly_sha,
        "exercise": exercise["status"], "reload": reload_receipt["status"],
        "runtimeErrorCount": 0,
        "receipts": {"exercise": str(args.exercise.resolve()), "reload": str(args.reload.resolve())},
        "record": {key: exercise[key] for key in PERSISTENCE_KEYS},
        "storageDirectory": exercise["actualStorageDirectory"],
        "visualAcceptance": {
            "status": "passed" if reviewed else "awaiting-review",
            "visuallyReviewed": reviewed,
            "visualReviewAttestation": "Explicit --visually-reviewed flag" if reviewed else "Not supplied",
            **primary_visuals,
        },
        "reference": reference_result,
        "baselineComparison": baseline_result,
        "limits": (
            "One Windows host. Actual UI callbacks and business commands; isolated instancing pixel probes "
            "use the real runtime material, mesh and camera plus a synthetic control-color instance. "
            "The optional review flag attests PNG review only. Ordinary launch, physical mouse interaction, "
            "other devices/platforms and independent ZIP extraction require separate evidence. "
            "Receipts do not embed binary hashes; the GameAssembly hash identifies the supplied build directory. "
            + ("Ecs and Reference exercises passed; reload was checked for Ecs only."
               if reference_result else "Ecs exercise/reload passed; Reference was not supplied.")
        ),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": summary["status"], "output": str(args.output.resolve()),
                      "reference": reference_result is not None, "baseline": baseline_result is not None},
                     ensure_ascii=False))


if __name__ == "__main__":
    main()
