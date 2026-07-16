# 一次性脚本：程序化生成 Outpost 的全部音频资产（纯合成，与"全几何体+程序网格零美术"基调一致）。
# 从项目根运行：python Tools/gen_outpost_audio.py
# 产物落 Assets/Game/Outpost/Res/Audio/（Res 收集器 CollectAll 覆盖，运行时按文件名寻址 Bag.Load<AudioClip>）。
# DSP 引擎在 Tools/outpost_audio_dsp.py（numpy/scipy：失谐堆叠 / 滤波扫频 / 混响回声 / 立体声 / 循环回绕）。
#
# 2026-07-16 二轮（用户试听反馈"战斗曲和主界面一样舒缓、不搭"）：
# - 战斗曲从"drone+心跳氛围"改成驱动型编排——120BPM 鼓组（底鼓/军鼓/踩镲）+ 八分贝斯 riff +
#   离拍和弦戳 + C 段暗色主题旋律，24 小节 A/B/break/C 四段 48s 循环。**反转 2026-07-10 的
#   "不靠节奏音型"决策**：当年被嫌"吵/欢快"的是高音区快速琶音，病根是音区和音色不是节奏本身；
#   本版鼓/贝斯全在低中频、小调、暗音色——战斗感来自驱动力，不来自亮色。
# - 标题曲保持舒缓但给一条真正的主题旋律（分句呼吸的小调旋律 + 慢琶音），与战斗曲拉开气质差。
# - 响度统一：三首 BGM 同一 RMS（-23dBFS），全部 SFX 同一 RMS（-18dBFS）——资产级响度一致，
#   游戏内混音只由播放侧音量参数负责（OutpostAudioSystem.MusicVolume / 各 PlaySfx volume）。
# 保留的既有决策：全小调（Am/Dm/Em）；打击类指数衰减长尾；循环无缝 = 尾部回绕 + 噪声环形淡接。
#
# 每个资产独立 RNG seed —— 音色可任意增删改序互不影响。战斗曲构建器 build_battle_track 被
# gen_outpost_expansion_audio.py 复用（radio=True 换"军用电台"皮），保证两首战斗曲同一能量骨架。
import numpy as np

from outpost_audio_dsp import (
    RATE, silence, mix_into, to_stereo, midi,
    osc_saw, osc_sine, detuned_saw_stack,
    env_ar, env_exp,
    lowpass, highpass, bandpass, lowpass_sweep,
    delay_echo, reverb, chorus, softclip,
    wrap_loop_tail, loop_crossfade, seam_report,
    white, crackle, normalize, write_wav, samples,
)

OUT_DIR = "Assets/Game/Outpost/Res/Audio"
BGM_RMS = -23.0   # 三首 BGM 统一响度
SFX_RMS = -18.0   # 全部音效统一响度（游戏内相对混音由 PlaySfx volume 参数负责）


def out(name, x, target_rms_db, peak_cap=0.85):
    write_wav(OUT_DIR, name, normalize(x, target_rms_db, peak_cap))


# ── 乐器件 ───────────────────────────────────────────────────────────────

def pad_note(m, sec, rng, detune=6.0, voices=3, attack=1.8, release=2.5):
    """垫音色单音：失谐锯齿堆叠 + 慢起慢收。返回单声道（滤波与空间在总线上做）。"""
    x = detuned_saw_stack(midi(m), sec, voices=voices, detune_cents=detune, rng=rng)
    return x * env_ar(len(x), attack, release)


def bell_note(m, sec, gain=1.0):
    """冷感钟音：基频 + 非谐泛音（2.76×/5.4×，钟类的典型非谐比例），指数长释放。"""
    f = midi(m)
    n = samples(sec)
    x = (osc_sine(f, sec) * env_exp(n, sec * 0.45)
         + 0.35 * osc_sine(f * 2.76, sec) * env_exp(n, sec * 0.22)
         + 0.12 * osc_sine(f * 5.40, sec) * env_exp(n, sec * 0.10))
    return gain * x


