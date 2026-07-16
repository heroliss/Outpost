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
# 2026-07-16 三轮（用户反馈"射击/爆炸闷、像敲桌子；电台皮听不出差别；BGM 想要立体声层次"）：
# - 打击类 SFX 补高频层：爆膛 crack 提早提亮 + 4~9kHz sizzle/碎裂层——同 RMS 下低频占优的声音
#   听感更闷更小（等响度曲线），"结实"靠低频、"清脆"靠高频，两头都要有。
# - BGM 真立体声摆位：此前文件虽是双声道但主体是"双单声道"（同信号进左右）；现在踩镲/和弦戳/
#   琶音/钟声做等功率声像摆位，鼓/贝斯/drone 守中央（低频守中是混音惯例——立体声低频相位问题+能量分散）。
# - 电台皮识别度：莫尔斯呼叫提前到 A 段/break（原先只在 32s 后的 C 段，前半首和默认皮几乎无差别），
#   静电底/噼啪增益上调——切换开关应在数秒内可辨。
#
# 2026-07-16 四轮（用户反馈"击毁像敲铁皮无爆炸感；机炮偏小、单发缺出膛厚重感、连发反而比单发小；
# 受创音与击毁音分不开；BGM 低频偏高"）：
# - 拦截爆炸重做成"导弹空爆"：加低通噪声慢衰减的轰隆余韵 + 尾段短混响——爆炸与"敲击"的区别
#   一半在衰减尾巴上；去掉金属 ring 层（弹着"叮"才是金属声的地界）。
# - 单发炮声加 200~900Hz 报告层（等响度曲线的敏感区，"厚重感"主要住在这里）+ 低通轰鸣尾。
# - 火墙循环从"稳态炉膛轰鸣"重做成 25 发/秒连发脉冲串：同 RMS 下稳态噪声听感远小于瞬态串，
#   这是"连发听着比单发小"的主因（另一半在游戏侧交叉曲线，见 BattleDirectorSystem/TurretView）。
# - 受创重音（sfx_detonate）与拦截空爆拉开性格：受创=沉/暗/装甲应力呻吟，空爆=亮劈裂/散/轰隆尾。
# - BGM 低频回收：总线一阶低架削 ~3.5dB（RMS 归一自动把能量还给中高频，不改编曲）。
#
# 2026-07-16 五轮（用户要求"单发与连发听感一致、物理拟真低速到高速的连发"）：
# - 火墙从单循环换成**多档同源烘焙**（赛车引擎音按 RPM 分档采样的同一范式）：
#   抽出共享的出膛瞬态生成器 shot_transient——单发 sfx_shot 与五档连发循环（16/32/64/128/256
#   发/秒）字面共享同一配方，音色 DNA 一致；运行时按真实射速选相邻两档交叉淡变 + 档内小幅变速
#   精确对齐射速（TurretView.SetFireWall）。
# - 物理依据：速射炮的音色不随射速变，变的只是重复率——低于 ~15 发/秒是离散炮响，越过 20~30
#   发/秒脉冲串融合成有音高的连续音（基频=射速；密集阵 75 发/秒的"BRRRT"即 75Hz 蜂鸣）。
#   单循环宽域变速做不到（重采样把音色一起搬走）；各档原生射速烘焙让尾巴叠加、谐波融合在
#   离线求和时物理发生，档内 ±1 档带宽的变速音色形变可忽略。
# - 每发用**全长**瞬态（含完整轰鸣尾）直接求和，不做任何压尾：脉冲串的长期平均频谱=单发频谱
#   ×梳状采样，质心天然跨档不变（与单发同族）；高射速下尾巴 30+ 层叠加出的怒吼底、调制深度
#   随射速变浅（离散→融合）都是物理结果，不是调出来的。首版曾"压尾模拟掩蔽"——错：掩蔽是
#   感知现象，能量物理上仍在，压尾等于删掉住在长尾里的低频体量（实测质心飙到 6.7kHz）。
#
# 2026-07-16 六轮（用户反馈"大量敌人被摧毁音效却很少"）：
# - 爆炸限流从"超额丢弃"改成"方位聚合"（游戏侧 BattleDirectorSystem）：放行速率不变
#   （~9 声/秒、voice 预算不动），但每声代表其方位扇区自上次以来的全部击杀——音量=能量和
#   开方（不相干声源功率相加）、聚合越大音高越沉；没有击杀再被静默吞掉。
# - 新增战场轰鸣底床 sfx_rumble：空爆瞬态（与 sfx_explosion 同源，配方抽出 explosion_transient）
#   在随机时刻不相干叠加成循环，运行时音量跟随击杀率、位置滑向击杀能量声心。与火墙档位组是
#   同一物理故事的正反面：炮口串相干（同一门炮锁相重复→梳状谱蜂鸣，须分档烘焙），战场爆炸
#   不相干（各自独立的时刻/位置/反射路径→无音高的连续怒吼，一条循环 × 音量跟随即可）。
#   整体一阶低通回收高频——几十层宽带 crack 不相干堆积会成嘶声地毯（五轮教训的爆炸版），
#   且"远方的战场轰鸣"经空气吸收本就没高频。
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


