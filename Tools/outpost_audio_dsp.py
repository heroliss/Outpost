# Outpost 音频合成的共享 DSP 库（numpy/scipy）。被 gen_outpost_audio.py / gen_outpost_expansion_audio.py 引用。
#
# 与旧版（纯标准库逐样本 for 循环）的关键差别：
# - 全 numpy 向量化 + scipy 滤波器：能负担得起"多振荡失谐 + 滤波扫频 + 混响/回声 + 立体声"这套真实效果链；
# - 每个资产独立 RNG seed（np.random.default_rng(seed)）——不再共享全局 RNG 顺序，
#   音色可任意增删改序、互不影响，旧脚本"新音色只能往末尾追加"的约束就此作废；
# - 循环无缝改用"尾部回绕"（wrap_loop_tail）：混响/回声/长释放的尾巴叠回开头，
#   循环点两侧是同一段声音的自然延续——比"段边界包络归零"高一级（后者循环点必然有一瞬静默感）。
#
# 约定：所有音频缓冲为 float64；单声道 shape=(n,)，立体声 shape=(n,2)。增益单位为线性幅度。
import math

import numpy as np
from scipy import signal

RATE = 44100


# ── 基础工具 ─────────────────────────────────────────────────────────────

def seconds(n_samples):
    return n_samples / RATE


def samples(sec):
    return int(round(sec * RATE))


def midi(m):
    """MIDI 音号 → 频率（A4=69=440Hz）。"""
    return 440.0 * 2.0 ** ((m - 69) / 12.0)


def silence(sec, stereo=False):
    n = samples(sec)
    return np.zeros((n, 2)) if stereo else np.zeros(n)


def mix_into(dst, src, at_sec, gain=1.0):
    """把 src 以 gain 叠加进 dst 的 at_sec 处；越界部分截断（dst 不扩容，循环缓冲要定长）。"""
    ofs = samples(at_sec)
    n = min(len(src), len(dst) - ofs)
    if n <= 0:
        return
    if dst.ndim == 2 and src.ndim == 1:  # 单声道叠进立体声：两声道等量
        dst[ofs:ofs + n, 0] += gain * src[:n]
        dst[ofs:ofs + n, 1] += gain * src[:n]
    else:
        dst[ofs:ofs + n] += gain * src[:n]


def to_stereo(x, pan=0.0):
    """单声道 → 立体声。pan ∈ [-1,1]，等功率声像。"""
    ang = (pan + 1) * math.pi / 4
    return np.stack([x * math.cos(ang), x * math.sin(ang)], axis=1)


# ── 振荡器（wavetable，带限锯齿按基频选谐波数防混叠）───────────────────────

_TABLE_SIZE = 4096
_saw_tables = {}


def _saw_table(nharm):
    if nharm not in _saw_tables:
        k = np.arange(1, nharm + 1)
        ph = np.linspace(0, 1, _TABLE_SIZE, endpoint=False)
        _saw_tables[nharm] = (np.sin(2 * np.pi * np.outer(ph, k)) / k).sum(axis=1) * (2 / np.pi)
    return _saw_tables[nharm]


def osc_saw(freq, sec, phase0=0.0):
    """带限锯齿：谐波数按基频截到 ~16kHz 以下（防混叠），wavetable 查表。freq 可为标量或逐样本数组。"""
    n = samples(sec)
    t = np.arange(n) / RATE
    f_scalar = float(np.mean(freq))
    nharm = max(1, min(64, int(16000 / max(f_scalar, 20.0))))
    table = _saw_table(nharm)
    if np.isscalar(freq):
        ph = phase0 + freq * t
    else:
        ph = phase0 + np.cumsum(np.asarray(freq)) / RATE
    idx = ((ph % 1.0) * _TABLE_SIZE).astype(np.intp)
    return table[idx]


def osc_sine(freq, sec, phase0=0.0):
    """正弦。freq 可为标量或逐样本数组（数组时相位累积，变频无啁啾伪音）。"""
    n = samples(sec)
    if np.isscalar(freq):
        t = np.arange(n) / RATE
        return np.sin(2 * np.pi * (phase0 + freq * t))
    ph = phase0 + np.cumsum(np.asarray(freq)[:n]) / RATE
    return np.sin(2 * np.pi * ph)


def detuned_saw_stack(freq, sec, voices=3, detune_cents=7.0, rng=None):
    """失谐锯齿堆叠（合成垫的主音色）：voices 个振荡器在 ±detune_cents 内均布 + 随机初相。
    相近频率相互拍频 → 厚而缓慢流动的"模拟感"，单振荡正弦垫（旧版蜂鸣感）的直接解药。"""
    rng = rng or np.random.default_rng(0)
    out = np.zeros(samples(sec))
    for v in range(voices):
        cents = detune_cents * (2 * v / max(voices - 1, 1) - 1) if voices > 1 else 0.0
        out += osc_saw(freq * 2 ** (cents / 1200), sec, phase0=rng.random())
    return out / voices


# ── 包络 ─────────────────────────────────────────────────────────────────