def drum_kick(rng):
    """底鼓：快速下扫正弦体 + 高频瞬态 click + 轻过载——结实有"点"，驱动感的地基。"""
    sec = 0.22
    n = samples(sec)
    t = np.arange(n) / RATE
    body = osc_sine(44 + 100 * np.exp(-t * 32), sec) * env_exp(n, 0.075)
    x = softclip(body * 1.2, 1.6)
    click = highpass(white(0.004, rng), 2500) * 0.35
    x[: len(click)] += click
    return x


def drum_snare(rng):
    """军鼓：宽带噪声 + 190Hz 皮膜体。"""
    sec = 0.18
    n = samples(sec)
    noise = bandpass(white(sec, rng), 900, 7500) * env_exp(n, 0.055)
    body = osc_sine(190, sec) * env_exp(n, 0.045)
    return 0.85 * noise + 0.5 * body


def drum_hat(rng):
    """闭镲：高通噪声极短衰减。"""
    n = samples(0.07)
    return highpass(white(0.07, rng), 6800) * env_exp(n, 0.013)


def bass_note(m, sec, rng):
    """合成贝斯单音：失谐锯齿 + 低八度正弦，逐音低通包络（400→150Hz "咬字"）+ 轻过载。"""
    n = samples(sec)
    x = detuned_saw_stack(midi(m), sec, voices=2, detune_cents=5, rng=rng)
    x = x + 0.6 * osc_sine(midi(m) / 2, sec)
    fc = 150 + 300 * np.exp(-np.arange(n) / RATE / 0.06)
    x = lowpass_sweep(x, fc)
    return softclip(x, 1.3) * env_ar(n, 0.004, min(0.06, sec * 0.4))


def stab_chord(notes, sec, rng, band=None):
    """和弦戳：失谐锯齿三和音、快速衰减；band 给定时改窄带（电台皮的"薄"音色）。"""
    x = np.zeros(samples(sec))
    for m in notes:
        x += detuned_saw_stack(midi(m), sec, voices=2, detune_cents=6, rng=rng)
    x = lowpass(x, 1500) if band is None else bandpass(x, *band)
    return x * env_exp(len(x), 0.09)


def lead_note(m, sec, rng):
    """主题旋律音色（战斗曲 C 段）：失谐锯齿过低通——暗色、有分量，不是亮色电子音。"""
    x = detuned_saw_stack(midi(m), sec, voices=3, detune_cents=8, rng=rng)
    x = lowpass(x, 1100)
    return x * env_ar(len(x), 0.02, min(0.3, sec * 0.5))


def title_lead_note(m, sec):
    """标题主题旋律音色：正弦 + 弱二次谐波 + 5Hz 揉音，柔和悠长。"""
    n = samples(sec)
    t = np.arange(n) / RATE
    vib = midi(m) * (1.0 + 0.006 * np.sin(2 * np.pi * 5.0 * t) * np.clip(t / 0.4, 0, 1))
    x = osc_sine(vib, sec) + 0.25 * osc_sine(vib * 2, sec)
    return x * env_ar(n, 0.08, min(0.8, sec * 0.5))


# ── BGM：战斗（48s 立体声循环，120BPM · 24 小节 A/B/break/C）─────────────
# 编排（战斗感来自驱动力，暗色小调防"欢快"）：
#   小节 0-3   A：底鼓 + 八分贝斯 + drone（进场就有推进力）
#   小节 4-11  B：+ 军鼓反拍 + 踩镲 + 离拍和弦戳（Am→Dm）
#   小节 12-15 break：鼓撤到底鼓单击，Em 垫涌起，末小节军鼓滚奏 + 上升器
#   小节 16-23 C：全奏 + 暗色主题旋律，末小节鼓 fill 引回循环头
# 和声：Am×8 → Dm×4 → Em×4(break) → Am×4 → Dm×2 → Em×2 → 回 Am（i-iv-v 自然小调循环）。

BPM = 120.0
STEP = 60.0 / BPM / 4        # 16 分音符 0.125s
BAR = STEP * 16              # 2.0s
BATTLE_BARS = 24
BATTLE_LOOP = BAR * BATTLE_BARS  # 48s

