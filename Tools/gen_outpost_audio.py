# 一次性脚本：程序化生成 Outpost 的全部音频资产（纯合成，与"全几何体+程序网格零美术"基调一致）。
# 从项目根运行：python Tools/gen_outpost_audio.py
# 产物落 Assets/Game/Outpost/Res/Audio/（Res 收集器 CollectAll 覆盖，运行时按文件名寻址 Bag.Load<AudioClip>）。
#
# 设计要点：
# - BGM 是可无缝循环的和弦垫（loop 边界处所有分量包络闭合，无爆点）；
# - 火墙循环音 sfx_fire_loop 按整周期构造保证无缝（音量/音高调制在运行时由 AudioSource 做，见 TurretView）；
# - 一次性音效首尾加包络防爆音；整体峰值压在 ~0.6 以内，混响空间留给运行时叠加。
import math
import os
import random
import struct
import wave

RATE = 44100
OUT_DIR = "Assets/Game/Outpost/Res/Audio"
os.makedirs(OUT_DIR, exist_ok=True)


def write_wav(name, samples):
    # 软限幅（tanh）防止叠加超 1.0 的削波爆音
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


def tone(freq, seconds, gain=0.2, attack=0.01, release=0.05, shape="sine", vibrato=0.0):
    """单音：attack/release 线性包络；shape=sine/triangle/saw-ish。"""
    n = int(RATE * seconds)
    out = []
    for i in range(n):
        t = i / RATE
        f = freq * (1.0 + vibrato * math.sin(2 * math.pi * 5.0 * t))
        ph = 2 * math.pi * f * t
        if shape == "triangle":
            v = 2 / math.pi * math.asin(math.sin(ph))
        elif shape == "soft-saw":  # 前三阶谐波近似锯齿，比真锯齿柔和
            v = math.sin(ph) + math.sin(2 * ph) / 2 + math.sin(3 * ph) / 3
            v *= 0.55
        else:
            v = math.sin(ph)
        env = min(1.0, t / attack if attack > 0 else 1.0,
                  (seconds - t) / release if release > 0 else 1.0)
        out.append(gain * env * v)
    return out


def noise_burst(seconds, gain=0.3, lp=0.15, decay_pow=2.0):
    """低通白噪声爆发：lp 越小越闷；指数衰减。"""
    n = int(RATE * seconds)
    out = []
    acc = 0.0
    for i in range(n):
        t = i / n
        acc += lp * (random.uniform(-1, 1) - acc)  # 一阶低通
        out.append(gain * acc * (1.0 - t) ** decay_pow)
    return out


# ── BGM：和弦垫，循环无缝 ───────────────────────────────────────────────
# 手法：每个和弦一段，段内各音符 attack/release 包络在段边界闭合——循环点落在段边界，天然无爆点。

A2, C3, D3, E3, F3, G3 = 110.0, 130.81, 146.83, 164.81, 174.61, 196.0
A3, C4, D4, E4, F4, G4 = 220.0, 261.63, 293.66, 329.63, 349.23, 392.0
A4, C5, E5, G5, B4, D5 = 440.0, 523.25, 659.25, 783.99, 493.88, 587.33


def pad_chord(freqs, seconds, gain):
    """和弦垫：多分量 + 慢速呼吸 LFO，段首尾包络闭合。"""
    n = int(RATE * seconds)
    out = [0.0] * n
    for k, f in enumerate(freqs):
        lfo_rate = 0.23 + 0.11 * k  # 各分量呼吸速率错开，垫子有"活气"
        for i in range(n):
            t = i / RATE
            env = min(1.0, t / 0.8, (seconds - t) / 1.2)  # 慢起慢收，段边界归零
            breathe = 0.8 + 0.2 * math.sin(2 * math.pi * lfo_rate * t + k)
            out[i] += gain * env * breathe * math.sin(2 * math.pi * f * t)
    return out


def make_bgm_title():
    # Am → F → C → G 四和弦，每段 4s，共 16s 循环；深空冷色调。
    prog = [
        [A2, E3, A3, C4, E4],
        [F3, C4, F4, A4 / 2 * 1.0, A3],  # F 大三和弦（A3 做三音低八度填充）
        [C3, G3, C4, E4, G4],
        [G3, D4, G4, B4 / 2 * 1.0, D3],
    ]
    out = []
    for freqs in prog:
        out.extend(pad_chord(freqs, 4.0, 0.055))
    write_wav("bgm_title", out)


def make_bgm_battle():
    # 同进行但紧张化：底鼓脉冲 + 八分音符低音 + 十六分琶音，2s/和弦、8s 循环。
    prog = [
        ([A2, E3], [A4, C5, E5, C5]),
        ([F3, C4], [A4, C5, F4 * 2, C5]),
        ([C3, G3], [G4 * 2 / 2, C5, E5, C5]),
        ([G3, D4], [B4, D5, G5, D5]),
    ]
    seg = 2.0
    out = []
    for bass_notes, arp_notes in prog:
        seg_buf = silence(seg)
        # 八分音符低音脉冲（0.25s 一发）
        step = 0.25
        k = 0
        tpos = 0.0
        while tpos < seg - 1e-6:
            f = bass_notes[k % len(bass_notes)]
            mix_into(seg_buf, tone(f, 0.22, gain=0.16, attack=0.004, release=0.1, shape="soft-saw"), tpos)
            k += 1
            tpos += step
        # 十六分琶音（高一层的驱动感，音量低）
        step = 0.125
        k = 0
        tpos = 0.0
        while tpos < seg - 1e-6:
            f = arp_notes[k % len(arp_notes)]
            mix_into(seg_buf, tone(f, 0.09, gain=0.045, attack=0.003, release=0.05, shape="triangle"), tpos)
            k += 1
            tpos += step
        # 和弦垫铺底
        pad = pad_chord([bass_notes[0], bass_notes[1], bass_notes[0] * 2], seg, 0.03)
        for i in range(len(pad)):
            seg_buf[i] += pad[i]
        out.extend(seg_buf)
    write_wav("bgm_battle", out)


