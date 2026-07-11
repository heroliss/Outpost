# 一次性脚本：生成 OutpostExpansionPackage（扩展包）的音频资产——「增援电台」战斗 BGM 变体。
# 从项目根运行：python Tools/gen_outpost_expansion_audio.py
# 产物落 Assets/Game/Outpost/ResExpansion/Audio/（扩展包收集器覆盖，跨包按文件名寻址）。
#
# 刻意与 Tools/gen_outpost_audio.py 相互独立：主脚本在 import 时即生成全部主包音频、且各音色共享同一
# RNG 顺序（新音色只能往末尾追加）——扩展内容单独一个脚本、单独一个 seed，互不牵连。
#
# 音乐设计：与主战斗曲同一副小调骨架（Am→Em→Dm→Am，同一气质、切换不突兀），换一副「军用电台」的皮——
# 失谐双振荡 drone（拍频更厚）+ 慢速行军底拍 + 每段一组莫尔斯风冷音标（"电台呼叫"）+ 低噪声静电底。
# 循环无缝手法同主脚本：全部分量在段边界包络闭合。
import math
import os
import random
import struct
import wave

RATE = 44100
OUT_DIR = "Assets/Game/Outpost/ResExpansion/Audio"
os.makedirs(OUT_DIR, exist_ok=True)


def write_wav(name, samples):
    path = f"{OUT_DIR}/{name}.wav"
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(
            struct.pack("<h", int(math.tanh(s) * 32767 * 0.98)) for s in samples))
    print("written:", path, f"{len(samples) / RATE:.2f}s")


def silence(seconds):
    return [0.0] * int(RATE * seconds)


def mix_into(dst, src, offset_sec):
    ofs = int(RATE * offset_sec)
    need = ofs + len(src) - len(dst)
    if need > 0:
        dst.extend([0.0] * need)
    for i, s in enumerate(src):
        dst[ofs + i] += s


def tone(freq, seconds, gain=0.2, attack=0.01, release=0.05, shape="sine"):
    n = int(RATE * seconds)
    out = []
    for i in range(n):
        t = i / RATE
        ph = 2 * math.pi * freq * t
        if shape == "soft-saw":  # 前三阶谐波近似锯齿，比真锯齿柔和
            v = (math.sin(ph) + math.sin(2 * ph) / 2 + math.sin(3 * ph) / 3) * 0.55
        else:
            v = math.sin(ph)
        env = min(1.0, t / attack if attack > 0 else 1.0,
                  (seconds - t) / release if release > 0 else 1.0)
        out.append(gain * env * v)
    return out


def kick(freq_hi=90.0, freq_lo=48.0, seconds=0.14, gain=0.3):
    # 低频行军底拍：短促下扫正弦（同主曲心跳的手法，节奏感更"步进"）。
    n = int(RATE * seconds)
    out = []
    for i in range(n):
        t = i / RATE
        f = freq_hi + (freq_lo - freq_hi) * (t / seconds)
        out.append(gain * (1 - i / n) ** 1.6 * math.sin(2 * math.pi * f * t))
    return out


def tick(seconds=0.04, gain=0.1):
    # 弱拍军鼓刷感：极短低通噪声。
    n = int(RATE * seconds)
    out = []
    acc = 0.0
    for i in range(n):
        acc += 0.45 * (random.uniform(-1, 1) - acc)
        out.append(gain * acc * (1 - i / n) ** 2)
    return out


A1, E2, D2 = 55.0, 82.41, 73.42
A2, E3, G3, A3, C4, D4, F3 = 110.0, 164.81, 196.0, 220.0, 261.63, 293.66, 174.61
E5, G5 = 659.25, 783.99

MINOR_PROG = [
    ([A2, E3, A3, C4], A1),   # Am
    ([E2 * 2, G3, E3], E2),   # Em
    ([D4 / 2, A3, F3], D2),   # Dm
    ([A2, E3, A3, C4], A1),   # Am 回归
]


def detuned_drone(root, seconds, gain):
    # 失谐双振荡：root 与 root+0.7Hz 叠加产生 ~0.7Hz 拍频——比单 drone 厚、有"载波"感。
    n = int(RATE * seconds)
    out = [0.0] * n
    for f in (root, root + 0.7):
        for i in range(n):
            t = i / RATE
            env = min(1.0, t / 0.3, (seconds - t) / 0.45)
            v = (math.sin(2 * math.pi * f * t)
                 + 0.5 * math.sin(2 * math.pi * 2 * f * t)
                 + 0.33 * math.sin(2 * math.pi * 3 * f * t)) * 0.55
            out[i] += gain * env * v * 0.5
    return out


def static_bed(seconds, gain=0.02):
    # 电台静电底：低通噪声 + 1Hz 慢速幅度调制；段边界包络闭合保循环无缝。
    n = int(RATE * seconds)
    out = []
    acc = 0.0
    for i in range(n):
        t = i / RATE
        env = min(1.0, t / 0.4, (seconds - t) / 0.4)
        am = 0.7 + 0.3 * math.sin(2 * math.pi * 1.0 * t)
        acc += 0.12 * (random.uniform(-1, 1) - acc)
        out.append(gain * env * am * acc)
    return out


def morse(freq, offset, buf):
    # 每段一组「电台呼叫」：短-短-长 三点冷音标，音量克制（点缀不抢戏）。
    for i, dur in enumerate((0.07, 0.07, 0.2)):
        mix_into(buf, tone(freq, dur, gain=0.055, attack=0.004, release=0.04, shape="sine"),
                 offset + i * 0.14)


def make_bgm_battle_alt():
    seg = 2.0
    pings = [E5, G5, E5, G5 / 2]
    out = []
    for idx, (chord, root) in enumerate(MINOR_PROG):
        seg_buf = silence(seg)
        # 失谐 drone 铺底（根音低八度）
        mix_into(seg_buf, detuned_drone(root, seg, 0.075), 0.0)
        # 和弦垫（很低的存在感，只为不空）
        for k, f in enumerate(chord):
            comp = tone(f, seg, gain=0.018, attack=1.0, release=1.2)
            mix_into(seg_buf, comp, 0.0)
        # 行军底拍：主拍每 1.0s + 0.5s 弱刷
        tpos = 0.0
        while tpos < seg - 0.2:
            mix_into(seg_buf, kick(gain=0.26), tpos)
            mix_into(seg_buf, tick(), tpos + 0.5)
            tpos += 1.0
        # 段中一组莫尔斯呼叫
        morse(pings[idx], 0.9, seg_buf)
        # 静电底
        mix_into(seg_buf, static_bed(seg), 0.0)
        out.extend(seg_buf[:int(RATE * seg)])  # morse 尾部不越段（越段会破坏循环边界闭合）
        # 截断可能被 mix_into 撑长的缓冲，保证段长精确、循环点对齐
    write_wav("bgm_battle_alt", out)


random.seed(20260711)  # 噪声可复现：重跑脚本产物逐字节一致
make_bgm_battle_alt()
