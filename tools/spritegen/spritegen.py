"""빌드 타임 스프라이트 생성기. 런타임에는 실행되지 않는다.

산출물: 32x32 프레임 8개 x 6행 = 256x192 PNG.
행 순서는 PetCore.PetState enum 순서와 반드시 일치해야 한다
(Idle, Reading, Writing, Running, Error, NeedsYou).

캐릭터: 납작한 픽셀 아트, 음영/외곽선 없음. 넓은 직사각형 몸통 하나가 머리를
겸하고, 눈썹혹 두 개, 눈 두 개, 다리 네 개(좌 2 + 우 2, 가운데 간격)로 이뤄진
실루엣은 모든 상태에서 그대로 유지한다. 상태 구분은 몸통 색 + 움직임/표정으로
한다 — 상태별 색을 다시 도입했다(사용자 피드백).

캐릭터는 32x32 셀의 바닥 쪽에 서 있다 (발이 y=29, 프레임 바닥에서 2줄 위).
그래서 상하 bob은 발이 셀 밖으로 나가지 않도록 "오직 위로만" 움직인다 —
dy는 항상 <= 0.

Python 표준 라이브러리만 사용한다 (struct, zlib, pathlib) — Pillow 없음.
"""
import struct
import zlib
from pathlib import Path

FRAME = 32
COLS = 8
ROWS = 6
W, H = FRAME * COLS, FRAME * ROWS

# --- 레스트 포즈 지오메트리 (셀 기준 상대좌표, 양끝 포함 구간) ---
# 사용자가 "발이 바닥에 닿도록" 다시 측정해 준 배치. 이전 배치보다 몸 전체가
# 아래로 7px 이동했을 뿐, 폭/모양은 그대로다.
BODY_X0, BODY_X1 = 6, 25    # 20 wide
BODY_Y0, BODY_Y1 = 13, 25   # 13 tall

LEFT_NUB_X0, LEFT_NUB_X1 = 4, 5
RIGHT_NUB_X0, RIGHT_NUB_X1 = 26, 27
NUB_Y0, NUB_Y1 = 19, 22

LEFT_EYE_X0, LEFT_EYE_X1 = 10, 11
RIGHT_EYE_X0, RIGHT_EYE_X1 = 21, 22
EYE_Y0, EYE_Y1 = 16, 19

LEG_Y0, LEG_Y1 = 26, 29
LEG_X0S = (7, 11, 19, 23)  # 다리 4개의 왼쪽 x. 각 다리는 2px 폭(x0, x0+1).
                            # 왼쪽 쌍 7/11, 오른쪽 쌍 19/23 — 가운데 12..18 은 빈 간격.

# 골격 상수가 미래에 바뀌어도 다리가 몸통 폭을 벗어나지 않는지, 발이 프레임
# 바닥(y=31) 안쪽에 머무는지 미리 확인한다.
assert all(BODY_X0 <= x0 and x0 + 1 <= BODY_X1 for x0 in LEG_X0S), "다리가 몸통 폭을 벗어남"
assert LEG_Y1 < FRAME, "발이 프레임 바닥 밖으로 나감"

# --- 색상: 상태별 몸통 색 (PetState enum 순서와 반드시 일치) ---
ROW_BODY_COLORS = (
    (214, 132, 90),   # 0 Idle     — 원래의 산호주황, "평상시" 기준색
    (120, 180, 240),  # 1 Reading  — 파랑
    (140, 225, 150),  # 2 Writing  — 초록
    (250, 190, 70),   # 3 Running  — 호박색
    (150, 150, 165),  # 4 Error    — 채도 낮은 회청색, 멍한 느낌
    (230, 70, 60),    # 5 NeedsYou — 강렬한 빨강
)

EYE_COLOR = (26, 26, 26)  # 거의 검은 눈 — 기본값 (Idle/Reading/Writing/Running/Error)
# NeedsYou는 몸통이 진한 빨강이라 검은 눈이 묻히는 느낌이 덜하긴 하지만, 이
# 상태가 가장 중요한 신호이므로 눈에도 "번뜩이는" 대비를 주기 위해 밝은 눈을
# 쓴다. Error는 wince(가로 막대) 모양 자체가 이미 뚜렷해서 검은 눈 그대로도
# 회청색 몸통 위에서 충분히 읽힌다 — 그래서 Error는 기본 EYE_COLOR를 유지.
NEEDSYOU_EYE_COLOR = (255, 224, 179)
ROW_EYE_COLORS = (EYE_COLOR, EYE_COLOR, EYE_COLOR, EYE_COLOR, EYE_COLOR, NEEDSYOU_EYE_COLOR)