_ROOTS = [33] * 8 + [38] * 4 + [40] * 4 + [33] * 4 + [38] * 2 + [40] * 2  # 每小节贝斯根音（A1/D2/E2）
_DRONE_SEGS = [(0, 8), (8, 4), (12, 4), (16, 4), (20, 2), (22, 2)]         # (起始小节, 小节数)

# 贝斯 riff（16 分步序：步, 相对根音半音, 时值步数）
_BASS_A = [(s, 0, 2) for s in range(0, 16, 2)]                              # A 段：直八分推进
_BASS_B = [(0, 0, 2), (2, 0, 1), (3, 0, 1), (4, 12, 2), (6, 0, 2),
           (8, 0, 2), (10, 3, 2), (12, 0, 2), (14, -2, 2)]                  # B/C 段：riff（八度跳 + 小三度/下二度经过音）

# C 段主题旋律（小节, 步, midi, 时值步数）——A 小调五声、上限 E5，短句有呼吸。
_LEAD = [
    (16, 0, 69, 6), (16, 8, 72, 4), (16, 12, 74, 4),
    (17, 0, 76, 10), (17, 12, 74, 4),
    (18, 0, 72, 6), (18, 8, 69, 4), (18, 12, 67, 4),
    (19, 0, 69, 14),
    (20, 0, 72, 6), (20, 8, 74, 4), (20, 12, 76, 4),
    (21, 0, 76, 10), (21, 12, 74, 4),
    (22, 0, 74, 6), (22, 8, 72, 4), (22, 12, 69, 4),
]

# 电台皮的莫尔斯呼叫（替代主题旋律）：每两小节一组"短-短-长"，两个呼叫音高交替。
_MORSE_BARS = [(16, 880.0), (18, 988.0), (20, 784.0), (22, 880.0)]


def _at(bar, step=0.0):
    return bar * BAR + step * STEP