def stab_chord(notes, sec, rng, band=None, spread=0.5):
    """和弦戳：失谐锯齿三和音、快速衰减，和弦音按 spread 在声场内展开（低音→高音从一侧到另一侧，
    spread 取负镜像）——立体声宽度来自声部摆位而非事后加宽。band 给定时改窄带（电台皮的"薄"音色）。"""
    n = samples(sec)
    out = np.zeros((n, 2))
    for k, m in enumerate(notes):
        pan = spread * (2 * k / max(len(notes) - 1, 1) - 1)
        out += to_stereo(detuned_saw_stack(midi(m), sec, voices=2, detune_cents=6, rng=rng), pan)
    out = lowpass(out, 1500) if band is None else bandpass(out, *band)
    return out * env_exp(n, 0.09)[:, None]


def lead_note(m, sec, rng):
    """主题旋律音色（战斗曲 C 段）：失谐锯齿过低通——暗色、有分量，不是亮色电子音。
    三个失谐声部左/中/右展开（超锯齿的经典宽度手法：失谐拍频在两耳间流动）。返回立体声。"""
    n = samples(sec)
    out = np.zeros((n, 2))
    for cents, pan in ((-8.0, -0.4), (0.0, 0.0), (8.0, 0.4)):
        out += to_stereo(osc_saw(midi(m) * 2 ** (cents / 1200), sec, phase0=rng.random()), pan)
    out = lowpass(out / 3, 1100)
    return out * env_ar(n, 0.02, min(0.3, sec * 0.5))[:, None]


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

