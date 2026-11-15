"""Generate the original WinKit application icon using bundled Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / "assets"
assets.mkdir(exist_ok=True)
image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((8, 8, 248, 248), radius=52, fill="#2563EB")
# Toolbox handle and case: legible even at small native icon sizes.
draw.rounded_rectangle((87, 60, 169, 109), radius=13, outline="white", width=12)
draw.rounded_rectangle((47, 91, 209, 193), radius=18, fill="white")
draw.rectangle((48, 126, 208, 136), fill="#2563EB")
draw.rounded_rectangle((113, 116, 143, 147), radius=5, fill="#2563EB")
image.save(assets / "WinKit.png")
image.save(assets / "WinKit.ico", sizes=[(16,16), (24,24), (32,32), (48,48), (64,64), (128,128), (256,256)])
print("Generated WinKit icon.")
