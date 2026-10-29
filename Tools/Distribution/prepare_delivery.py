"""Copy a successful, accepted Outpost build to a reviewable Windows ZIP.

This script never launches or builds Unity. All runtime acceptance must finish first.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


def digest(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def require_swarm_visual_acceptance(receipt: dict, visuals: dict) -> None:
    if receipt.get("instancingVariants") != "KeepAll":
        raise SystemExit("Resource and Player build must retain KeepAll instancing variants")
    if (visuals.get("swarmShader") != "Outpost/SwarmUnlit" or
            visuals.get("swarmShaderSupported") is not True or
            visuals.get("swarmMaterialInstancing") is not True or
            visuals.get("systemSupportsInstancing") is not True):
        raise SystemExit("Actual Player swarm shader/material/instancing checks have not passed")
    pixel_checks = (
        "enemyRenderedPixels", "enemyControlColorPixels",
        "projectileRenderedPixels", "projectileControlColorPixels",
    )
    if any(type(visuals.get(key)) is not int or visuals[key] < 2 for key in pixel_checks):
        raise SystemExit("Actual Player enemy/projectile instanced color pixel proofs have not passed")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--acceptance", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    receipt = json.loads((args.build / "build-receipt.json").read_text(encoding="utf-8-sig"))
    if receipt["status"] != "built" or not receipt["originalSettingsRestored"]:
        raise SystemExit("Build or settings restoration has not succeeded")
    acceptance = json.loads(args.acceptance.read_text(encoding="utf-8-sig"))
    if acceptance.get("status") != "passed":
        raise SystemExit("Standalone acceptance has not passed")
    visuals = acceptance.get("visualAcceptance", {})
    if (visuals.get("status") != "passed" or not visuals.get("visuallyReviewed") or
            not visuals.get("turretFoundation") or not visuals.get("turretCore") or
            visuals.get("armorBraceCount") != 6 or
            visuals.get("emplacementShader") != "Universal Render Pipeline/Unlit"):
        raise SystemExit("Explicit Player visual acceptance has not passed")
    require_swarm_visual_acceptance(receipt, visuals)
    required = ["Outpost.exe", "UnityPlayer.dll", "GameAssembly.dll", "Outpost_Data"]
    if any(not (args.build / name).exists() for name in required):
        raise SystemExit("Missing runtime files")
    if digest(args.build / "GameAssembly.dll") != acceptance.get("gameAssemblySha256"):
        raise SystemExit("The package binary differs from the accepted binary")
    if len(args.source_sha) != 40 or any(c not in "0123456789abcdef" for c in args.source_sha):
        raise SystemExit("A complete source commit SHA is required")
    destination = args.output / "Outpost-Windows-x64"
    args.output.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        raise SystemExit("Refusing to overwrite existing distribution")
    destination.mkdir()
    omitted: list[str] = []
    for item in args.build.iterdir():
        if item.name == "build-receipt.json" or "DontShip" in item.name or "DoNotShip" in item.name or item.suffix.lower() == ".pdb":
            omitted.append(item.name)
            continue
        target = destination / item.name
        if item.is_dir():
            shutil.copytree(item, target, ignore=shutil.ignore_patterns("*.pdb"))
        else:
            shutil.copy2(item, target)
    readme = """Outpost — Windows x64 离线演示

整体解压后双击 Outpost.exe。请保留同目录所有 DLL、D3D12 与 Outpost_Data。
不需要 Unity Editor、账号、本地 CDN 或排行榜服务器。
窗口初始 1280×720，可调整大小。标题页打开设置，开始游戏后自动射击与波次推进；
波间选择强化，操作 HUD 可调整速度/托管并撤离结算。设置和历史战绩保存在本机用户目录。
增援电台已完整内置，设置中的安装入口无需联网。

代码与项目说明：https://github.com/heroliss/Outpost
构建/验收版本见 VERSION.json；原始构建回执与验收摘要见 verification/。
这是非 Development 的 IL2CPP 演示包；普通启动不运行自动演示或验收。
如需开发者验收参数，请参考源仓库 docs/windows-distribution.md。