def env_ar(n, attack_sec, release_sec, curve=1.0):
    """attack/release 包络（线性起、幂次收），总长 n 样本。"""
    t = np.arange(n) / RATE
    total = n / RATE
    a = np.clip(t / max(attack_sec, 1e-4), 0, 1)
    r = np.clip((total - t) / max(release_sec, 1e-4), 0, 1) ** curve
    return np.minimum(a, r)


def env_exp(n, tau, edge_pow=0.5):
    """指数衰减 × 末端幂次归零：打击类的自然余韵（指数不到零，末端乘 (1-t)^p 防残余台阶）。"""
    t = np.arange(n) / RATE
    total = n / RATE
    return np.exp(-t / tau) * (1 - t / total) ** edge_pow


# ── 滤波 ─────────────────────────────────────────────────────────────────

def lowpass(x, fc, order=2):
    sos = signal.butter(order, min(fc, RATE / 2 * 0.98), "low", fs=RATE, output="sos")
    return signal.sosfilt(sos, x, axis=0)


def highpass(x, fc, order=2):
    sos = signal.butter(order, max(fc, 10.0), "high", fs=RATE, output="sos")
    return signal.sosfilt(sos, x, axis=0)


def bandpass(x, lo, hi, order=2):
    sos = signal.butter(order, [max(lo, 10.0), min(hi, RATE / 2 * 0.98)], "band", fs=RATE, output="sos")
    return signal.sosfilt(sos, x, axis=0)


def lowpass_sweep(x, fc_curve, order=2, block=512):
    """时变低通（滤波扫频）：分块处理、块间携带滤波器状态，慢扫频下状态失配不可闻。
    fc_curve 为逐样本截止频率数组（长度 ≥ len(x)）。"""
    y = np.empty_like(x)
    zi = None
    for start in range(0, len(x), block):
        end = min(start + block, len(x))
        fc = float(np.mean(fc_curve[start:end]))
        sos = signal.butter(order, np.clip(fc, 30.0, RATE / 2 * 0.98), "low", fs=RATE, output="sos")
        if zi is None:
            zi = signal.sosfilt_zi(sos) * x[start]
        y[start:end], zi = signal.sosfilt(sos, x[start:end], zi=zi)
    return y


# ── 效果 ─────────────────────────────────────────────────────────────────

def delay_echo(x, time_sec, feedback=0.4, repeats=8, damp_fc=3500.0, pingpong=False):
    """回声（feed-forward 展开的反馈延迟）：每次重复增益 ×feedback、并逐次低通变闷（模拟反馈路衰减）。
    pingpong=True 时输出立体声、重复在左右交替。返回"湿"信号（不含干声），由调用方按需混合。"""
    d = samples(time_sec)
    n = len(x)
    wet = np.zeros((n, 2)) if pingpong else np.zeros(n)
    tap = x.copy()
    for k in range(1, repeats + 1):
        tap = lowpass(tap, damp_fc)
        g = feedback ** k
        if g < 1e-3 or k * d >= n:
            break
        seg = tap[: n - k * d] * g
        if pingpong:
            wet[k * d:, k % 2] += seg
        else:
            wet[k * d:] += seg
    return wet


def _fb_comb(x, delay, g, damp):
    """反馈梳状滤波 y[n] = x[n] + g·LP(y[n-D])——按块（块长=D）精确向量化，反馈路一阶低通做高频阻尼。"""
    n = len(x)
    y = np.zeros(n)
    lp_b, lp_a = [1 - damp], [1, -damp]
    zi = signal.lfilter_zi(lp_b, lp_a) * 0.0
    prev = np.zeros(delay)
    for start in range(0, n, delay):
        end = min(start + delay, n)
        fed, zi = signal.lfilter(lp_b, lp_a, prev[: end - start], zi=zi)
        y[start:end] = x[start:end] + g * fed
        prev = y[start:end] if end - start == delay else np.concatenate([y[start:end], np.zeros(delay - (end - start))])
    return y


def _allpass(x, delay, g=0.5):
    """Schroeder 全通 y[n] = -g·x[n] + x[n-D] + g·y[n-D]，同块长=D 的精确向量化。"""
    n = len(x)
    y = np.zeros(n)
    prev_x = np.zeros(delay)
    prev_y = np.zeros(delay)
    for start in range(0, n, delay):
        end = min(start + delay, n)
        m = end - start
        y[start:end] = -g * x[start:end] + prev_x[:m] + g * prev_y[:m]
        if m == delay:
            prev_x, prev_y = x[start:end].copy(), y[start:end].copy()
        else:
            prev_x = np.concatenate([x[start:end], np.zeros(delay - m)])
            prev_y = np.concatenate([y[start:end], np.zeros(delay - m)])
    return y


_COMBS = [1557, 1617, 1491, 1422]
_ALLPASSES = [223, 556]