def build_battle_track(rng, radio=False):
    """战斗曲构建器（主包默认皮 / radio=True 电台皮共用同一能量骨架）。返回已回绕的立体声循环。"""
    total = BATTLE_LOOP + 5.0
    n = samples(total)

    # drone：根音软锯齿（LP 160）+ 五度正弦，按和声分段铺底
    drone = silence(total)
    for start, bars in _DRONE_SEGS:
        seg_len = bars * BAR + 1.2
        root = _ROOTS[start] + (12 if radio else 0)  # 电台皮 drone 高八度=载波感更明显
        x = lowpass(detuned_saw_stack(midi(root), seg_len, voices=2, detune_cents=4, rng=rng), 160)
        mix_into(drone, x * env_ar(samples(seg_len), 0.8, 1.2), _at(start), gain=0.5)
        fifth = osc_sine(midi(root + 7), seg_len) * env_ar(samples(seg_len), 1.0, 1.4)
        mix_into(drone, fifth, _at(start), gain=0.10)

    # 鼓组
    drums = silence(total)
    for bar in range(BATTLE_BARS):
        in_break = 12 <= bar <= 14
        grooving = bar >= 4 and not in_break and bar != 15
        # 底鼓：主拍 1/3；非 break 的奇数小节加一脚 16 分推（step 10）
        kick_steps = [0] if in_break else ([0, 8, 10] if bar % 2 == 1 else [0, 8])
        for s in kick_steps:
            mix_into(drums, drum_kick(rng), _at(bar, s), gain=0.62)
        # 军鼓：反拍 2/4；bar15/23 滚奏（渐强，引入下一段/循环头）
        if grooving or bar == 15:
            if bar in (15, 23):
                start_s = 8 if bar == 15 else 12
                for i, s in enumerate(range(start_s, 16)):
                    mix_into(drums, drum_snare(rng), _at(bar, s), gain=0.16 + 0.04 * i)
            if bar != 15:
                for s in (4, 12):
                    mix_into(drums, drum_snare(rng), _at(bar, s), gain=0.42)
        # 踩镲：八分、强弱交替；电台皮换行军刷点（军鼓噪声更低沉的 brush 感由窄带镲代替）
        if grooving:
            for i, s in enumerate(range(0, 16, 2)):
                mix_into(drums, drum_hat(rng), _at(bar, s), gain=0.30 if i % 2 == 0 else 0.16)

    # 贝斯：A 段直八分、B/C 段 riff；break 静默（把空间让给垫和上升器）
    bass = silence(total)
    for bar in range(BATTLE_BARS):
        if 12 <= bar <= 15:
            continue
        pattern = _BASS_A if bar < 4 else _BASS_B
        for s, off, ln in pattern:
            mix_into(bass, bass_note(_ROOTS[bar] + off + 12, ln * STEP * 0.92, rng), _at(bar, s), gain=0.5)

    # 和弦戳：B/C 段离拍（步 6/14），三和音在 A3 声位；电台皮改窄带"薄"音色
    stabs = silence(total)
    band = (500.0, 2200.0) if radio else None
    for bar in range(BATTLE_BARS):
        if not (4 <= bar <= 11 or 16 <= bar <= 23):
            continue
        r = _ROOTS[bar] + 24
        for s in (6, 14):
            mix_into(stabs, stab_chord((r, r + 3, r + 7), 0.35, rng, band=band), _at(bar, s), gain=0.16)

    # break 垫涌起（Em）：慢起慢收，填住鼓撤走的空间
    pad = silence(total)
    seg = silence(BAR * 4 + 2.0)
    for m in (52, 55, 59):
        mix_into(seg, pad_note(m, BAR * 4 + 2.0, rng, attack=1.6, release=2.0), 0.0, gain=0.4)
    mix_into(pad, lowpass(seg, 900), _at(12))
    pad_st = chorus(to_stereo(pad), rate_hz=0.3, depth_ms=6.0, mix=0.35)

    # 主题层：默认皮=暗色旋律 + 乒乓回声；电台皮=莫尔斯呼叫（同为 C 段的"人声位"）
    theme = silence(total)
    if not radio:
        for bar, s, m, ln in _LEAD:
            mix_into(theme, lead_note(m, ln * STEP * 1.05, rng), _at(bar, s), gain=0.20)
    else:
        for bar, freq in _MORSE_BARS:
            for i, dur in enumerate((0.07, 0.07, 0.2)):
                beep = osc_sine(freq, dur + 0.05) * env_ar(samples(dur + 0.05), 0.004, 0.05)
                mix_into(theme, bandpass(beep, freq * 0.6, freq * 1.8), _at(bar) + i * 0.14, gain=0.30)
    theme_st = to_stereo(theme) + delay_echo(theme, STEP * 3, feedback=0.35, pingpong=True) * 0.5

    # 上升器（bar 14-15）：高通噪声时变低通上扫 + 音量 ^2 渐强，60ms 尾巴淡出（回绕后被 C 段首拍掩蔽）
    riser_len = BAR * 2 + 0.06
    rn = samples(riser_len)
    rt_ = np.arange(rn) / RATE
    fc_r = 500 * (3000 / 500) ** (rt_ / riser_len)
    ramp = np.clip(rt_ / (BAR * 2), 0, 1) ** 2 * np.clip((riser_len - rt_) / 0.06, 0, 1)
    riser = lowpass_sweep(highpass(white(riser_len, rng), 300), fc_r) * ramp
    riser_buf = silence(total)
    mix_into(riser_buf, riser, _at(14), gain=0.15)

    # 电台皮附加层：静电底 + 偶发噼啪（"频道没关"的持续存在感）
    extra_st = np.zeros((n, 2))
    if radio:
        t = np.arange(n) / RATE
        static = lowpass(white(total, rng), 3000) * (0.65 + 0.35 * np.sin(2 * np.pi * 1.0 * t))
        pops = bandpass(crackle(total, rng, density_hz=3.0, tau=1e9), 800, 4000)
        extra_st = np.stack([static, np.roll(static, 631)], axis=1) * 0.012 + to_stereo(pops) * 0.04

    dry = to_stereo(drone + drums + bass + riser_buf)
    wet = reverb(pad_st + theme_st + to_stereo(stabs), mix=0.28, rt=1.8, damp=0.35)
    return wrap_loop_tail(dry + wet + extra_st, BATTLE_LOOP)


