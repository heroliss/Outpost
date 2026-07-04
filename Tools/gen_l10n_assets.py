# 一次性脚本：生成 demo 本地化章的 per-locale 演示资产（横幅 PNG + 提示音 WAV）。
# 从项目根运行。命名遵循「location 后缀约定」：<资源名>_<locale>。
import math
import struct
import wave

from PIL import Image, ImageDraw, ImageFont

OUT_DIR = "Assets/Game/Framework/Demo/Res/L10N"
FONT_PATH = "Assets/Game/Fonts/NotoSansSC-Regular.ttf"  # 框架随附的简中字体，画中英文都够

import os
os.makedirs(OUT_DIR, exist_ok=True)

# ── 横幅 PNG：底色 + 大字，让「换语言换图」肉眼可辨 ─────────────────────────
def make_banner(name, text, bg, fg):
    img = Image.new("RGB", (512, 128), bg)
    draw = ImageDraw.Draw(img)
    font = ImageFont.truetype(FONT_PATH, 56)
    box = draw.textbbox((0, 0), text, font=font)
    pos = ((512 - (box[2] - box[0])) // 2 - box[0], (128 - (box[3] - box[1])) // 2 - box[1])
    draw.text(pos, text, font=font, fill=fg)
    path = f"{OUT_DIR}/{name}.png"
    img.save(path)
    print("written:", path)

make_banner("l10n-banner_zh-CN", "你好，世界", (168, 50, 44), (255, 240, 220))   # 中文红底
make_banner("l10n-banner_en", "Hello, World", (30, 70, 140), (220, 236, 255))  # 英文蓝底

# ── 提示音 WAV：中文上行双音、英文下行双音，听感可区分 ───────────────────────
def make_voice(name, freqs):
    rate = 44100
    seg = int(rate * 0.28)
    samples = []
    for f in freqs:
        for i in range(seg):
            env = min(1.0, i / (rate * 0.01), (seg - i) / (rate * 0.03))  # 首尾包络防爆音
            samples.append(0.35 * env * math.sin(2 * math.pi * f * i / rate))
    path = f"{OUT_DIR}/{name}.wav"
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", int(s * 32767)) for s in samples))
    print("written:", path)

make_voice("l10n-voice_zh-CN", [523.25, 659.25])  # C5 → E5 上行
make_voice("l10n-voice_en", [659.25, 523.25])     # E5 → C5 下行
