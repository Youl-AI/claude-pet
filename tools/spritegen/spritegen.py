"""빌드 타임 스프라이트 생성기. 런타임에는 실행되지 않는다.

산출물: 32x32 프레임 8개 x 6행 = 256x192 PNG.
행 순서는 PetCore.PetState enum 순서와 반드시 일치해야 한다
(Idle, Reading, Writing, Running, Error, NeedsYou).

캐릭터: 납작한 산호주황(coral-orange) 픽셀 아트, 음영/외곽선 없음(NeedsYou 제외).
머리 없이 넓은 직사각형 몸통 하나가 머리를 겸한다. 여섯 상태는 전부 같은
산호색 몸통을 유지하고, 오직 "움직임과 표정"만으로 구별한다 — 상태마다
색을 바꾸던 이전 방식은 캐릭터의 정체성(=산호색)을 해치므로 쓰지 않는다.

Python 표준 라이브러리만 사용한다 (struct, zlib, pathlib) — Pillow 없음.
"""
import struct
import zlib
from pathlib import Path

FRAME = 32
COLS = 8
ROWS = 6
W, H = FRAME * COLS, FRAME * ROWS

# --- 색상: 이름 있는 상수로 정의 ---
BODY_COLOR = (214, 132, 90)            # 산호주황 몸통 — 모든 상태 공통
EYE_COLOR = (26, 26, 26)               # 거의 검은 눈
NEEDSYOU_BODY_COLOR = (255, 178, 140)  # NeedsYou 전용: 눈에 띄게 밝힌 산호색
OUTLINE_BRIGHT = (255, 244, 200)       # NeedsYou 펄스 외곽선 — 밝은 위상
OUTLINE_DIM = (255, 208, 140)          # NeedsYou 펄스 외곽선 — 어두운 위상

# --- 레스트 포즈 지오메트리 (셀 기준 상대좌표, 양끝 포함 구간) ---
# 사용자가 레퍼런스 이미지에서 직접 측정해 준 정확한 배치.
BODY_X0, BODY_X1 = 6, 25   # 20 wide
BODY_Y0, BODY_Y1 = 6, 18   # 13 tall

LEFT_NUB_X0, LEFT_NUB_X1 = 4, 5
RIGHT_NUB_X0, RIGHT_NUB_X1 = 26, 27
NUB_Y0, NUB_Y1 = 12, 15

LEFT_EYE_X0, LEFT_EYE_X1 = 10, 11
RIGHT_EYE_X0, RIGHT_EYE_X1 = 21, 22
EYE_Y0, EYE_Y1 = 9, 12

LEG_Y0, LEG_Y1 = 19, 22
LEG_X0S = (7, 11, 19, 23)  # 다리 4개의 왼쪽 x. 각 다리는 2px 폭(x0, x0+1).
                            # 왼쪽 쌍 7/11, 오른쪽 쌍 19/23 — 가운데 12..18 은 빈 간격.

# 골격 상수가 미래에 바뀌어도 다리가 몸통 폭을 벗어나지 않는지 미리 확인한다.
assert all(BODY_X0 <= x0 and x0 + 1 <= BODY_X1 for x0 in LEG_X0S), "다리가 몸통 폭을 벗어남"


def blank():
    return [[(0, 0, 0, 0)] * W for _ in range(H)]


def put(px, ox, oy, x, y, color):
    """셀 기준 상대좌표 (x, y)에 색을 찍는다. 32x32 셀(인덱스 0..31)을 벗어나는
    쓰기는 조용히 실패하는 대신 즉시 assert로 막는다 — build-time 스크립트라
    비용이 없고, 애니메이션 파라미터를 아무리 바꿔도 이웃 프레임을 침범하는
    일이 "일어나기 어렵다" 수준이 아니라 "일어날 수 없다" 수준으로 보장된다."""
    assert 0 <= x < FRAME and 0 <= y < FRAME, f"cell 밖 쓰기: x={x} y={y}"
    px[oy + y][ox + x] = color


def rect(px, ox, oy, x0, x1, y0, y1, dx, dy, color):
    """양끝 포함 사각형 [x0,x1]x[y0,y1] 을 (dx, dy)만큼 이동해 채운다."""
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            put(px, ox, oy, x + dx, y + dy, color)


