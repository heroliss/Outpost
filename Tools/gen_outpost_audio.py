# 一次性脚本：程序化生成 Outpost 的全部音频资产（纯合成，与"全几何体+程序网格零美术"基调一致）。
# 从项目根运行：python Tools/gen_outpost_audio.py
# 产物落 Assets/Game/Outpost/Res/Audio/（Res 收集器 CollectAll 覆盖，运行时按文件名寻址 Bag.Load<AudioClip>）。
# DSP 引擎在 Tools/outpost_audio_dsp.py（numpy/scipy：失谐堆叠 / 滤波扫频 / 混响回声 / 立体声 / 循环回绕）。
#
# 2026-07-16 全面重写（用户反馈旧版"嘀嘀嘀太单调"）。旧版病根与对应解法：
# - 裸正弦直出无效果链 → 失谐锯齿堆叠 + 低通扫频 + Schroeder 混响 + 合唱，音色有厚度与空间；
# - BGM 循环仅 8s/16s → 战斗 48s（20 小节 A/B/收束 三段编排）、标题 40s（8 和弦 + 钟音动机），单声道 → 立体声；
# - 循环无缝从"段边界包络归零"升级为"尾部回绕"（混响尾叠回开头，循环点无静默感）。
# 保留的既有决策：全小调（Am/Em/Dm，2026-07-10"吵/过于欢快"反馈后定）；战斗紧张感靠低频压迫
# 与心跳律动、不靠快节奏亮色音型；打击类一律指数衰减长尾（防"被掐断"感）。
# 响度对齐旧版逐资产 RMS 基准（游戏内几十处音量常数不必重调）。
#
# 每个资产独立 RNG seed —— 音色可任意增删改序互不影响（旧版共享 RNG 的"只能末尾追加"约束作废）。
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


def out(name, x, target_rms_db, peak_cap=0.85):
    write_wav(OUT_DIR, name, normalize(x, target_rms_db, peak_cap))


# ── 乐器件（BGM 共用）────────────────────────────────────────────────────

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


def kick(f_hi=90.0, f_lo=44.0, sec=0.16, tau=0.09):
    """低频心跳单击：指数下扫正弦（相位累积，无啁啾），闷而不炸。"""
    n = samples(sec)
    t = np.arange(n) / RATE
    f = f_lo + (f_hi - f_lo) * np.exp(-t * 22)
    return osc_sine(f, sec) * env_exp(n, tau)


def sub_note(m, sec, attack=0.01, tau=0.18):
    """低音脉冲：正弦 + 一点 2 次谐波（小喇叭上也听得到轮廓）。"""
    f = midi(m)
    n = samples(sec)
    x = osc_sine(f, sec) + 0.25 * osc_sine(f * 2, sec)
    return x * env_ar(n, attack, sec * 0.6) * env_exp(n, tau)


def tick_noise(sec, rng, lo=2800.0, hi=4800.0, tau=0.02):
    """金属质感节拍点：窄带噪声极短衰减（不含音高 = 不添旋律亮色）。"""
    x = bandpass(white(sec, rng), lo, hi)
    return x * env_exp(samples(sec), tau)


# ── BGM：标题（40s 立体声循环，深空冷氛围）───────────────────────────────
# 结构：8 和弦 × 5s（小调进行两轮、第二轮换声位），三层——失谐垫（低通呼吸扫频）+
# 低音根音 + 稀疏钟音动机（乒乓回声）+ 极低噪声"风底"。音量刻意压低：标题页只要"深空里有点声音"。

TITLE_LOOP = 40.0
TITLE_CHORDS = [  # (根音 midi, 垫声位 midi 列表)；全小调，低中音区为主
    (33, [45, 52, 57, 60]),          # Am
    (40, [40, 47, 55, 59]),          # Em
    (38, [38, 50, 53, 57]),          # Dm
    (33, [45, 52, 57, 60]),          # Am
    (33, [45, 52, 57, 62]),          # Am(add4) 第二轮换色
    (38, [38, 45, 53, 57]),          # Dm 低位
    (40, [40, 47, 52, 55]),          # Em 收拢
    (33, [45, 52, 57, 60]),          # Am 归位
]
TITLE_BELLS = [  # (时间 s, midi)——A 小调五声内的稀疏动机，避开大调明亮音
    (2.5, 64), (7.5, 59), (12.5, 62), (17.5, 57),
    (22.5, 60), (27.5, 64), (32.5, 62), (36.5, 57),
]