# --- NeedsYou 전용: 화남 표시(빠직/💢) ---
# 몸이 아래로 이동하면서 비게 된 y 0..12 구간에 그린다. 두꺼운 대각선 두 개가
# 교차하는 "X" 형태 — 굵기는 셀 두 칸 정도, (MARK_CENTER_X, MARK_CENTER_Y)를
# 중심으로 대칭이며, 펄스에 따라 크기가 줄어들 때도 같은 중심을 공유한다.
MARK_COLOR = (150, 20, 20)
MARK_CENTER_X, MARK_CENTER_Y = 24, 6  # 표시 범위: 풀사이즈일 때 x 20..28, y 2..10


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


def draw_eyes(px, ox, oy, dx, dy, mode, eye_color):
    """눈 표현 세 가지:
    - normal: 얇고 긴 세로 막대 (레스트 포즈)
    - blink:  세로 막대가 짧은 한 줄로 줄어듦 (감은 눈)
    - wince:  가로로 넓은 짧은 막대 — 찡그리며 질끈 감은 눈 (Error 전용)
    """
    color = (*eye_color, 255)
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


def draw_anger_mark(px, ox, oy, size):
    """빠직/💢 anger mark: 두꺼운 대각선 두 개가 교차하는 "X"를 size x size
    정사각형 안에 근사한다. (MARK_CENTER_X, MARK_CENTER_Y)를 중심으로 그리므로
    size가 줄어도(pulse) 같은 자리에서 작아지는 것처럼 보인다.
    size <= 0 이면 아무것도 그리지 않는다 — 펄스가 "꺼진" 프레임.
    """
    if size <= 0:
        return
    half = size // 2
    x0, y0 = MARK_CENTER_X - half, MARK_CENTER_Y - half
    color = (*MARK_COLOR, 255)
    for v in range(size):
        for u in range(size):
            # 두 대각선(주대각선/부대각선) 근방 픽셀만 칠해 두꺼운 X를 만든다.
            if abs(u - v) <= 1 or abs(u + v - (size - 1)) <= 1:
                put(px, ox, oy, x0 + u, y0 + v, color)


def draw_pet(px, ox, oy, *, body_color, eye_color=EYE_COLOR, dx=0, dy=0, lean_dx=0,
             eye_mode="normal", eye_dx=0, leg_dx=(0, 0, 0, 0)):
    """캐릭터 한 프레임을 그린다.

    dx, dy       : 몸통·눈썹혹·다리·눈 전체에 적용되는 전역 이동 (흔들림/호핑용).
                    dy는 발이 셀 바닥을 벗어나지 않도록 절대 양수가 되지 않는다
                    (호출하는 쪽에서 보장 — 이 함수는 그 값을 그대로 적용만 한다).
    lean_dx       : 몸통·눈썹혹·눈에만 더해지는 추가 x 이동 (다리는 그대로 두어
                     "몸이 진행 방향으로 살짝 기운" 느낌을 만든다 — Running 전용)
    eye_mode/eye_dx: 눈 표현 방식과, 눈에만 더해지는 추가 x 이동 (Reading 스캔용)
    leg_dx        : 다리 4개 각각에 더해지는 x 이동 (보폭 표현용)
    """
    body = (*body_color, 255)
    rect(px, ox, oy, BODY_X0, BODY_X1, BODY_Y0, BODY_Y1, dx + lean_dx, dy, body)
    rect(px, ox, oy, LEFT_NUB_X0, LEFT_NUB_X1, NUB_Y0, NUB_Y1, dx + lean_dx, dy, body)
    rect(px, ox, oy, RIGHT_NUB_X0, RIGHT_NUB_X1, NUB_Y0, NUB_Y1, dx + lean_dx, dy, body)

    for x0, ldx in zip(LEG_X0S, leg_dx):
        rect(px, ox, oy, x0, x0 + 1, LEG_Y0, LEG_Y1, dx + ldx, dy, body)

    draw_eyes(px, ox, oy, dx + lean_dx + eye_dx, dy, eye_mode, eye_color)


# ---------------------------------------------------------------------------
# 상태별 애니메이션 — 8프레임에 걸친 파라미터 표.
# 몸통 색은 행(row)마다 ROW_BODY_COLORS로 고정되고, 그 안에서 움직임/표정으로
# 프레임을 구별한다.
# ---------------------------------------------------------------------------