def draw_eyes(px, ox, oy, dx, dy, mode):
    """눈 표현 세 가지:
    - normal: 얇고 긴 세로 막대 (레스트 포즈)
    - blink:  세로 막대가 짧은 한 줄로 줄어듦 (감은 눈)
    - wince:  가로로 넓은 짧은 막대 — 찡그리며 질끈 감은 눈 (Error 전용)
    """
    color = (*EYE_COLOR, 255)
    if mode == "normal":
        rect(px, ox, oy, LEFT_EYE_X0, LEFT_EYE_X1, EYE_Y0, EYE_Y1, dx, dy, color)
        rect(px, ox, oy, RIGHT_EYE_X0, RIGHT_EYE_X1, EYE_Y0, EYE_Y1, dx, dy, color)
    elif mode == "blink":
        y = EYE_Y1 - 1  # 세로 막대 안의 한 줄만 남겨 감은 눈처럼 보이게 한다
        rect(px, ox, oy, LEFT_EYE_X0, LEFT_EYE_X1, y, y, dx, dy, color)
        rect(px, ox, oy, RIGHT_EYE_X0, RIGHT_EYE_X1, y, y, dx, dy, color)
    elif mode == "wince":
        y0, y1 = EYE_Y0 + 1, EYE_Y0 + 2  # 세로 4칸 중 가운데 2줄만 사용
        rect(px, ox, oy, LEFT_EYE_X0 - 1, LEFT_EYE_X1 + 1, y0, y1, dx, dy, color)
        rect(px, ox, oy, RIGHT_EYE_X0 - 1, RIGHT_EYE_X1 + 1, y0, y1, dx, dy, color)
    else:
        raise ValueError(f"unknown eye mode: {mode}")


def draw_outline(px, ox, oy, dx, dy, lean_dx, leg_dx, color):
    """실루엣 바깥으로 1px 확장한 외곽선을 몸통/눈썹혹/다리 각각에 대해 먼저
    그려둔다. 이후 draw_pet()이 그 위에 본체 색을 채우면서, 서로 맞닿은
    부위(몸통-눈썹혹, 몸통-다리) 사이의 이음매에 낀 외곽선은 자연히 덮여
    사라지고, 정말 바깥 경계에 해당하는 픽셀만 남는다."""
    if color is None:
        return
    o = (*color, 255)
    rect(px, ox, oy, BODY_X0 - 1, BODY_X1 + 1, BODY_Y0 - 1, BODY_Y1 + 1, dx + lean_dx, dy, o)
    rect(px, ox, oy, LEFT_NUB_X0 - 1, LEFT_NUB_X1 + 1, NUB_Y0 - 1, NUB_Y1 + 1, dx + lean_dx, dy, o)
    rect(px, ox, oy, RIGHT_NUB_X0 - 1, RIGHT_NUB_X1 + 1, NUB_Y0 - 1, NUB_Y1 + 1, dx + lean_dx, dy, o)
    for x0, ldx in zip(LEG_X0S, leg_dx):
        rect(px, ox, oy, x0 - 1, x0 + 2, LEG_Y0 - 1, LEG_Y1 + 1, dx + ldx, dy, o)


def draw_pet(px, ox, oy, *, body_color=BODY_COLOR, dx=0, dy=0, lean_dx=0,
             eye_mode="normal", eye_dx=0, leg_dx=(0, 0, 0, 0), outline_color=None):
    """캐릭터 한 프레임을 그린다.

    dx, dy       : 몸통·눈썹혹·다리·눈 전체에 적용되는 전역 이동 (흔들림/호핑용)
    lean_dx       : 몸통·눈썹혹·눈에만 더해지는 추가 x 이동 (다리는 그대로 두어
                     "몸이 진행 방향으로 살짝 기운" 느낌을 만든다 — Running 전용)
    eye_mode/eye_dx: 눈 표현 방식과, 눈에만 더해지는 추가 x 이동 (Reading 스캔용)
    leg_dx        : 다리 4개 각각에 더해지는 x 이동 (보폭 표현용)
    outline_color : None이면 외곽선 없음. 지정하면 1px 펄스 외곽선 (NeedsYou 전용)
    """
    draw_outline(px, ox, oy, dx, dy, lean_dx, leg_dx, outline_color)

    body = (*body_color, 255)
    rect(px, ox, oy, BODY_X0, BODY_X1, BODY_Y0, BODY_Y1, dx + lean_dx, dy, body)
    rect(px, ox, oy, LEFT_NUB_X0, LEFT_NUB_X1, NUB_Y0, NUB_Y1, dx + lean_dx, dy, body)
    rect(px, ox, oy, RIGHT_NUB_X0, RIGHT_NUB_X1, NUB_Y0, NUB_Y1, dx + lean_dx, dy, body)

    for x0, ldx in zip(LEG_X0S, leg_dx):
        rect(px, ox, oy, x0, x0 + 1, LEG_Y0, LEG_Y1, dx + ldx, dy, body)

    draw_eyes(px, ox, oy, dx + lean_dx + eye_dx, dy, eye_mode)