# ── 音效 ────────────────────────────────────────────────────────────────

def make_click():
    # UI 点击：短促高频 blip。
    write_wav("sfx_click", tone(1250, 0.06, gain=0.3, attack=0.002, release=0.04))


def make_upgrade():
    # 选定升级：上行确认双音（E5→A5）。
    out = tone(659.25, 0.1, gain=0.3, attack=0.004, release=0.05)
    out += tone(880.0, 0.16, gain=0.3, attack=0.004, release=0.1)
    write_wav("sfx_upgrade", out)


def make_wave():
    # 新一波开场：双音警报（A4/E5 交替两轮），提示但不刺耳。
    out = []
    for f in (440.0, 659.25, 440.0, 659.25):
        out += tone(f, 0.13, gain=0.22, attack=0.008, release=0.05, shape="triangle")
    write_wav("sfx_wave", out)


def make_explosion():
    # 拦截击毁：闷噪声爆发 + 低频 thump。
    buf = noise_burst(0.3, gain=0.55, lp=0.12, decay_pow=2.2)
    thump = []
    n = int(RATE * 0.18)
    for i in range(n):
        t = i / RATE
        f = 150 * (1 - t / 0.18) + 45  # 低频快速下扫
        thump.append(0.5 * (1 - i / n) ** 1.5 * math.sin(2 * math.pi * f * t))
    mix_into(buf, thump, 0.0)
    write_wav("sfx_explosion", buf)


def make_detonate():
    # 漏怪自爆炸基地（受创聚合窗口到期播）：更重的 boom，低扫更深、噪声更闷更长。
    buf = noise_burst(0.55, gain=0.6, lp=0.07, decay_pow=1.8)
    thump = []
    n = int(RATE * 0.4)
    for i in range(n):
        t = i / RATE
        f = 120 * (1 - t / 0.4) + 32
        thump.append(0.62 * (1 - i / n) ** 1.3 * math.sin(2 * math.pi * f * t))
    mix_into(buf, thump, 0.0)
    write_wav("sfx_detonate", buf)


def make_repair():
    # 波间维修回满：柔和上行琶音 C5→E5→G5。
    out = silence(0.5)
    for i, f in enumerate((523.25, 659.25, 783.99)):
        mix_into(out, tone(f, 0.22, gain=0.2, attack=0.01, release=0.14), i * 0.09)
    write_wav("sfx_repair", out)


def make_defeat():
    # 哨站失守：下行小调三音（A3→F3→D3），沉重收尾。
    out = silence(1.1)
    for i, f in enumerate((220.0, 174.61, 146.83)):
        mix_into(out, tone(f, 0.5, gain=0.26, attack=0.02, release=0.3, shape="soft-saw"), i * 0.28)
    write_wav("sfx_defeat", out)


def make_retreat():
    # 主动撤离（分数落袋）：平稳双音收束（E5→C5，下行但明亮——不是失败）。
    out = tone(659.25, 0.16, gain=0.24, attack=0.008, release=0.08)
    out += tone(523.25, 0.3, gain=0.24, attack=0.008, release=0.22)
    write_wav("sfx_retreat", out)


def make_fire_loop():
    # 火墙循环底噪：55Hz 基频 buzz + 幅度抖动，1s 整周期构造保证无缝循环。
    # 运行时由 TurretView 挂 AudioSource 播放，volume/pitch 随火力热度逐帧调制。
    seconds = 1.0
    n = int(RATE * seconds)
    out = []
    acc = 0.0
    for i in range(n):
        t = i / RATE
        # 基频 55Hz 方波感（基波+3次谐波）；周期数为整数 → 循环无缝
        v = math.sin(2 * math.pi * 55 * t) + 0.4 * math.sin(2 * math.pi * 165 * t)
        # 8Hz 幅度抖动（整周期），读成"机械连发的颤"
        wobble = 0.75 + 0.25 * math.sin(2 * math.pi * 8 * t)
        # 一点低通噪声让声音"脏"些（火药感）；噪声不保证无缝，音量压低到听不出接缝
        acc += 0.2 * (random.uniform(-1, 1) - acc)
        out.append(0.22 * wobble * v + 0.05 * acc)
    write_wav("sfx_fire_loop", out)


random.seed(20260710)  # 噪声可复现：重跑脚本产物逐字节一致
make_bgm_title()
make_bgm_battle()
make_click()
make_upgrade()
make_wave()
make_explosion()
make_detonate()
make_repair()
make_defeat()
make_retreat()
make_fire_loop()