# 电台皮的莫尔斯呼叫：每组"短-短-长"，呼叫音高交替。开场 A 段与 break 也有呼叫——
# 电台皮的辨识度必须在数秒内建立（此前只在 32s 后的 C 段出现，前半首与默认皮几乎无差别）。
_MORSE_BARS = [(0, 880.0), (2, 988.0), (13, 740.0), (16, 880.0), (18, 988.0), (20, 784.0), (22, 880.0)]


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

    # 鼓组：底鼓/军鼓守中央（低频+骨架），踩镲进独立立体声总线做声像摆位（高频件拉宽声场）
    drums = silence(total)
    hats_st = silence(total, stereo=True)
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
        # 踩镲：八分、强弱交替，强拍偏左弱拍偏右（左右摆是最经典的镲组宽度手法）
        if grooving:
            for i, s in enumerate(range(0, 16, 2)):
                pan = -0.55 if i % 2 == 0 else 0.55
                mix_into(hats_st, to_stereo(drum_hat(rng), pan), _at(bar, s), gain=0.30 if i % 2 == 0 else 0.16)

    # 贝斯：A 段直八分、B/C 段 riff；break 静默（把空间让给垫和上升器）
    bass = silence(total)
    for bar in range(BATTLE_BARS):
        if 12 <= bar <= 15:
            continue
        pattern = _BASS_A if bar < 4 else _BASS_B
        for s, off, ln in pattern:
            mix_into(bass, bass_note(_ROOTS[bar] + off + 12, ln * STEP * 0.92, rng), _at(bar, s), gain=0.5)

    # 和弦戳：B/C 段离拍（步 6/14），三和音在 A3 声位，左右交替摆位（离拍件在两侧与中央鼓组错开）；
    # 电台皮改窄带"薄"音色
    stabs = silence(total, stereo=True)
    band = (500.0, 2200.0) if radio else None
    for bar in range(BATTLE_BARS):
        if not (4 <= bar <= 11 or 16 <= bar <= 23):
            continue
        r = _ROOTS[bar] + 24
        for s, spread in ((6, -0.5), (14, 0.5)):  # 两个离拍互为镜像：声部展开方向左右交替
            mix_into(stabs, stab_chord((r, r + 3, r + 7), 0.35, rng, band=band, spread=spread), _at(bar, s), gain=0.16)

    # break 垫涌起（Em）：慢起慢收，填住鼓撤走的空间
    pad = silence(total)
    seg = silence(BAR * 4 + 2.0)
    for m in (52, 55, 59):
        mix_into(seg, pad_note(m, BAR * 4 + 2.0, rng, attack=1.6, release=2.0), 0.0, gain=0.4)
    mix_into(pad, lowpass(seg, 900), _at(12))
    pad_st = chorus(to_stereo(pad), rate_hz=0.3, depth_ms=8.0, mix=0.5)

    # 主题层：默认皮=暗色旋律（声部立体展开）+ 乒乓回声；电台皮=莫尔斯呼叫（点源守中央）。
    # 乒乓回声路径要求单声道输入，主信号与回声 send 分开累积
    theme = silence(total, stereo=True)
    theme_send = silence(total)
    if not radio:
        for bar, s, m, ln in _LEAD:
            note = lead_note(m, ln * STEP * 1.05, rng)
            mix_into(theme, note, _at(bar, s), gain=0.20)
            mix_into(theme_send, note.mean(axis=1), _at(bar, s), gain=0.20)
    else:
        for bar, freq in _MORSE_BARS:
            for i, dur in enumerate((0.07, 0.07, 0.2)):
                beep = osc_sine(freq, dur + 0.05) * env_ar(samples(dur + 0.05), 0.004, 0.05)
                beep = bandpass(beep, freq * 0.6, freq * 1.8)
                mix_into(theme, to_stereo(beep), _at(bar) + i * 0.14, gain=0.30)
                mix_into(theme_send, beep, _at(bar) + i * 0.14, gain=0.30)
    theme_st = theme + delay_echo(theme_send, STEP * 3, feedback=0.35, pingpong=True) * 0.5

    # 上升器（bar 14-15）：高通噪声时变低通上扫 + 音量 ^2 渐强，60ms 尾巴淡出（回绕后被 C 段首拍掩蔽）
    riser_len = BAR * 2 + 0.06
    rn = samples(riser_len)
    rt_ = np.arange(rn) / RATE
    fc_r = 500 * (3000 / 500) ** (rt_ / riser_len)
    ramp = np.clip(rt_ / (BAR * 2), 0, 1) ** 2 * np.clip((riser_len - rt_) / 0.06, 0, 1)
    riser = lowpass_sweep(highpass(white(riser_len, rng), 300), fc_r) * ramp
    riser_buf = silence(total)
    mix_into(riser_buf, riser, _at(14), gain=0.15)

    # 电台皮附加层：静电底 + 偶发噼啪（"频道没关"的持续存在感）——增益给到可辨级别，
    # 它与莫尔斯呼叫共同构成电台皮的第一识别特征
    extra_st = np.zeros((n, 2))
    if radio:
        t = np.arange(n) / RATE
        static = lowpass(white(total, rng), 3000) * (0.65 + 0.35 * np.sin(2 * np.pi * 1.0 * t))
        pops = bandpass(crackle(total, rng, density_hz=3.0, tau=1e9), 800, 4000)
        extra_st = np.stack([static, np.roll(static, 631)], axis=1) * 0.028 + to_stereo(pops) * 0.08

    # 鼓+贝斯过短房间混响（低 mix、短 rt 保打点）：给中央骨架一点空间——否则 dry 总线是纯"双单声道"，
    # 两耳信号完全相同，戴耳机听像贴在头中央
    room = reverb(to_stereo(drums + bass), mix=0.14, rt=0.9, damp=0.5)
    dry = to_stereo(drone + riser_buf) + room + hats_st
    wet = reverb(pad_st + theme_st + stabs, mix=0.28, rt=1.8, damp=0.35)
    mix = dry + wet + extra_st
    # 低频回收：一阶低架削 ~3.5dB（x - g·LP1 是平滑的快速低架，一阶相移小无梳状感）。
    # RMS 归一会把削掉的能量还给中高频——频谱重心轻轻上移、总响度不变、编曲不动。
    mix = mix - 0.35 * lowpass(mix, 130, order=1)
    return wrap_loop_tail(mix, BATTLE_LOOP)


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

    # 慢琶音脉动：每拍（1s）一颗和弦音软拨，[低,中,高,中] 循环——给氛围一个安静的心率。
    # 声像随音高左→右画弧（低音靠左、高音靠右，钢琴摆位惯例），是标题曲立体声宽度的主来源
    arp = silence(total, stereo=True)
    arp_pans = [-0.45, -0.15, 0.45, 0.15]
    for i, (_, notes) in enumerate(TITLE_CHORDS):
        order = [notes[0], notes[1], notes[3], notes[2]]
        for b in range(8):
            m = order[b % 4]
            pluck = (osc_sine(midi(m), 1.4) + 0.3 * osc_sine(midi(m) * 2, 1.4)) * env_exp(samples(1.4), 0.35)
            mix_into(arp, to_stereo(pluck, arp_pans[b % 4]), i * 8.0 + b * 1.0, gain=0.10)

    # 主题旋律 + 长回声（0.75s 乒乓）
    mel = silence(total)
    for at, m, dur in TITLE_MELODY:
        mix_into(mel, title_lead_note(m, dur), at, gain=0.22)
    mel = lowpass(mel, 1800)
    mel_st = to_stereo(mel) + delay_echo(mel, 0.75, feedback=0.35, pingpong=True) * 0.6

    # 冷钟点缀（左右交替落点）+ 风底；钟的乒乓回声喂单声道混和（pingpong 路径要求 mono 输入）
    bells_mono = silence(total)
    bells = silence(total, stereo=True)
    for k, (at, m) in enumerate(TITLE_BELLS):
        note = bell_note(m, 3.0)
        mix_into(bells_mono, note, at, gain=0.12)
        mix_into(bells, to_stereo(note, -0.4 if k % 2 == 0 else 0.4), at, gain=0.12)
    bells_st = bells + delay_echo(bells_mono, 0.66, feedback=0.45, pingpong=True) * 0.7
    air = bandpass(white(total, rng), 500, 1600) * (0.6 + 0.4 * np.sin(2 * np.pi * t / TITLE_LOOP + 1.7))
    air_st = np.stack([air, np.roll(air, 977)], axis=1)

    mix = pad_st + to_stereo(sub) + arp + mel_st + bells_st + air_st * 0.012
    mix = reverb(mix, mix=0.36, rt=2.8, damp=0.4)
    mix = mix - 0.28 * lowpass(mix, 110, order=1)  # 低频回收（同战斗曲的低架手法，幅度略轻）
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