def make_bgm_battle():
    rng = np.random.default_rng(202)
    looped = build_battle_track(rng)
    print(seam_report(looped, "bgm_battle"))
    out("bgm_battle", looped, BGM_RMS, peak_cap=0.8)


# ── BGM：标题（48s 立体声循环，60BPM · 6 和弦 + 主题旋律）─────────────────
# 舒缓但有可辨识的主题：失谐垫（低通呼吸）+ 低音根音 + 慢琶音脉动 + 分句旋律（正弦揉音 + 长回声）。

TITLE_LOOP = 48.0
TITLE_CHORDS = [  # (根音 midi, 垫声位)×6，每和弦 8s：Am → Dm → Em → Am → Dm → Am
    (33, [45, 52, 57, 60]),
    (38, [50, 53, 57, 62]),
    (40, [52, 55, 59, 64]),
    (33, [45, 52, 57, 62]),
    (38, [50, 53, 57, 65]),
    (33, [45, 52, 57, 60]),
]
TITLE_MELODY = [  # (时间 s, midi, 时值 s)——A 小调，两句一答一收，间隔留呼吸
    (8.0, 64, 1.5), (9.5, 62, 1.5), (11.0, 60, 2.0), (13.0, 57, 2.8),
    (16.0, 64, 1.5), (17.5, 67, 1.5), (19.0, 64, 2.0), (21.0, 62, 2.8),
    (32.0, 65, 1.5), (33.5, 64, 1.5), (35.0, 62, 2.0), (37.0, 60, 2.8),
    (40.0, 57, 4.0),
]
TITLE_BELLS = [(4.0, 64), (26.0, 62), (44.5, 57)]  # 极稀疏的冷钟点缀


def make_bgm_title():
    rng = np.random.default_rng(101)
    total = TITLE_LOOP + 6.0
    n = samples(total)
    t = np.arange(n) / RATE

    # 垫：各和弦重叠 2s 交叉淡接；总线低通呼吸扫频（1 循环/圈）
    pad = silence(total)
    for i, (_, notes) in enumerate(TITLE_CHORDS):
        seg = silence(10.0)
        for k, m in enumerate(notes):
            mix_into(seg, pad_note(m, 10.0, rng, attack=2.2, release=3.0), 0.0, gain=0.5 if k == 0 else 0.36)
        mix_into(pad, seg, i * 8.0)
    fc = 650 + 350 * np.sin(2 * np.pi * t / TITLE_LOOP)
    pad = lowpass_sweep(pad, fc)
    pad_st = chorus(to_stereo(pad), rate_hz=0.25, depth_ms=7.0, mix=0.45)

    # 低音根音
    sub = silence(total)
    for i, (root, _) in enumerate(TITLE_CHORDS):
        x = osc_sine(midi(root), 8.8) + 0.2 * osc_sine(midi(root) * 2, 8.8)
        mix_into(sub, x * env_ar(samples(8.8), 1.2, 1.6), i * 8.0, gain=0.30)

    # 慢琶音脉动：每拍（1s）一颗和弦音软拨，[低,中,高,中] 循环——给氛围一个安静的心率
    arp = silence(total)
    for i, (_, notes) in enumerate(TITLE_CHORDS):
        order = [notes[0], notes[1], notes[3], notes[2]]
        for b in range(8):
            m = order[b % 4]
            pluck = (osc_sine(midi(m), 1.4) + 0.3 * osc_sine(midi(m) * 2, 1.4)) * env_exp(samples(1.4), 0.35)
            mix_into(arp, pluck, i * 8.0 + b * 1.0, gain=0.10)

    # 主题旋律 + 长回声（0.75s 乒乓）
    mel = silence(total)
    for at, m, dur in TITLE_MELODY:
        mix_into(mel, title_lead_note(m, dur), at, gain=0.22)
    mel = lowpass(mel, 1800)
    mel_st = to_stereo(mel) + delay_echo(mel, 0.75, feedback=0.35, pingpong=True) * 0.6

    # 冷钟点缀 + 风底
    bells = silence(total)
    for at, m in TITLE_BELLS:
        mix_into(bells, bell_note(m, 3.0), at, gain=0.12)
    bells_st = to_stereo(bells) + delay_echo(bells, 0.66, feedback=0.45, pingpong=True) * 0.7
    air = bandpass(white(total, rng), 500, 1600) * (0.6 + 0.4 * np.sin(2 * np.pi * t / TITLE_LOOP + 1.7))
    air_st = np.stack([air, np.roll(air, 977)], axis=1)

    mix = pad_st + to_stereo(sub + arp) + mel_st + bells_st + air_st * 0.012
    mix = reverb(mix, mix=0.36, rt=2.8, damp=0.4)
    looped = wrap_loop_tail(mix, TITLE_LOOP)
    print(seam_report(looped, "bgm_title"))
    out("bgm_title", looped, BGM_RMS, peak_cap=0.7)