def make_bgm_title():
    rng = np.random.default_rng(101)
    total = TITLE_LOOP + 6.0  # 多渲染 6s 尾巴（混响/长释放），wrap_loop_tail 叠回开头
    n = samples(total)
    t = np.arange(n) / RATE

    # 垫：各和弦重叠 1.5s 交叉淡接；总线上做一次整曲低通呼吸扫频（1 循环/圈，接缝处相位连续）
    pad = silence(total)
    for i, (_, notes) in enumerate(TITLE_CHORDS):
        seg = silence(6.5)
        for k, m in enumerate(notes):
            mix_into(seg, pad_note(m, 6.5, rng), 0.0, gain=0.5 if k == 0 else 0.38)
        mix_into(pad, seg, i * 5.0)
    fc = 650 + 350 * np.sin(2 * np.pi * t / TITLE_LOOP)
    pad = lowpass_sweep(pad, fc)
    pad_st = chorus(to_stereo(pad), rate_hz=0.25, depth_ms=7.0, mix=0.45)

    # 低音根音：正弦 + 弱 2 次谐波，跟随和弦
    sub = silence(total)
    for i, (root, _) in enumerate(TITLE_CHORDS):
        x = osc_sine(midi(root), 5.6) + 0.2 * osc_sine(midi(root) * 2, 5.6)
        mix_into(sub, x * env_ar(samples(5.6), 1.0, 1.4), i * 5.0, gain=0.30)

    # 钟音动机 + 乒乓回声（0.66s、反馈 0.45）
    bells = silence(total)
    for at, m in TITLE_BELLS:
        mix_into(bells, bell_note(m, 3.0), at, gain=0.16)
    bells_st = to_stereo(bells) + delay_echo(bells, 0.66, feedback=0.45, pingpong=True) * 0.7

    # 风底：宽带噪声开窄带缓扫（1 循环/圈），存在感极低
    air = bandpass(white(total, rng), 500, 1600)
    air = air * (0.6 + 0.4 * np.sin(2 * np.pi * t / TITLE_LOOP + 1.7))
    air_st = np.stack([air, np.roll(air, 977)], axis=1)  # 右声道错位=去相关的伪立体声

    mix = pad_st * 1.0 + to_stereo(sub) + bells_st + air_st * 0.012
    mix = reverb(mix, mix=0.38, rt=2.8, damp=0.4)
    looped = wrap_loop_tail(mix, TITLE_LOOP)
    print(seam_report(looped, "bgm_title"))
    out("bgm_title", looped, target_rms_db=-28.5, peak_cap=0.6)


# ── BGM：战斗（48s 立体声循环，100BPM · 20 小节 A/B/收束）────────────────
# 编排（紧张感=低频压迫+心跳律动，不靠快节奏亮色音型——既有反馈红线）：
#   小节 1-4   A 段：低频 drone + 心跳双击（旧曲的身份元素保留）
#   小节 5-8   Em：加暗垫
#   小节 9-16  B 段（Dm→Am）：加离拍低音脉冲 + 金属节拍点——密度上来但仍在低中频
#   小节 17-20 收束：剥回 drone+心跳，末 2 小节噪声上升器引回循环头
# 和声每 4 小节一换：Am → Em → Dm → Am → (A pedal 收束)。

BPM = 100.0
BEAT = 60.0 / BPM            # 0.6s
BAR = BEAT * 4               # 2.4s
BATTLE_LOOP = BAR * 20       # 48s
BATTLE_SECTIONS = [          # (根音 midi, 垫声位（中低区）)
    (33, None),              # A 段无垫
    (40, [47, 52, 55]),      # Em
    (38, [50, 53, 57]),      # Dm
    (33, [52, 57, 60]),      # Am
    (33, None),              # 收束
]
BATTLE_PINGS = [64, 67, 65, 64, None]  # 每段第 2 小节一颗冷 ping（E4/G4/F4/E4）