# 0 Idle — 완만한 상하 bob(항상 위로만) + 이따금 눈을 깜빡임.
IDLE_DY = (0, -1, -2, -3, -2, -1, 0, 0)
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


# 2 Writing — 짧고 빠른 bob(항상 위로만) + 다리가 빠르게 번갈아 두드리듯 움직인다.
# bob은 주기 2, 다리는 주기 3으로 서로 엇갈리게 해서 프레임마다 조합이
# 계속 바뀌게 한다 (같은 주기로 묶으면 프레임 절반이 서로 동일해진다).
WRITING_DY = (0, -1, 0, -1, 0, -1, 0, -1)
WRITING_LEG_SETS = ((0, 0, 0, 0), (1, -1, 1, -1), (-1, 1, -1, 1))


def writing_frame(col):
    return dict(dy=WRITING_DY[col], leg_dx=WRITING_LEG_SETS[col % 3])


# 3 Running — 더 큰 보폭(다리가 크게 벌어짐)과 진행 방향으로의 살짝 기울임.
# bob은 항상 위로만 움직인다.
RUNNING_DY = (0, -1, -2, -3, -3, -2, -1, 0)
RUNNING_LEG_SETS = ((-2, -2, 2, 2), (1, 1, -1, -1), (2, 2, -2, -2), (1, 1, -1, -1))


def running_frame(col):
    return dict(dy=RUNNING_DY[col], leg_dx=RUNNING_LEG_SETS[col % 4], lean_dx=1)


# 4 Error — 눈은 항상 질끈 감은 가로 막대(wince), 몸 전체가 좌우로 떨린다.
# 좌우 흔들림(dx)만 쓰므로 바닥 접지에는 영향이 없다.
ERROR_DX = (0, 1, -1, 2, -2, 1, -1, 0)


def error_frame(col):
    return dict(dx=ERROR_DX[col], eye_mode="wince")


# 5 NeedsYou — 가장 중요한 신호이므로 한눈에 다른 상태와 착각할 수 없어야
# 한다. 강렬한 빨강 몸통 + 밝은 눈 + 가장 큰 호핑(dy 최대 -4, 항상 위로만) +
# 위쪽 여백에 펄스하는 화남 표시(💢)를 함께 쓴다. dy와 mark_size 위상이 서로
# 다른 곡선이라 8프레임 전부가 서로 다른 그림이 되고, 호핑 정점(col=3)에서
# 화남 표시도 풀사이즈라 "정점에서 몸/표시가 모두 셀 안에 있는지"를 같은
# 프레임에서 확인할 수 있다. 시작/끝 프레임(col 0/7)은 mark_size를 다르게
# 두어(0 vs 3) 루프가 닫힐 때도 두 프레임이 픽셀 단위로 동일해지지 않게 한다.
NEEDSYOU_DY = (0, -1, -2, -4, -3, -2, -1, 0)
NEEDSYOU_MARK_SIZE = (0, 5, 9, 9, 9, 7, 5, 3)


def needsyou_frame(col):
    return dict(dy=NEEDSYOU_DY[col], mark_size=NEEDSYOU_MARK_SIZE[col])


ROW_FRAME_FNS = (
    idle_frame,      # 0 Idle
    reading_frame,   # 1 Reading
    writing_frame,   # 2 Writing
    running_frame,   # 3 Running
    error_frame,     # 4 Error
    needsyou_frame,  # 5 NeedsYou
)
assert len(ROW_FRAME_FNS) == ROWS, "행 함수 개수가 PetState 값 개수와 다름"
assert len(ROW_BODY_COLORS) == ROWS, "행 색상 개수가 PetState 값 개수와 다름"
assert len(ROW_EYE_COLORS) == ROWS, "행 눈 색상 개수가 PetState 값 개수와 다름"

NEEDSYOU_ROW = 5  # PetState.NeedsYou — 화남 표시는 이 행에만 그린다.


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
        body_color = ROW_BODY_COLORS[row]
        eye_color = ROW_EYE_COLORS[row]
        for col in range(COLS):
            params = dict(frame_fn(col))
            mark_size = params.pop("mark_size", 0)
            ox, oy = col * FRAME, row * FRAME
            draw_pet(px, ox, oy, body_color=body_color, eye_color=eye_color, **params)
            if row == NEEDSYOU_ROW:
                draw_anger_mark(px, ox, oy, mark_size)

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "pet.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
