# 一次性脚本：生成 OutpostExpansionPackage（扩展包）的音频资产——「增援电台」战斗 BGM 变体。
# 从项目根运行：python Tools/gen_outpost_expansion_audio.py
# 产物落 Assets/Game/Outpost/ResExpansion/Audio/（扩展包收集器覆盖，跨包按文件名寻址）。
#
# 变体 = 主战斗曲构建器 build_battle_track 的 radio 皮（同一 120BPM 鼓组/贝斯/编排骨架——两首战斗曲
# 能量一致、切换不突兀）：主题旋律换莫尔斯呼叫、和弦戳换窄带"薄"音色、drone 高八度载波感、加静电噼啪底。
# 复用主脚本代码是安全的：每资产独立 RNG seed（旧版"两脚本必须隔离共享 RNG"的顾虑已不存在），
# 且主脚本的生成入口在 __main__ 下、import 不触发生成。
import numpy as np

from gen_outpost_audio import BGM_RMS, build_battle_track
from outpost_audio_dsp import normalize, seam_report, write_wav

OUT_DIR = "Assets/Game/Outpost/ResExpansion/Audio"


def make_bgm_battle_alt():
    rng = np.random.default_rng(301)
    looped = build_battle_track(rng, radio=True)
    print(seam_report(looped, "bgm_battle_alt"))
    write_wav(OUT_DIR, "bgm_battle_alt", normalize(looped, BGM_RMS, peak_cap=0.8))


if __name__ == "__main__":
    make_bgm_battle_alt()