def make_bgm_battle():
    rng = np.random.default_rng(202)
    total = BATTLE_LOOP + 5.0
    n = samples(total)
    t = np.arange(n) / RATE

    # drone：根音软锯齿（LP 160）+ 五度正弦，整段铺底
    drone = silence(total)
    for i, (root, _) in enumerate(BATTLE_SECTIONS):
        seg_len = BAR * 4 + 1.2
        x = lowpass(detuned_saw_stack(midi(root), seg_len, voices=2, detune_cents=4, rng=rng), 160)
        x = x * env_ar(samples(seg_len), 1.2, 1.6)
        mix_into(drone, x, i * BAR * 4, gain=0.5)
        fifth = osc_sine(midi(root + 7), seg_len) * env_ar(samples(seg_len), 1.5, 1.8)
        mix_into(drone, fifth, i * BAR * 4, gain=0.10)

    # 心跳：每小节 主击 + 0.18s 弱补拍；隔小节第 3 拍幽灵击。段首（每 4 小节）加一颗更低的重音。
    beats = silence(total)
    for bar in range(20):
        at = bar * BAR
        mix_into(beats, kick(), at, gain=0.62)
        mix_into(beats, kick(), at + 0.18, gain=0.34)
        if bar % 2 == 1:
            mix_into(beats, kick(f_hi=70, f_lo=40), at + 2.5 * BEAT, gain=0.22)
        if bar % 4 == 0:
            mix_into(beats, kick(f_hi=64, f_lo=34, sec=0.3, tau=0.16), at, gain=0.30)
    # 收束末小节：八分心跳滚奏渐强，落回循环头
    for k in range(8):
        mix_into(beats, kick(), 19 * BAR + k * BEAT / 2, gain=0.12 + 0.03 * k)

    # 离拍低音脉冲（B 段 9-16 小节）：根音高八度，落在 1.5 / 3 拍——推进感而不亮色
    pulse = silence(total)
    for bar in range(8, 16):
        root = BATTLE_SECTIONS[bar // 4][0]
        at = bar * BAR
        mix_into(pulse, sub_note(root + 12, 0.35), at + 1.5 * BEAT, gain=0.30)
        mix_into(pulse, sub_note(root + 12, 0.35), at + 3.0 * BEAT, gain=0.24)
    pulse = lowpass(pulse, 300)

    # 暗垫（5-16 小节）：中低声位、LP 800，慢起——和声运动主要靠它
    pad = silence(total)
    for i, (_, notes) in enumerate(BATTLE_SECTIONS):
        if notes is None:
            continue
        seg_len = BAR * 4 + 2.0
        seg = silence(seg_len)
        for m in notes:
            mix_into(seg, pad_note(m, seg_len, rng, attack=2.2, release=2.8), 0.0, gain=0.4)
        mix_into(pad, lowpass(seg, 800), i * BAR * 4)
    pad_st = chorus(to_stereo(pad), rate_hz=0.3, depth_ms=6.0, mix=0.35)

    # 金属节拍点（B 段）：2/4 拍窄带噪声 tick，左右交替声像
    ticks = np.zeros((n, 2))
    for bar in range(8, 16):
        for beat, pan in ((1, -0.5), (3, 0.5)):
            x = tick_noise(0.05, rng) * rng.uniform(0.7, 1.0)
            mix_into(ticks, to_stereo(x, pan), bar * BAR + beat * BEAT, gain=0.10)

    # 冷 ping：每段第 2 小节一颗 + 回声
    pings = silence(total)
    for i, m in enumerate(BATTLE_PINGS):
        if m is None:
            continue
        mix_into(pings, bell_note(m, 2.0), i * BAR * 4 + BAR, gain=0.09)
    pings_st = to_stereo(pings) + delay_echo(pings, BEAT * 1.5, feedback=0.4, pingpong=True) * 0.6

    # 上升器（末 2 小节）：高通噪声过"截止 500→3000 上扫"的时变低通 + 音量 ^2 渐强，引回循环头。
    # 末端多渲染 60ms 快速淡出——经 wrap_loop_tail 叠回循环头，落点被小节头重锤掩蔽且无硬切咔哒。
    riser_len = BAR * 2 + 0.06
    rn = samples(riser_len)
    rt_ = np.arange(rn) / RATE
    fc_r = 500 * (3000 / 500) ** (rt_ / riser_len)
    ramp = np.clip(rt_ / (BAR * 2), 0, 1) ** 2
    ramp *= np.clip((riser_len - rt_) / 0.06, 0, 1)
    riser = lowpass_sweep(highpass(white(riser_len, rng), 300), fc_r) * ramp
    riser_buf = silence(total)
    mix_into(riser_buf, riser, 18 * BAR, gain=0.16)

    dry_center = to_stereo(drone + beats * 1.0 + pulse) + to_stereo(riser_buf)
    wet = reverb(pad_st + pings_st + ticks, mix=0.32, rt=2.0, damp=0.35)
    mix = dry_center + wet
    looped = wrap_loop_tail(mix, BATTLE_LOOP)
    print(seam_report(looped, "bgm_battle"))
    out("bgm_battle", looped, target_rms_db=-24.0, peak_cap=0.7)


# ── 音效（全部单声道；分层=瞬态+体腔+余韵，指数长尾）─────────────────────

def make_click():
    # UI 点击：2ms 高频瞬态 + 1.15k 短鸣 + 2.3k 弱泛音——"实体按键"两层感。
    rng = np.random.default_rng(11)
    n = samples(0.07)
    x = np.zeros(n)
    x[: samples(0.004)] += highpass(white(0.004, rng), 3000) * 0.3
    x += osc_sine(1150, 0.07) * env_exp(n, 0.016) * 0.8
    x += osc_sine(2300, 0.07) * env_exp(n, 0.008) * 0.25
    out("sfx_click", x, -16.5)


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
    out("sfx_upgrade", x, -16.1)


def make_wave():
    # 新一波开场：暗色警报——A3+E4 失谐方波感双音两次涌起（LP 1.2k），"哨站警笛"而非"电子哔哔"。
    x = silence(1.05)
    for at in (0.0, 0.5):
        sec = 0.42
        nn = samples(sec)
        swell = np.sin(np.pi * np.clip(np.arange(nn) / nn, 0, 1)) ** 1.5  # 涌起-回落包络
        tone_mix = np.zeros(nn)
        for f, g in ((midi(57), 1.0), (midi(57) * 1.005, 0.7), (midi(64), 0.45)):
            tone_mix += g * np.sign(osc_sine(f, sec))  # 方波（后级 LP 磨圆）
        mix_into(x, lowpass(tone_mix, 1200) * swell, at, gain=0.24)
    out("sfx_wave", x, -19.7)


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
    out("sfx_explosion", x, -19.6)


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
    out("sfx_detonate", x, -18.4)


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
    out("sfx_repair", x, -18.7)


def make_defeat():
    # 哨站失守：下行暗垫三音 A3→E3→C3 相互叠入 + 1.1s 处低频终锤——沉重、有终局感。
    rng = np.random.default_rng(15)
    x = silence(1.7)
    for i, m in enumerate((57, 52, 48)):
        note = lowpass(detuned_saw_stack(midi(m), 0.9, voices=3, detune_cents=9, rng=rng), 900)
        note *= env_ar(samples(0.9), 0.03, 0.55)
        mix_into(x, note, i * 0.3, gain=0.4)
    mix_into(x, kick(f_hi=60, f_lo=33, sec=0.5, tau=0.22), 1.05, gain=0.8)
    st = reverb(x, mix=0.3, rt=2.2)
    out("sfx_defeat", st.mean(axis=1), -19.9)


def make_retreat():
    # 主动撤离（分数落袋）：E5→A4 纯五度下行钟音——收束、平稳、明亮但不欢庆（不是失败也不是胜利）。
    x = silence(0.9)
    mix_into(x, bell_note(76, 0.5), 0.0, gain=0.55)
    mix_into(x, bell_note(69, 0.7), 0.16, gain=0.65)
    x += delay_echo(x, 0.14, feedback=0.25, repeats=2)
    out("sfx_retreat", x, -18.3)


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
    out("sfx_shot", x, -18.4)


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
    out("sfx_impact", x * 0.4, -22.6)


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
    # 噪声层：滤波后先丢弃 0.5s 瞬态头，再环形淡接闭合循环
    roar = bandpass(white(loop + 0.56, rng), 130, 950)[samples(0.5):]
    roar = loop_crossfade(roar, 0.06)[:n]
    rattle = bandpass(white(loop + 0.56, rng), 1800, 3600)[samples(0.5):]
    rattle = loop_crossfade(rattle, 0.06)[:n]
    am = 0.72 + 0.28 * np.sin(2 * np.pi * 13.0 * t)
    x = (0.6 * buzz + 0.25 * sub + 0.5 * roar + 0.08 * rattle) * am
    print(seam_report(x, "sfx_fire_loop"))
    out("sfx_fire_loop", x, -18.0)


def make_servo_loop():
    # 炮塔回转伺服循环（1s 无缝）：92Hz 电机基波 + 非谐 2.13×/3.97× 部分音（真电机的不完美谐波）
    # + 13Hz 齿轮纹波 + 3Hz 慢"晃"（都取整周期）+ 电刷噪声（环形淡接）。运行时随角速度调 volume/pitch。
    rng = np.random.default_rng(19)
    loop = 1.0
    n = samples(loop)
    t = np.arange(n) / RATE
    # 非谐部分音取整数频率（92/196/365Hz≈1×/2.13×/3.97×）：1s 循环内全部整周期，接缝无跳变
    hum = (osc_sine(92, loop) + 0.5 * osc_sine(196, loop) + 0.2 * osc_sine(365, loop))
    ripple = (0.72 + 0.28 * np.sin(2 * np.pi * 13 * t)) * (0.9 + 0.1 * np.sin(2 * np.pi * 3 * t))
    brush = lowpass(white(loop + 0.55, rng), 1300)[samples(0.5):]
    brush = loop_crossfade(brush, 0.05)[:n]
    x = 0.55 * hum * ripple + 0.12 * brush
    print(seam_report(x, "sfx_servo_loop"))
    out("sfx_servo_loop", x, -20.7)


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