def explosion_transient(rng, sec=1.15):
    """空爆瞬态——单发拦截爆炸 sfx_explosion 与战场轰鸣底床 sfx_rumble 共用的同源配方
    （同 shot_transient 之于火墙档位组：单点与群体听感一致的根基）。
    分层：起爆劈裂 + 低频冲击体（165→30Hz 下扫）+ 爆膛下扫 + 轰隆余韵（低通噪声慢衰减）+ 碎片噼啪。
    爆炸与"敲击"的听感区别一半在衰减尾巴：前 20ms 宽带冲击给"炸"、之后 0.5s+ 的低频轰隆给
    "爆炸的体量"——没有尾巴就是敲铁皮。"""
    n = samples(sec)
    t = np.arange(n) / RATE
    crack = highpass(white(sec, rng), 2500) * env_exp(n, 0.012)
    body = osc_sine(30 + 135 * np.exp(-t * 13), sec) * env_exp(n, 0.17)
    burst = lowpass_sweep(white(sec, rng), 7000 * np.exp(-t * 8) + 250) * env_exp(n, 0.11)
    rumble = lowpass(white(sec, rng), 380) * env_exp(n, 0.4)
    debris = bandpass(crackle(sec, rng, density_hz=45, tau=0.28), 1200, 6500) * env_exp(n, 0.32)
    return softclip(0.55 * crack + 0.9 * body + 0.6 * burst + 0.55 * rumble + 0.4 * debris, drive=1.5)