# ---------------------------------------------------------------------------
# 상태별 애니메이션 — 8프레임에 걸친 파라미터 표.
# 모든 상태가 같은 산호색 몸통을 쓰므로, 구별은 오직 움직임/표정으로만 한다.
# ---------------------------------------------------------------------------

# 0 Idle — 완만한 상하 bob + 이따금 눈을 깜빡임.
IDLE_DY = (0, 1, 2, 3, 2, 1, 0, 0)
IDLE_BLINK_COLS = {4, 5}  # "a frame or two" — 두 프레임 연속으로 감았다 뜬다


def idle_frame(col):
    return dict(
        dy=IDLE_DY[col],
        eye_mode="blink" if col in IDLE_BLINK_COLS else "normal",
    )


# 1 Reading — 몸은 가만히, 눈이 몸통 안에서 좌우로 스캔하듯 움직인다.
READING_EYE_DX = (0, -1, -2, -1, 0, 1, 2, 1)


def reading_frame(col):
    return dict(eye_dx=READING_EYE_DX[col])


# 2 Writing — 짧고 빠른 bob + 다리가 빠르게 번갈아 두드리듯 움직인다.
# bob은 주기 2, 다리는 주기 3으로 서로 엇갈리게 해서 프레임마다 조합이
# 계속 바뀌게 한다 (같은 주기로 묶으면 프레임 절반이 서로 동일해진다).
WRITING_DY = (0, 1, 0, 1, 0, 1, 0, 1)
WRITING_LEG_SETS = ((0, 0, 0, 0), (1, -1, 1, -1), (-1, 1, -1, 1))


def writing_frame(col):
    return dict(dy=WRITING_DY[col], leg_dx=WRITING_LEG_SETS[col % 3])


# 3 Running — 더 큰 보폭(다리가 크게 벌어짐)과 진행 방향으로의 살짝 기울임.
RUNNING_DY = (0, 1, 2, 3, 3, 2, 1, 0)
RUNNING_LEG_SETS = ((-2, -2, 2, 2), (1, 1, -1, -1), (2, 2, -2, -2), (1, 1, -1, -1))


def running_frame(col):
    return dict(dy=RUNNING_DY[col], leg_dx=RUNNING_LEG_SETS[col % 4], lean_dx=1)


# 4 Error — 눈은 항상 질끈 감은 가로 막대(wince), 몸 전체가 좌우로 떨린다.
ERROR_DX = (0, 1, -1, 2, -2, 1, -1, 0)


def error_frame(col):
    return dict(dx=ERROR_DX[col], eye_mode="wince")


# 5 NeedsYou — 가장 중요한 신호이므로 한눈에 다른 상태와 착각할 수 없어야
# 한다. 밝힌 산호색 + 펄스하는 1px 외곽선 + 가장 큰 호핑(dy 최대 -4)을
# 함께 쓴다. dy와 외곽선 밝기 위상이 서로 다른 주기로 어긋나 있어서 8프레임
# 전부가 서로 다른 그림이 된다.
NEEDSYOU_DY = (0, -1, -2, -4, -3, -2, -1, 0)


def needsyou_frame(col):
    return dict(
        dy=NEEDSYOU_DY[col],
        body_color=NEEDSYOU_BODY_COLOR,
        outline_color=OUTLINE_BRIGHT if col % 2 == 0 else OUTLINE_DIM,
    )


ROW_FRAME_FNS = (
    idle_frame,      # 0 Idle
    reading_frame,   # 1 Reading
    writing_frame,   # 2 Writing
    running_frame,   # 3 Running
    error_frame,     # 4 Error
    needsyou_frame,  # 5 NeedsYou
)
assert len(ROW_FRAME_FNS) == ROWS, "행 함수 개수가 PetState 값 개수와 다름"


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
    for row, frame_fn in enumerate(ROW_FRAME_FNS):
        for col in range(COLS):
            draw_pet(px, col * FRAME, row * FRAME, **frame_fn(col))

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "pet.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