# ── 音效（全部单声道；分层=瞬态+体腔+余韵，指数长尾；统一 RMS）───────────

def make_click():
    # UI 点击：2ms 高频瞬态 + 1.15k 短鸣 + 2.3k 弱泛音——"实体按键"两层感。
    rng = np.random.default_rng(11)
    n = samples(0.07)
    x = np.zeros(n)
    x[: samples(0.004)] += highpass(white(0.004, rng), 3000) * 0.3
    x += osc_sine(1150, 0.07) * env_exp(n, 0.016) * 0.8
    x += osc_sine(2300, 0.07) * env_exp(n, 0.008) * 0.25
    out("sfx_click", x, SFX_RMS)


def make_upgrade():
    # 选定升级：A4→E5 双拨弦（基频+泛音各自衰减）+ 高频微光 + 一次短回声——确认感、不甜腻。
    rng = np.random.default_rng(12)
    x = silence(0.55)
    for at, m, g in ((0.0, 69, 0.8), (0.10, 76, 1.0)):
        f = midi(m)
        sec = 0.4
        nn = samples(sec)
        note = (osc_sine(f, sec) * env_exp(nn, 0.12)
                + 0.4 * osc_sine(f * 2, sec) * env_exp(nn, 0.06)
                + 0.15 * osc_sine(f * 3.01, sec) * env_exp(nn, 0.035))
        mix_into(x, note, at, gain=g * 0.6)
    shimmer = bandpass(white(0.3, rng), 4500, 9000) * env_exp(samples(0.3), 0.07)
    mix_into(x, shimmer, 0.1, gain=0.05)
    x += delay_echo(x, 0.12, feedback=0.3, repeats=2)
    out("sfx_upgrade", x, SFX_RMS)


def make_wave():
    # 新一波开场：暗色警报——A3+E4 失谐方波感双音两次涌起（LP 1.2k），"哨站警笛"而非"电子哔哔"。
    x = silence(1.05)
    for at in (0.0, 0.5):
        sec = 0.42
        nn = samples(sec)
        swell = np.sin(np.pi * np.clip(np.arange(nn) / nn, 0, 1)) ** 1.5
        tone_mix = np.zeros(nn)
        for f, g in ((midi(57), 1.0), (midi(57) * 1.005, 0.7), (midi(64), 0.45)):
            tone_mix += g * np.sign(osc_sine(f, sec))
        mix_into(x, lowpass(tone_mix, 1200) * swell, at, gain=0.24)
    out("sfx_wave", x, SFX_RMS)