def reverb(x, mix=0.3, rt=2.2, damp=0.35, stereo_spread=37):
    """Schroeder 混响（4 并联反馈梳 + 2 串联全通）。输入单/立体声均可，输出立体声——
    右声道梳状延迟整体偏移 stereo_spread 个样本，两耳回声图样不同 = 空间宽度。"""
    dry = x if x.ndim == 2 else np.stack([x, x], axis=1)
    out = np.empty_like(dry)
    for ch, spread in ((0, 0), (1, stereo_spread)):
        src = dry[:, ch]
        acc = np.zeros(len(src))
        for base in _COMBS:
            d = base + spread
            g = 10 ** (-3 * d / (rt * RATE))
            acc += _fb_comb(src, d, g, damp)
        acc /= len(_COMBS)
        for d in _ALLPASSES:
            acc = _allpass(acc, d + spread)
        out[:, ch] = acc
    return dry * (1 - mix) + out * mix


def chorus(x, rate_hz=0.35, depth_ms=6.0, base_ms=14.0, mix=0.4):
    """合唱：LFO 调制的分数延迟（np.interp 线性插值），立体声两声道 LFO 反相 → 宽度。"""
    st = x if x.ndim == 2 else np.stack([x, x], axis=1)
    n = len(st)
    t = np.arange(n)
    out = st.copy() * (1 - mix)
    for ch, phase in ((0, 0.0), (1, 0.5)):
        lfo = np.sin(2 * np.pi * (rate_hz * t / RATE + phase))
        idx = t - (base_ms + depth_ms * lfo) * RATE / 1000.0
        out[:, ch] += mix * np.interp(np.clip(idx, 0, n - 1), t, st[:, ch])
    return out


def softclip(x, drive=1.0):
    return np.tanh(x * drive) / math.tanh(drive) if drive > 0 else x


# ── 循环无缝 ─────────────────────────────────────────────────────────────

def wrap_loop_tail(x, loop_sec):
    """尾部回绕：渲染长度 = 循环长 + 尾巴（混响/回声/长释放自然延伸），
    把循环点之后的尾巴叠回开头再截断——循环边界两侧是同一段声音的延续，无缝且无"静默感"。"""
    n_loop = samples(loop_sec)
    out = x[:n_loop].copy()
    tail = x[n_loop:]
    m = min(len(tail), n_loop)
    out[:m] += tail[:m]
    return out


def loop_crossfade(x, fade_sec=0.05):
    """环形交叉淡接（噪声类循环用）：末尾 fade 段与开头等功率混合，接缝处相关噪声无断感。"""
    f = samples(fade_sec)
    y = x.copy()
    w = np.linspace(0, 1, f) if x.ndim == 1 else np.linspace(0, 1, f)[:, None]
    y[:f] = y[:f] * w + x[len(x) - f:] * (1 - w)
    return y[: len(x) - f]


def seam_report(x, name):
    """循环接缝质量：边界样本跳变 vs 全曲平均逐样本跳变（≈1 为完美）。"""
    mono = x if x.ndim == 1 else x.mean(axis=1)
    seam = abs(mono[0] - mono[-1])
    avg = np.abs(np.diff(mono)).mean() + 1e-12
    return f"{name}: seam-jump={seam:.4f} ({seam / avg:.1f}x avg-step)"


# ── 噪声 ─────────────────────────────────────────────────────────────────

def white(n_or_sec, rng, sec=True):
    n = samples(n_or_sec) if sec else int(n_or_sec)
    return rng.uniform(-1, 1, n)


def crackle(sec, rng, density_hz=40.0, tau=0.2):
    """稀疏脉冲串（爆炸碎裂/静电噼啪）：泊松间隔的双极性冲击，密度随时间指数衰减。"""
    n = samples(sec)
    out = np.zeros(n)
    t = 0.0
    while t < sec:
        rate = density_hz * math.exp(-t / tau) + 1.0
        t += rng.exponential(1.0 / rate)
        i = samples(t)
        if i < n:
            out[i] = rng.uniform(0.4, 1.0) * (1 if rng.random() < 0.5 else -1)
    return out


# ── 响度与落盘 ───────────────────────────────────────────────────────────

def rms_db(x):
    return 20 * math.log10(max(math.sqrt(float((x ** 2).mean())), 1e-9))


def normalize(x, target_rms_db, peak_cap=0.9):
    """归一到目标 RMS（dBFS），峰值超 cap 时整体回落——各资产响度可控、与旧基准对齐。"""
    y = x * 10 ** ((target_rms_db - rms_db(x)) / 20)
    peak = float(np.abs(y).max())
    if peak > peak_cap:
        y *= peak_cap / peak
    return y


def write_wav(out_dir, name, x):
    import os
    import wave
    path = f"{out_dir}/{name}.wav"
    os.makedirs(out_dir, exist_ok=True)
    y = np.tanh(x) * 0.98  # 软限幅安全网（正常内容远低于 1.0，不改变音色）
    pcm = (y * 32767).astype("<i2")
    ch = 1 if x.ndim == 1 else x.shape[1]
    with wave.open(path, "wb") as w:
        w.setnchannels(ch)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    dur = len(x) / RATE
    print(f"written: {path}  {dur:6.2f}s  ch={ch}  peak={float(np.abs(x).max()):.3f}  rms={rms_db(x if x.ndim == 1 else x.mean(axis=1)):6.1f} dBFS")
