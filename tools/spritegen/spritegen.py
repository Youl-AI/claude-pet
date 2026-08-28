"""빌드 타임 스프라이트 생성기. 런타임에는 실행되지 않는다.

산출물: 32x32 프레임 8개 x 6행 = 256x192 PNG.
행 순서는 PetCore.PetState enum 순서와 반드시 일치해야 한다.
"""
import struct
import zlib
from pathlib import Path

FRAME = 32
COLS = 8
ROWS = 6
W, H = FRAME * COLS, FRAME * ROWS

# (몸통, 무늬, 눈) — 행마다 다른 팔레트로 상태를 구분한다.
PALETTES = [
    ((240, 200, 120), (200, 150, 80), (40, 40, 40)),      # 0 Idle
    ((150, 200, 240), (100, 150, 200), (40, 40, 40)),      # 1 Reading
    ((180, 240, 170), (130, 190, 120), (40, 40, 40)),      # 2 Writing
    ((250, 200, 90), (210, 150, 50), (40, 40, 40)),        # 3 Running
    ((240, 130, 130), (200, 80, 80), (255, 255, 255)),     # 4 Error
    ((255, 230, 120), (240, 170, 40), (40, 40, 40)),       # 5 NeedsYou
]


def blank():
    return [[(0, 0, 0, 0)] * W for _ in range(H)]


def draw_cat(px, ox, oy, palette, bob, ear_up):
    body, stripe, eye = palette

    # 몸통
    for y in range(14, 26):
        for x in range(8, 26):
            px[oy + y + bob][ox + x] = (*body, 255)

    # 줄무늬
    for y in range(16, 24, 3):
        for x in range(10, 24):
            px[oy + y + bob][ox + x] = (*stripe, 255)

    # 머리
    for y in range(6, 16):
        for x in range(10, 24):
            px[oy + y + bob][ox + x] = (*body, 255)

    # 귀
    ear_top = 2 if ear_up else 4
    for i in range(4):
        for y in range(ear_top + i, 7):
            px[oy + y + bob][ox + 11 + i] = (*body, 255)
            px[oy + y + bob][ox + 20 - i] = (*body, 255)

    # 눈
    for x in (14, 19):
        px[oy + 10 + bob][ox + x] = (*eye, 255)
        px[oy + 11 + bob][ox + x] = (*eye, 255)

    # 다리
    for x in (10, 15, 18, 23):
        for y in range(26, 29):
            px[oy + y + bob][ox + x] = (*stripe, 255)

    # 꼬리
    for i in range(6):
        px[oy + 24 - i + bob][ox + 26 + (i // 3)] = (*body, 255)


def write_png(path, px):
    raw = b"".join(
        b"\x00" + b"".join(struct.pack("4B", *px[y][x]) for x in range(W))
        for y in range(H)
    )

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c))

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png)


def main():
    px = blank()
    for row, palette in enumerate(PALETTES):
        for col in range(COLS):
            bob = (col // 2) % 2          # 2프레임마다 1px 위아래
            ear_up = row != 0 or col < 4  # Idle 행 후반부는 귀를 내려 조는 느낌
            draw_cat(px, col * FRAME, row * FRAME, palette, bob, ear_up)

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "pet.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
