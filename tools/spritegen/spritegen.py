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
# 여섯 상태가 서로 뚜렷이 구별되는 색상(hue)을 쓰도록 지정한 팔레트.
# NeedsYou(5)는 다른 어떤 행도 쓰지 않는 마젠타로, 다른 상태와 절대 혼동되지 않게 한다.
PALETTES = [
    ((230, 205, 150), (195, 165, 110), (40, 40, 40)),      # 0 Idle      warm tan
    ((120, 180, 240), (80, 135, 200), (40, 40, 40)),       # 1 Reading   blue
    ((140, 225, 150), (95, 180, 105), (40, 40, 40)),       # 2 Writing   green
    ((255, 150, 50), (215, 110, 25), (40, 40, 40)),        # 3 Running   orange
    ((235, 80, 80), (195, 45, 45), (255, 255, 255)),       # 4 Error     red
    ((235, 110, 235), (190, 65, 195), (255, 255, 255)),    # 5 NeedsYou  magenta
]

# 프레임 8칸에 걸친 4단계 bob(상하 움직임) 사이클. 0→1→2→1로 부드럽게
# 오르내리는 것처럼 보이게 한다. 다리 위치(y 최대 28)에 이 값을 더해도
# 32px 셀(인덱스 0..31)을 벗어나지 않아야 하며, 아래 assert가 이를 보증한다.
BOB_CYCLE = (0, 1, 2, 1)
assert 28 + max(BOB_CYCLE) < FRAME, "bob이 커서 다리가 셀 밖으로 나감"

# 다리 x 위치 두 세트를 프레임마다 번갈아 그려서 걷는 듯한 스텝을 만든다.
LEG_SETS = (
    (10, 15, 18, 23),
    (9, 16, 17, 24),
)
for _legs in LEG_SETS:
    assert all(0 <= _lx < FRAME for _lx in _legs), "다리 x가 셀 밖으로 나감"


def blank():
    return [[(0, 0, 0, 0)] * W for _ in range(H)]


def put(px, ox, oy, x, y, color):
    """셀 기준 상대좌표 (x, y)에 색을 찍는다. 32x32 셀을 벗어나는 쓰기는
    조용히 실패하는 대신 즉시 assert로 막는다 (build-time 스크립트이므로
    비용 없음)."""
    assert 0 <= x < FRAME and 0 <= y < FRAME, f"cell 밖 쓰기: x={x} y={y}"
    px[oy + y][ox + x] = color


def draw_cat(px, ox, oy, palette, bob, ear_up, leg_phase):
    body, stripe, eye = palette

    # 몸통
    for y in range(14, 26):
        for x in range(8, 26):
            put(px, ox, oy, x, y + bob, (*body, 255))

    # 줄무늬
    for y in range(16, 24, 3):
        for x in range(10, 24):
            put(px, ox, oy, x, y + bob, (*stripe, 255))

    # 머리
    for y in range(6, 16):
        for x in range(10, 24):
            put(px, ox, oy, x, y + bob, (*body, 255))

    # 귀
    ear_top = 2 if ear_up else 4
    for i in range(4):
        for y in range(ear_top + i, 7):
            put(px, ox, oy, 11 + i, y + bob, (*body, 255))
            put(px, ox, oy, 20 - i, y + bob, (*body, 255))

    # 눈
    for x in (14, 19):
        put(px, ox, oy, x, 10 + bob, (*eye, 255))
        put(px, ox, oy, x, 11 + bob, (*eye, 255))

    # 다리 — 프레임마다 두 위치 세트를 번갈아 그려 걷는 스텝을 표현한다.
    legs = LEG_SETS[leg_phase % len(LEG_SETS)]
    for x in legs:
        for y in range(26, 29):
            put(px, ox, oy, x, y + bob, (*stripe, 255))

    # 꼬리
    for i in range(6):
        put(px, ox, oy, 26 + (i // 3), 24 - i + bob, (*body, 255))


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
            bob = BOB_CYCLE[col % len(BOB_CYCLE)]  # 0,1,2,1 로 오르내림
            # 다리 교대 주기를 bob 주기(4)와 다르게 (col // 2) % 2 로 잡는다.
            # col % 2 로 두면 bob=1 인 홀수 열(1,3,5,7)에서 다리도 항상 같은
            # 값이 되어 프레임 절반이 완전히 동일해지는 충돌이 생긴다.
            leg_phase = (col // 2) % 2
            ear_up = row != 0 or col < 4  # Idle 행 후반부는 귀를 내려 조는 느낌
            draw_cat(px, col * FRAME, row * FRAME, palette, bob, ear_up, leg_phase)

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "pet.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