许可见 LICENSE 与 THIRD-PARTY/。SHA256SUMS.txt 提供除其自身之外的文件哈希。
"""
    (destination / "README.txt").write_text(readme, encoding="utf-8")
    shutil.copy2(args.source / "LICENSE", destination / "LICENSE")
    notices = destination / "THIRD-PARTY"
    notices.mkdir()
    explicit = [
        (args.source / "Packages/com.liss.ssframework/LICENSE", "SSFramework-LICENSE.txt"),
        (args.source / "Assets/Game/Fonts/noto sans_license.txt", "NotoSans-LICENSE.txt"),
        (args.source / "Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt", "LiberationSans-OFL.txt"),
        (args.source / "Assets/TextMesh Pro/Sprites/EmojiOne Attribution.txt", "EmojiOne-Attribution.txt"),
        (args.source / "Assets/Plugins/NuGet/LICENSE", "NuGet-Plugins-LICENSE.txt"),
    ]
    for path, name in explicit:
        if path.exists():
            shutil.copy2(path, notices / name)
    # Keep the installed package's actual notice, with its version/hash directory for provenance.
    # Some Editor-only dependencies may be listed too; this is a notice snapshot, not a runtime dependency claim.
    package_notices: list[str] = []
    cache = args.source / "Library/PackageCache"
    for package in sorted(cache.iterdir()):
        if not package.is_dir():
            continue
        for path in sorted(package.iterdir()):
            if path.is_file() and path.name.lower() in {"license", "license.md", "license.txt", "third party notices.md", "thirdpartynotices.md", "third party notices.txt"}:
                name = f"{package.name}-{path.name}"
                shutil.copy2(path, notices / name)
                package_notices.append(name)
    (notices / "README.txt").write_text(
        "本目录保留工程字体、插件、Framework 与本机实际安装 UPM 包的原始许可/声明。\n"
        "UPM 声明按安装版本/缓存哈希标识，可能包含仅 Editor 使用的构建依赖，不表示这些包全部进入 Player。\n",
        encoding="utf-8",
    )
    evidence = destination / "verification"
    evidence.mkdir()
    shutil.copy2(args.build / "build-receipt.json", evidence / "build-receipt.json")
    shutil.copy2(args.acceptance, evidence / "acceptance.json")
    version = {
        "project": "Outpost", "platform": "Windows x64", "sourceCommit": args.source_sha,
        "unityVersion": receipt["unityVersion"], "resourceVersion": receipt["resourceVersion"],
        "scriptingBackend": "IL2CPP", "developmentBuild": False,
        "instancingVariants": receipt["instancingVariants"],
        "assetPlayMode": "Offline", "packages": receipt["packages"],
        "standaloneAcceptance": acceptance["status"], "omittedBuildArtifacts": omitted,
    }
    (destination / "VERSION.json").write_text(json.dumps(version, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    files = sorted(p for p in destination.rglob("*") if p.is_file())
    lines = [f"{digest(path)}  {path.relative_to(destination).as_posix()}" for path in files]
    (destination / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    archive = args.output / "Outpost-Windows-x64.zip"
    if archive.exists():
        raise SystemExit("Refusing to overwrite an existing ZIP")
    with ZipFile(archive, "w", ZIP_DEFLATED, compresslevel=6) as zipped:
        for path in sorted(destination.rglob("*")):
            if path.is_file():
                zipped.write(path, path.relative_to(args.output))
    with ZipFile(archive) as zipped:
        corrupt = zipped.testzip()
        if corrupt:
            raise SystemExit(f"ZIP corruption: {corrupt}")
    summary = {"archive": str(archive.resolve()), "bytes": archive.stat().st_size, "sha256": digest(archive), "files": len(files) + 1, "packageNoticeFiles": len(package_notices), "sourceCommit": args.source_sha, "resourceVersion": receipt["resourceVersion"], "build": acceptance.get("build")}
    (args.output / "archive-manifest.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    main()
