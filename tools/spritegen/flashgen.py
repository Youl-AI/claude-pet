"""레벨업 이펙트 스프라이트를 만든다.

몸통 실루엣에서 링이 바깥으로 퍼지며 사라진다. 8프레임, 12fps 로 약 0.66초.

32x32 안에 들어간다: 몸통이 x 4..27 이라 좌우 4px 여유가 있고 링 3px 이 맞는다.
아래쪽은 발이 바닥(y=31)에 붙어 있어 여유가 없으므로 링이 바닥에서 잘린다 —
빛이 바닥을 뚫지 않는 것이 오히려 자연스럽다 (스펙 §6).

spritegen.py 와 같은 무의존 PNG 작성기를 쓴다.
"""
import struct
import zlib
from pathlib import Path

FRAME = 32
COLS = 8
W, H = FRAME * COLS, FRAME

# pet.png 의 서 있는 포즈 지오메트리와 맞춘다.
BODY_X0, BODY_X1 = 6, 25
BODY_Y0, BODY_Y1 = 15, 27
LEFT_NUB_X0, LEFT_NUB_X1 = 4, 5
RIGHT_NUB_X0, RIGHT_NUB_X1 = 26, 27
NUB_Y0, NUB_Y1 = 21, 24
LEG_Y0, LEG_Y1 = 28, 31
LEG_X0S = (7, 11, 19, 23)

# 링 색: 따뜻한 흰색에서 산호색으로 식는다. 알파로 사라진다.
RING = (255, 248, 232)

# 프레임별 (실루엣에서 바깥으로 밀어낸 거리, 알파)
# f0 은 실루엣에 딱 붙고, f3 에서 가장 크고, f7 에서 사라진다.
EXPAND = (0, 1, 2, 3, 4, 5, 6, 7)
ALPHA  = (255, 255, 230, 190, 145, 100, 55, 20)


def blank():
    return [[(0, 0, 0, 0)] * W for _ in range(H)]


def silhouette():
    """서 있는 포즈가 채우는 셀 좌표 집합."""
    cells = set()
    for y in range(BODY_Y0, BODY_Y1 + 1):
        for x in range(BODY_X0, BODY_X1 + 1):
            cells.add((x, y))
    for y in range(NUB_Y0, NUB_Y1 + 1):
        for x in range(LEFT_NUB_X0, LEFT_NUB_X1 + 1):
            cells.add((x, y))
        for x in range(RIGHT_NUB_X0, RIGHT_NUB_X1 + 1):
            cells.add((x, y))
    for x0 in LEG_X0S:
        for y in range(LEG_Y0, LEG_Y1 + 1):
            cells.add((x0, y))
            cells.add((x0 + 1, y))
    return cells


def ring_at(cells, distance):
    """실루엣에서 정확히 distance 만큼 떨어진 껍질(체비쇼프 거리).

    distance=0 이 실루엣 그 자체(속이 꽉 찬 덩어리)가 아니라 실루엣 바로
    바깥 한 겹부터 시작하도록 모든 distance 에 1을 더한다. distance==0 일
    때만 보정하면 EXPAND=0 과 EXPAND=1 이 같은 체비쇼프 거리(1)로 뭉개져
    두 프레임이 완전히 겹치므로, 모든 값에 균일하게 오프셋을 준다.
    """
    distance += 1
    inner = grow(cells, distance - 1)
    outer = grow(cells, distance)
    return outer - inner


def grow(cells, distance):
    """체비쇼프 거리 distance 만큼 부풀린 집합."""
    out = set()
    for (x, y) in cells:
        for dy in range(-distance, distance + 1):
            for dx in range(-distance, distance + 1):
                out.add((x + dx, y + dy))
    return out


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
    body = silhouette()
    for col in range(COLS):
        ox = col * FRAME
        alpha = ALPHA[col]
        for (x, y) in ring_at(body, EXPAND[col]):
            # 셀 밖으로 나간 픽셀은 버린다. 아래쪽은 바닥에서 잘린다.
            if 0 <= x < FRAME and 0 <= y < FRAME:
                px[y][ox + x] = (*RING, alpha)

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "flash.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
