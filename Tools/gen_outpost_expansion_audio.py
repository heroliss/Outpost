# 一次性脚本：生成 OutpostExpansionPackage（扩展包）的音频资产——「增援电台」战斗 BGM 变体。
# 从项目根运行：python Tools/gen_outpost_expansion_audio.py
# 产物落 Assets/Game/Outpost/ResExpansion/Audio/（扩展包收集器覆盖，跨包按文件名寻址）。
#
# 2026-07-16 随主脚本一并重写（DSP 引擎共用 Tools/outpost_audio_dsp.py——每资产独立 seed 后
# 旧版"两脚本必须隔离 RNG"的顾虑不复存在，共享 DSP 代码不再互相牵连）。
#
# 音乐设计：与主战斗曲同一骨架（100BPM · 48s · Am→Em→Dm→Am · 心跳律动），换一副「军用电台」的皮——
# 失谐载波 drone（0.7Hz 拍频）+ 行军刷点 + 每段一组莫尔斯呼叫（窄带薄音色=电台质感）+ 静电噼啪底。
import numpy as np

from outpost_audio_dsp import (
    RATE, silence, mix_into, to_stereo, midi,
    osc_sine, detuned_saw_stack,
    env_ar, env_exp,
    lowpass, bandpass,
    delay_echo, reverb, chorus,
    wrap_loop_tail, seam_report,
    white, crackle, normalize, write_wav, samples,
)

OUT_DIR = "Assets/Game/Outpost/ResExpansion/Audio"

BPM = 100.0
BEAT = 60.0 / BPM
BAR = BEAT * 4
LOOP = BAR * 20  # 48s，与主战斗曲同长同格——切换不突兀
SECTIONS = [  # (根音 midi, 垫声位)；骨架同主曲
    (33, None),
    (40, [47, 52, 55]),
    (38, [50, 53, 57]),
    (33, [52, 57, 60]),
    (33, None),
]
MORSE_FREQS = [880.0, 988.0, 784.0, 880.0, None]  # 每段一组呼叫音标（薄、窄带）


def kick(f_hi=90.0, f_lo=44.0, sec=0.16, tau=0.09):
    n = samples(sec)
    t = np.arange(n) / RATE
    f = f_lo + (f_hi - f_lo) * np.exp(-t * 22)
    return osc_sine(f, sec) * env_exp(n, tau)


def brush(sec, rng, tau=0.03):
    """行军弱拍刷点：低通噪声极短衰减（军鼓刷感）。"""
    return lowpass(white(sec, rng), 2200) * env_exp(samples(sec), tau)


def morse_call(freq, rng):
    """一组「电台呼叫」短-短-长：窄带正弦 + 轻微静电颗粒，1.2kHz 附近的薄音色=收音机质感。"""
    x = silence(0.8)
    for i, dur in enumerate((0.07, 0.07, 0.2)):
        n = samples(dur + 0.05)
        tone_ = osc_sine(freq, dur + 0.05) * env_ar(n, 0.004, 0.05)
        mix_into(x, tone_, i * 0.14, gain=0.8)
    grain = bandpass(white(0.8, rng), freq * 0.7, freq * 1.5) * 0.06
    return bandpass(x + grain * (np.abs(x) > 0.01), 500, 2600)  # 窄带化：只在响时混入颗粒


def make_bgm_battle_alt():
    rng = np.random.default_rng(301)
    total = LOOP + 5.0
    n = samples(total)

    # 失谐载波 drone：根音双振荡（+0.7Hz 拍频）+ 2 次谐波，LP 200——比主曲 drone 更"载波"
    drone = silence(total)
    for i, (root, _) in enumerate(SECTIONS):
        seg_len = BAR * 4 + 1.2
        f = midi(root)
        x = np.zeros(samples(seg_len))
        for df in (0.0, 0.7):
            x += osc_sine(f + df, seg_len) + 0.4 * osc_sine((f + df) * 2, seg_len)
        x = lowpass(x, 200) * env_ar(samples(seg_len), 1.2, 1.6)
        mix_into(drone, x, i * BAR * 4, gain=0.30)

    # 行军拍：主拍每小节 1、3 拍 + 2、4 拍弱刷——比主曲心跳更"步进"
    beats = silence(total)
    for bar in range(20):
        at = bar * BAR
        mix_into(beats, kick(), at, gain=0.55)
        mix_into(beats, kick(f_hi=76, f_lo=42), at + 2 * BEAT, gain=0.34)
        mix_into(beats, brush(0.06, rng), at + 1 * BEAT, gain=0.16)
        mix_into(beats, brush(0.06, rng), at + 3 * BEAT, gain=0.20)
    for k in range(8):  # 收束末小节滚奏引回循环头（同主曲）
        mix_into(beats, kick(), 19 * BAR + k * BEAT / 2, gain=0.10 + 0.03 * k)

    # 暗垫（同主曲声位，LP 更低=更远的电台氛围）
    pad = silence(total)
    for i, (_, notes) in enumerate(SECTIONS):
        if notes is None:
            continue
        seg_len = BAR * 4 + 2.0
        seg = silence(seg_len)
        for m in notes:
            x = detuned_saw_stack(midi(m), seg_len, voices=3, detune_cents=6, rng=rng)
            mix_into(seg, x * env_ar(samples(seg_len), 2.2, 2.8), 0.0, gain=0.4)
        mix_into(pad, lowpass(seg, 650), i * BAR * 4)
    pad_st = chorus(to_stereo(pad), rate_hz=0.28, depth_ms=6.0, mix=0.35)

    # 莫尔斯呼叫：每段第 2 小节一组 + 乒乓回声（电台回响）
    calls = silence(total)
    for i, f in enumerate(MORSE_FREQS):
        if f is None:
            continue
        mix_into(calls, morse_call(f, rng), i * BAR * 4 + BAR, gain=0.11)
    calls_st = to_stereo(calls) + delay_echo(calls, BEAT * 1.5, feedback=0.4, pingpong=True) * 0.55

    # 静电底：低通噪声 1Hz 慢调制 + 稀疏噼啪颗粒——"频道没关"的持续存在感
    t = np.arange(n) / RATE
    static = lowpass(white(total, rng), 3000) * (0.65 + 0.35 * np.sin(2 * np.pi * t / (LOOP / 48)))
    pops = bandpass(crackle(total, rng, density_hz=3.0, tau=1e9), 800, 4000)  # 密度恒定的偶发噼啪
    static_st = np.stack([static, np.roll(static, 631)], axis=1) * 0.014 + to_stereo(pops) * 0.05

    dry = to_stereo(drone + beats)
    wet = reverb(pad_st + calls_st, mix=0.3, rt=1.8, damp=0.4)
    looped = wrap_loop_tail(dry + wet + static_st, LOOP)
    print(seam_report(looped, "bgm_battle_alt"))
    write_wav(OUT_DIR, "bgm_battle_alt", normalize(looped, -24.5, peak_cap=0.7))


if __name__ == "__main__":
    make_bgm_battle_alt()