def make_explosion():
    # 拦截击毁（导弹空爆）：同源瞬态过短混响并回单声道（3D 位置播放要求 mono）。
    x = reverb(explosion_transient(np.random.default_rng(13)), mix=0.16, rt=1.0, damp=0.5).mean(axis=1)
    out("sfx_explosion", x, SFX_RMS)


def make_kill_rumble():
    # 战场轰鸣底床（4s 无缝循环）：海量击杀的质感层——每秒 ~22 记全长空爆瞬态在**随机时刻**
    # 不相干叠加。与火墙档位组是同一物理故事的反面：这里逐记全新随机 + 随机定时（火墙融合档的
    # "冻结波形 + 脉冲网格"在这里反而是错的——各次爆炸本就互不相干），叠加没有"基频=速率"，
    # 密度只影响调制深度（>20 记/秒已基本融合成稳态怒吼），故不需要档位组，运行时纯靠音量
    # 跟随击杀率（BattleDirectorSystem.UpdateKillAudio）。
    # 频谱塑形分两带：暗色怒吼体（一阶低通 1k）+ 中频噼啪纹理带（700~3500 带通）。首版只留
    # 一阶低通 1.4k——99% 能量落在 400Hz 以下，物理上"远方轰鸣"没错，但试听反馈证明它在真实
    # 混音里不可闻：火墙是 -13.5dBFS 资产、2D 满音量的持续蜂鸣，底床在可闻频段比它低 ~10dB，
    # 等于不存在。中频带是"远处炸点此起彼伏"的可闻载体（等响度曲线的敏感区），仍严切 >4k 防
    # 嘶声地毯（五轮教训的爆炸版）。
    # RMS 压在 SFX_RMS-0.5：仍略让合爆 boom（瞬态才是"炸"），但不能再让出 2dB——那正是被埋的量。
    rng = np.random.default_rng(23)
    loop = 4.0
    imp = silence(loop + 1.3)
    for _ in range(int(loop * 22)):
        mix_into(imp, explosion_transient(rng), rng.random() * loop, gain=0.4 + 0.6 * rng.random())
    # 中频带 3×：瞬态源 96% 功率住在 400Hz 以下，带通取出的中频绝对量极小，小增益等于没加
    #（首调 0.5× 实测占比纹丝不动）；3× 后中频 ~10% 功率——占比小但落在等响度敏感区，听感上立得住。
    x = lowpass(imp, 1000, order=1) + 3.0 * bandpass(imp, 700, 3500)
    x = wrap_loop_tail(x, loop)
    print(seam_report(x, "sfx_rumble"))
    out("sfx_rumble", x, SFX_RMS - 0.5)


