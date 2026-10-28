#!/usr/bin/env python3
"""Regenerates mobile/res (launcher icons + splash wordmark) from the AFIF mark.
Run after changing the logo: python3 mobile/make_res.py  (needs cairosvg + pillow)."""
import io, os, cairosvg
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "res")
HAND = "M-30,-22 c6,-8 16,-8 20,-2 l7,12 c3,5 1,11 -4,14 l-6,3 c4,10 12,19 22,24 l4,-6 c3,-5 9,-7 14,-4 l12,7 c6,4 6,14 -1,20 c-10,9 -26,10 -42,-1 c-17,-12 -30,-30 -32,-46 c-1,-8 1,-15 6,-21 z"
GRAD = '<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#38e1ff"/><stop offset="1" stop-color="#2563eb"/></linearGradient>'
def emblem(scale):
    return f'''<g transform="translate(54 54) scale({scale})">
  <polygon points="0,-82 71,-41 71,41 0,82 -71,41 -71,-41" fill="#0b1630" stroke="url(#g)" stroke-width="8" stroke-linejoin="round"/>
  <path d="{HAND}" fill="url(#g)"/>
  <path d="M14,-34 a26,26 0 0 1 22,22" fill="none" stroke="#38e1ff" stroke-width="7" stroke-linecap="round"/>
  <path d="M16,-54 a46,46 0 0 1 40,40" fill="none" stroke="#38e1ff" stroke-width="7" stroke-linecap="round" opacity=".7"/></g>'''
# adaptive foreground: 108x108 canvas, art inside the 66 dp safe zone
FG = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108"><defs>{GRAD}</defs>{emblem(0.36)}</svg>'
# legacy icon: full square tile
LEG = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108"><defs>{GRAD}</defs><rect x="4" y="4" width="100" height="100" rx="22" fill="#0b1630"/>{emblem(0.5)}</svg>'
ROUND = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108"><defs>{GRAD}</defs><circle cx="54" cy="54" r="52" fill="#0b1630"/>{emblem(0.46)}</svg>'
WORD = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 400">
<text x="500" y="230" text-anchor="middle" font-family="Poppins, Montserrat, Arial, sans-serif" font-size="130" font-weight="800" fill="#2563eb">AFIF Phone</text>
<text x="500" y="320" text-anchor="middle" font-family="Poppins, Montserrat, Arial, sans-serif" font-size="44" font-weight="600" letter-spacing="14" fill="#64748b">BY AFIF-TECH</text></svg>'''

def png(svg, w, h, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    data = cairosvg.svg2png(bytestring=svg.encode(), output_width=w, output_height=h)
    Image.open(io.BytesIO(data)).save(path, optimize=True)

for d, k in {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}.items():
    folder = os.path.join(OUT, f"mipmap-{d}")
    png(FG, int(108 * k), int(108 * k), os.path.join(folder, "linphone_launcher_icon_foreground.png"))
    png(LEG, int(48 * k), int(48 * k), os.path.join(folder, "ic_launcher.png"))
    png(ROUND, int(48 * k), int(48 * k), os.path.join(folder, "ic_launcher_round.png"))
png(WORD, 1000, 400, os.path.join(OUT, "drawable", "linphone_splashscreen_branding.png"))
# a large preview for the README / release page
png(LEG, 512, 512, os.path.join(HERE, "afif-phone-icon.png"))
print("done")