def make_explosion():
    # 拦截击毁：四层——低频下扫体 + 噪声爆膛（LP 下扫）+ 碎裂噼啪 + 50Hz 余鸣，轻微过载胶合。
    rng = np.random.default_rng(13)
    sec = 0.75
    n = samples(sec)
    t = np.arange(n) / RATE
    body = osc_sine(38 + 120 * np.exp(-t * 16), sec) * env_exp(n, 0.13)
    burst = lowpass_sweep(white(sec, rng), 2800 * np.exp(-t * 7) + 250) * env_exp(n, 0.16)
    snap = bandpass(crackle(sec, rng, density_hz=70, tau=0.12), 1200, 5000) * env_exp(n, 0.2)
    hum = osc_sine(52, sec) * env_exp(n, 0.28)
    x = softclip(0.9 * body + 0.55 * burst + 0.3 * snap + 0.12 * hum, drive=1.5)
    out("sfx_explosion", x, SFX_RMS)


def make_detonate():
    # 漏怪自爆炸基地（受创聚合窗口到期播）：更深更长的 boom——下扫至 30Hz、40Hz 震腔殿后。
    rng = np.random.default_rng(14)
    sec = 1.15
    n = samples(sec)
    t = np.arange(n) / RATE
    body = osc_sine(30 + 95 * np.exp(-t * 11), sec) * env_exp(n, 0.2)
    burst = lowpass_sweep(white(sec, rng), 2000 * np.exp(-t * 6) + 160) * env_exp(n, 0.22)
    snap = bandpass(crackle(sec, rng, density_hz=50, tau=0.18), 900, 4000) * env_exp(n, 0.3)
    cavity = osc_sine(40, sec) * env_exp(n, 0.45)
    x = softclip(1.0 * body + 0.5 * burst + 0.24 * snap + 0.18 * cavity, drive=1.7)
    out("sfx_detonate", x, SFX_RMS)


def make_repair():
    # 波间维修回满：暖色上行琶音 A3-C4-E4-A4（软起、长余韵）+ 轻合唱回声——"系统恢复"而非"得分奖励"。
    x = silence(0.85)
    for i, m in enumerate((57, 60, 64, 69)):
        f = midi(m)
        sec = 0.45
        nn = samples(sec)
        note = (osc_sine(f, sec) + 0.3 * osc_sine(f * 2, sec)) * env_ar(nn, 0.012, 0.3) * env_exp(nn, 0.22)
        mix_into(x, note, i * 0.09, gain=0.5)
    x += delay_echo(x, 0.16, feedback=0.35, repeats=3)
    out("sfx_repair", x, SFX_RMS)


def make_defeat():
    # 哨站失守：下行暗垫三音 A3→E3→C3 相互叠入 + 1.1s 处低频终锤——沉重、有终局感。
    rng = np.random.default_rng(15)
    x = silence(1.7)
    for i, m in enumerate((57, 52, 48)):
        note = lowpass(detuned_saw_stack(midi(m), 0.9, voices=3, detune_cents=9, rng=rng), 900)
        note *= env_ar(samples(0.9), 0.03, 0.55)
        mix_into(x, note, i * 0.3, gain=0.4)
    thud_n = samples(0.5)
    thud_t = np.arange(thud_n) / RATE
    thud = osc_sine(33 + 27 * np.exp(-thud_t * 22), 0.5) * env_exp(thud_n, 0.22)
    mix_into(x, thud, 1.05, gain=0.8)
    st = reverb(x, mix=0.3, rt=2.2)
    out("sfx_defeat", st.mean(axis=1), SFX_RMS)


def make_retreat():
    # 主动撤离（分数落袋）：E5→A4 纯五度下行钟音——收束、平稳、明亮但不欢庆（不是失败也不是胜利）。
    x = silence(0.9)
    mix_into(x, bell_note(76, 0.5), 0.0, gain=0.55)
    mix_into(x, bell_note(69, 0.7), 0.16, gain=0.65)
    x += delay_echo(x, 0.14, feedback=0.25, repeats=2)
    out("sfx_retreat", x, SFX_RMS)