def make_detonate():
    # 哨站受创重音（受创聚合窗口到期播，跟随主观镜头而非敌人位置——语义是"我们被砸了"）：
    # 深冲击下扫 + 低通砸击 + 装甲结构应力呻吟（低频非谐金属部分音簇、缓慢音高晃动）+ 40Hz 震腔殿后。
    # 性格与拦截空爆刻意拉开：空爆=亮劈裂/宽带散开/轰隆尾，受创=沉/暗/金属应力——受击方 vs 击毁方，
    # 玩家闭眼也要能分清"是我在挨打"。
    rng = np.random.default_rng(14)
    sec = 1.2
    n = samples(sec)
    t = np.arange(n) / RATE
    body = osc_sine(26 + 90 * np.exp(-t * 9), sec) * env_exp(n, 0.2)
    thud = lowpass(white(sec, rng), 320) * env_exp(n, 0.06)
    groan = np.zeros(n)
    for f, g, tau in ((82, 1.0, 0.5), (147, 0.65, 0.38), (233, 0.45, 0.28), (341, 0.3, 0.2)):
        groan += g * osc_sine(f * (1 + 0.004 * np.sin(2 * np.pi * 1.3 * t)), sec) * env_exp(n, tau)
    crack = highpass(white(sec, rng), 1800) * env_exp(n, 0.01)
    cavity = osc_sine(40, sec) * env_exp(n, 0.5)
    x = softclip(0.95 * body + 0.55 * thud + 0.5 * groan + 0.35 * crack + 0.18 * cavity, drive=1.6)
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


def shot_transient(rng, sec=0.5):
    """出膛瞬态——单发 sfx_shot 与火墙各档连发循环共用的同源配方（"单发与连发听感一致"的根基）。
    分层：出膛 crack + 高频 sizzle + 低频冲击体（170→45Hz 下扫）+ 200~900Hz 报告层 + 低通轰鸣尾。
    "厚重感"主要住在报告层（等响度曲线的敏感区，胸腔感）和轰鸣尾（炮声在开阔地的余韵）；
    只有低频体腔+短瞬态就是干瘪的"砰"（鞭炮），crack/sizzle 保清脆、报告层+尾巴给分量。
    连发烘焙也用全长版直接求和（不压尾）：低频体量住在长尾里，删尾即变亮变薄（见文件头五轮注）。"""
    n = samples(sec)
    t = np.arange(n) / RATE
    crack = highpass(white(sec, rng), 2200) * env_exp(n, 0.012)
    sizz = bandpass(white(sec, rng), 3500, 9500) * env_exp(n, 0.03)
    body = osc_sine(45 + 125 * np.exp(-t * 15), sec) * env_exp(n, 0.075)
    punch = bandpass(white(sec, rng), 200, 900) * env_exp(n, 0.04)
    boom_tail = lowpass(white(sec, rng), 500) * env_exp(n, 0.13)
    # 配比原则：crack/sizzle 保"清脆"（质心目标 ~1000Hz，别掉回 3 位数变闷响），
    # body/punch/tail 给"出膛分量"（胸腔感+余韵）——两头都要，偏哪头都会收到试听反馈。
    return softclip(0.78 * crack + 0.35 * sizz + 0.78 * body + 0.7 * punch + 0.3 * boom_tail, drive=1.4)


def make_shot():
    # 单发炮声（低射速段主角）：同源瞬态的全长版（0.5s 完整轰鸣尾）。
    # 高射速段由 sfx_fire_* 档位组接棒——接棒带与选档见 director/TurretView。
    out("sfx_shot", shot_transient(np.random.default_rng(16), 0.5), SFX_RMS)


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


# 火墙档位组的原生射速（发/秒）：2 的幂间隔——相邻档在 log 域等距，运行时 log 三角权重
# 交叉淡变时任意射速恰好落在两档之内（TurretView.SetFireWall 与此表必须一致）。
# 覆盖 8~256 发/秒；8 发/秒以下是逐发单响 sfx_shot 的地界（物理上就是离散炮响）。
FIRE_GEAR_RATES = (16.0, 32.0, 64.0, 128.0, 256.0)


def make_fire_gears():
    # 火墙档位组（各 2s 无缝）：每档在**原生射速**下烘焙的连发脉冲串，逐发就是全长 shot_transient
    # ——档位循环字面上=「每秒 N 发 sfx_shot 的预渲染」，与单发层的接棒天然同音色；
    # 高档的尾巴 30+ 层跨发叠加出怒吼底、脉冲融合成蜂鸣（基频=射速）都在求和时物理发生
    # （64 发/秒以上的"BRRRT"正是密集阵/加特林的真实声学）。
    # 融合区（≥64 发/秒）用**冻结波形**：真炮每发的压力波近乎相同，脉冲串因此相干重复——能量
    # 聚在射速的谐波梳上，这正是蜂鸣音高的来源。逐发全新白噪会把"发间差异"夸大成完全不相干的
    # 噪声：宽带 crack 层（占白噪功率 91%）功率随发数线性堆积，高射速下堆成嘶声地毯、抹掉谐波
    # （实测质心从 900 飙到 7.3kHz）。离散区（≤32 发/秒）相反：逐发可分辨，波形全新 + 定时微
    # 抖动（±0.8% 周期）才不是"缝纫机"；此时 crack 重叠数 <1，不相干堆积可忽略。
    # 为什么是脉冲串不是稳态轰鸣：同 RMS 下稳态噪声的听感远小于瞬态串（人耳对瞬态敏感）——
    # 曾经的"炉膛轰鸣"版是"连发听着反而比单发小"的主因；瞬态密度即响度。
    # 循环闭合：整 N 发/圈、首发钉在 0（接缝落在脉冲网格上）、尾巴经 wrap_loop_tail 叠回头部。
    # 融合档补「燃气怒吼底床」：周期脉冲串的频谱物理上不存在基频以下的能量（梳状谱无低梳齿），
    # 单发 90% 的能量住在 200Hz 以下，故纯相干求和在 256 发/秒只剩 crack 高频梳齿（又亮又薄）。
    # 真实高速连发的低频来自**不锁相**成分：枪口燃气射流的湍流怒吼 + 每发反射路径各异的环境混响
    # ——它们填满梳齿之间与基频以下。底床用与单发同带的低频噪声（boom/punch 频带），能量随
    # 射速增长（怒吼功率 ∝ 发数）；离散档不加（单发自带的轰鸣尾就是它的"混响"）。
    loop = 2.0
    shot_len = 0.5
    for rate in FIRE_GEAR_RATES:
        rng = np.random.default_rng(20 + int(rate))
        period = 1.0 / rate
        fused = rate >= 64.0
        frozen = shot_transient(rng, shot_len) if fused else None
        imp = silence(loop + shot_len + 0.1)
        for i in range(int(loop * rate)):
            wave_i = frozen if fused else shot_transient(rng, shot_len)
            jitter = 0.0 if fused or i == 0 else (rng.random() - 0.5) * 0.016 * period
            mix_into(imp, wave_i, i * period + jitter, gain=0.9 + 0.2 * rng.random())
        x = wrap_loop_tail(imp, loop)
        if fused:
            n = samples(loop)
            bed = 0.9 * lowpass(white(loop + 0.56, rng), 450) + 0.45 * bandpass(white(loop + 0.56, rng), 200, 900)
            bed = loop_crossfade(bed[samples(0.5):], 0.06)[:n]
            beta = 0.55 * np.sqrt(rate / 64.0)  # 底床/脉冲串的 RMS 比：随射速 ∝√发数 增长
            x = x + bed * (beta * np.sqrt((x ** 2).mean()) / np.sqrt((bed ** 2).mean()))
            # 最高档梳齿彩票修正：256Hz 基频以上只剩 crack 高频梳齿（低频层的能量全被基频以下
            # 对消吃掉），质心飙到 3 倍家族值——一阶高架削拉回同族亮度，档间交叉淡变才不突变。
            # 亮度随射速温和递升是保留的（RPM 上行的锋利感是有效反馈），削的是"电钻嘶鸣"级偏亮。
            if rate >= 256.0:
                x = 0.45 * x + 0.55 * lowpass(x, 2000, order=1)
        name = f"sfx_fire_{int(rate):03d}"
        print(seam_report(x, name))
        # 响度随射速的增长烘进资产（+1.25dB/档，全组 +5dB）——刻意偏离"全 SFX 同 RMS"契约：
        # ① 物理是功率 ∝ 发数（每翻倍 +3dB，全给 +12dB 太猛，压半）；② 融合档调制变浅，同 RMS
        # 下听感更小（"稳态噪声 vs 瞬态串"教训的跨档版），热 RMS 是感知补偿；③ 运行时
        # AudioSource.volume 上限 1.0，火墙已顶着 0.9 播，增长没法只靠运行时标量给（试听实测
        # 全放运行时导致"高射速反而变小"的中段音量谷）。峰值实测 256 档 0.82 < 0.85 cap。
        gear_db = 1.25 * list(FIRE_GEAR_RATES).index(rate)
        out(name, x, SFX_RMS + gear_db)