def make_shot():
    # 单发炮声（低射速段主角）：炮口爆膛 crack + 165→62Hz 体腔下扫 + 金属簧片瞬态 + 机械短尾。
    # 高射速段由 sfx_fire_loop 接棒（>15Hz 重复事件人耳听成连续音）——限流与交叉见 director/TurretView。
    rng = np.random.default_rng(16)
    sec = 0.25
    n = samples(sec)
    t = np.arange(n) / RATE
    crack = highpass(white(sec, rng), 1500) * env_exp(n, 0.009)
    body = osc_sine(62 + 103 * np.exp(-t * 20), sec) * env_exp(n, 0.055)
    ring = osc_sine(1400, sec) * env_exp(n, 0.014)
    mech = lowpass(white(sec, rng), 500) * env_exp(n, 0.05)
    x = softclip(0.5 * crack + 0.95 * body + 0.12 * ring + 0.2 * mech, drive=1.4)
    out("sfx_shot", x, SFX_RMS)


def make_impact():
    # 击中未击毁（弹着"叮"）：非谐泛音簇（1×/1.51×/2.76×/4.07×）快衰减 + 3ms 瞬态——装甲上的金属声。
    rng = np.random.default_rng(17)
    sec = 0.16
    n = samples(sec)
    f0 = 1680.0
    x = np.zeros(n)
    for ratio, g, tau in ((1.0, 1.0, 0.045), (1.51, 0.6, 0.032), (2.76, 0.4, 0.022), (4.07, 0.2, 0.015)):
        x += g * osc_sine(f0 * ratio, sec) * env_exp(n, tau)
    x[: samples(0.003)] += highpass(white(0.003, rng), 2500) * 0.8
    out("sfx_impact", x, SFX_RMS)


def make_fire_loop():
    # 火墙循环底噪（2s 无缝）：52Hz 锯齿 buzz（整 104 周期）+ 26Hz 亚低频 + 宽带"怒吼"噪声（环形淡接）
    # + 13Hz 机械纹波 AM（整 26 周期）。运行时 TurretView 随热度调 volume/pitch（0.85~1.3）。
    # ⚠ 周期信号过滤波器会带启动瞬态（首尾滤波状态不同=接缝跳变）：tile 两圈取第二圈=全程稳态、逐位周期。
    rng = np.random.default_rng(18)
    loop = 2.0
    n = samples(loop)
    t = np.arange(n) / RATE
    buzz = lowpass(np.tile(osc_saw(52.0, loop), 2), 750)[n:]
    sub = osc_sine(26.0, loop)
    roar = bandpass(white(loop + 0.56, rng), 130, 950)[samples(0.5):]
    roar = loop_crossfade(roar, 0.06)[:n]
    rattle = bandpass(white(loop + 0.56, rng), 1800, 3600)[samples(0.5):]
    rattle = loop_crossfade(rattle, 0.06)[:n]
    am = 0.72 + 0.28 * np.sin(2 * np.pi * 13.0 * t)
    x = (0.6 * buzz + 0.25 * sub + 0.5 * roar + 0.08 * rattle) * am
    print(seam_report(x, "sfx_fire_loop"))
    out("sfx_fire_loop", x, SFX_RMS)


def make_servo_loop():
    # 炮塔回转伺服循环（1s 无缝）：92Hz 电机基波 + 非谐 196/365Hz 部分音（整数频率=整周期无接缝，
    # 比例≈2.13×/3.97× 保留"真电机不完美谐波"观感）+ 13Hz 齿轮纹波 + 3Hz 慢晃 + 电刷噪声（环形淡接）。
    rng = np.random.default_rng(19)
    loop = 1.0
    n = samples(loop)
    t = np.arange(n) / RATE
    hum = (osc_sine(92, loop) + 0.5 * osc_sine(196, loop) + 0.2 * osc_sine(365, loop))
    ripple = (0.72 + 0.28 * np.sin(2 * np.pi * 13 * t)) * (0.9 + 0.1 * np.sin(2 * np.pi * 3 * t))
    brush = lowpass(white(loop + 0.55, rng), 1300)[samples(0.5):]
    brush = loop_crossfade(brush, 0.05)[:n]
    x = 0.55 * hum * ripple + 0.12 * brush
    print(seam_report(x, "sfx_servo_loop"))
    out("sfx_servo_loop", x, SFX_RMS)


if __name__ == "__main__":
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
    make_shot()
    make_impact()
    make_fire_loop()
    make_servo_loop()