def make_servo_loop():
    # 炮塔回转伺服循环（1s 无缝）：92Hz 电机基波 + 非谐 196/365Hz 部分音（整数频率=整周期无接缝，
    # 比例≈2.13×/3.97× 保留"真电机不完美谐波"观感）+ 736/1472Hz 齿轮啮合啸声 + 13Hz 齿轮纹波
    # + 3Hz 慢晃 + 电刷噪声（环形淡接）。啸声层是可听性的关键：92Hz 基波在等响度曲线上天然"显小声"，
    # 中高频啸声让伺服音在混音里立得住，且随游戏侧 pitch 调制（0.8~1.4×）扫出"电机变速"感。
    rng = np.random.default_rng(19)
    loop = 1.0
    n = samples(loop)
    t = np.arange(n) / RATE
    hum = (osc_sine(92, loop) + 0.5 * osc_sine(196, loop) + 0.2 * osc_sine(365, loop))
    whine = (osc_sine(736, loop) + 0.5 * osc_sine(1472, loop)) * (0.8 + 0.2 * np.sin(2 * np.pi * 13 * t))
    ripple = (0.72 + 0.28 * np.sin(2 * np.pi * 13 * t)) * (0.9 + 0.1 * np.sin(2 * np.pi * 3 * t))
    brush = lowpass(white(loop + 0.55, rng), 2200)[samples(0.5):]
    brush = loop_crossfade(brush, 0.05)[:n]
    x = 0.5 * hum * ripple + 0.16 * whine + 0.12 * brush
    print(seam_report(x, "sfx_servo_loop"))
    out("sfx_servo_loop", x, SFX_RMS)


if __name__ == "__main__":
    make_bgm_title()
    make_bgm_battle()
    make_click()
    make_upgrade()
    make_wave()
    make_explosion()
    make_kill_rumble()
    make_detonate()
    make_repair()
    make_defeat()
    make_retreat()
    make_shot()
    make_impact()
    make_fire_gears()
    make_servo_loop()
