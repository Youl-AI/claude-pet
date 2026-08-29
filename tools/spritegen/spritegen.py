"""빌드 타임 스프라이트 생성기. 런타임에는 실행되지 않는다.

산출물: 32x32 프레임 8개 x 6행 = 256x192 PNG.
행 순서는 PetCore.PetState enum 순서와 반드시 일치해야 한다
(Idle, Reading, Writing, Running, Error, NeedsYou).

캐릭터: 납작한 픽셀 아트, 음영/외곽선 없음. 넓은 직사각형 몸통 하나가 머리를
겸하고, 눈썹혹 두 개, 눈 두 개, 다리 네 개(좌 2 + 우 2, 가운데 간격)로 이뤄진
실루엣은 모든 상태에서 그대로 유지한다. 상태 구분은 몸통 색 + 움직임/표정으로
한다 — 상태별 색을 다시 도입했다(사용자 피드백).

창은 작업 영역 바닥에 딱 붙고, 32x32 스프라이트를 2배로 늘려 64x64 창을
채운다. 그래서 "발이 셀의 마지막 행(y=31)에 있는가"가 곧 "바닥과 발 사이에
틈이 없는가"와 정확히 같은 말이 된다. 발이 y=29에서 끝나던 이전 배치는 30,
31행이 비어 화면에서 4px 공중부양으로 보였다 — 이번에 몸 전체를 2행 더
내려 다리가 y 28..31을 차지하도록 고쳤다.

**bob(상하 움직임)이 그 수정을 몰래 무효화하지 않도록**: 몸 전체를 위로
평행이동하는 대신, 다리를 "늘였다 줄였다" 한다. bob 값 b(0/1/2)에 대해
몸통·눈썹혹·눈은 y를 b만큼 위로 옮기고, 다리는 위쪽 끝만 b만큼 끌어올린 채
아래쪽 끝(발)은 항상 y=31에 고정한다 — 다리가 늘어나는 것처럼 보이고, 발은
절대 바닥을 벗어나지 않는다. 예외는 NeedsYou의 호핑뿐: 정점 프레임에서는
발을 포함한 캐릭터 전체가 진짜로 떠오르지만, 대부분의 프레임에서는 발이
다시 y=31에 붙는다.

Python 표준 라이브러리만 사용한다 (struct, zlib, pathlib) — Pillow 없음.
"""
import struct
import zlib
from pathlib import Path

FRAME = 32
COLS = 8
ROWS = 9
W, H = FRAME * COLS, FRAME * ROWS

# --- 레스트 포즈 지오메트리 (셀 기준 상대좌표, 양끝 포함 구간) ---
# 발이 셀의 마지막 행(y=31)에 닿도록 이전 배치보다 몸 전체를 2행 더 내렸다.
# 폭/모양은 그대로다.
BODY_X0, BODY_X1 = 6, 25    # 20 wide
BODY_Y0, BODY_Y1 = 15, 27   # 13 tall

LEFT_NUB_X0, LEFT_NUB_X1 = 4, 5
RIGHT_NUB_X0, RIGHT_NUB_X1 = 26, 27
NUB_Y0, NUB_Y1 = 21, 24

LEFT_EYE_X0, LEFT_EYE_X1 = 10, 11
RIGHT_EYE_X0, RIGHT_EYE_X1 = 21, 22
EYE_Y0, EYE_Y1 = 18, 21

LEG_Y0, LEG_Y1 = 28, 31
LEG_X0S = (7, 11, 19, 23)  # 다리 4개의 왼쪽 x. 각 다리는 2px 폭(x0, x0+1).
                            # 왼쪽 쌍 7/11, 오른쪽 쌍 19/23 — 가운데 12..18 은 빈 간격.

# 골격 상수가 미래에 바뀌어도 다리가 몸통 폭을 벗어나지 않는지, 발이 정확히
# 프레임의 마지막 행(y=31)에 있는지 미리 확인한다.
assert all(BODY_X0 <= x0 and x0 + 1 <= BODY_X1 for x0 in LEG_X0S), "다리가 몸통 폭을 벗어남"
assert LEG_Y1 == FRAME - 1, "발이 프레임의 마지막 행(바닥)에 있어야 함"

# --- 색상: 상태별 몸통 색 (PetState enum 순서와 반드시 일치) ---
ROW_BODY_COLORS = (
    (214, 132, 90),   # 0 Idle      — 산호주황, "평상시" 기준색
    (120, 180, 240),  # 1 Reading   — 파랑
    (140, 225, 150),  # 2 Writing   — 초록
    (250, 190, 70),   # 3 Running   — 호박색
    (150, 150, 165),  # 4 Error     — 채도 낮은 회청색, 멍한 느낌
    (214, 132, 90),   # 5 YourTurn  — Idle과 같은 산호주황(의도된 동일색)
    (230, 70, 60),    # 6 Blocked   — 강렬한 빨강
    (52, 50, 60),     # 7 Abandoned — 거의 검정. 순수 검정을 쓰지 않는 이유는
                      #   어두운 배경화면 위에서 형체가 아예 사라지기 때문이다.
    (96, 100, 122),   # 8 Sleeping  — 흐린 청회색. 검정(Abandoned)과도, Error 회청과도
                      #   구별된다. 리셋까지 강제 휴식이라는 "꺼짐"이 아니라 "잠듦".
)

EYE_COLOR = (26, 26, 26)  # 거의 검은 눈 — 밝은 몸통용 기본값
# Blocked는 몸통이 진한 빨강이라 검은 눈이 묻힌다. 이 상태가 가장 중요한
# 신호이므로 눈에도 "번뜩이는" 대비를 준다.
BLOCKED_EYE_COLOR = (255, 224, 179)
# Abandoned는 몸통이 거의 검정이라 반대로 밝은 눈이 필요하다. 다만 자고
# 쓰러진 느낌이어야 하므로 채도 없는 흐린 회색으로 낮춘다.
ABANDONED_EYE_COLOR = (146, 144, 158)
ROW_EYE_COLORS = (
    EYE_COLOR, EYE_COLOR, EYE_COLOR, EYE_COLOR, EYE_COLOR,
    EYE_COLOR,             # 5 YourTurn — 산호주황 위 검은 눈
    BLOCKED_EYE_COLOR,     # 6 Blocked
    ABANDONED_EYE_COLOR,   # 7 Abandoned
    ABANDONED_EYE_COLOR,   # 8 Sleeping
)

# --- 쓰러진 자세(Abandoned 전용) 지오메트리 ---
# 서 있는 포즈와 달리 다리가 없다. 몸이 바닥에 눌려 납작하게 퍼진 형태로,
# 폭은 넓어지고 높이는 절반 이하가 된다. 아래쪽 끝은 서 있을 때의 발과 같은
# y=31 이라 "바닥에 붙어 있다"는 접지감이 그대로 유지된다.
LY_BODY_X0, LY_BODY_X1 = 4, 27
LY_BODY_Y0, LY_BODY_Y1 = 20, 31
LY_LEFT_NUB_X0, LY_LEFT_NUB_X1 = 2, 3
LY_RIGHT_NUB_X0, LY_RIGHT_NUB_X1 = 28, 29
LY_NUB_Y0, LY_NUB_Y1 = 24, 29
LY_LEFT_EYE_X0, LY_LEFT_EYE_X1 = 9, 12
LY_RIGHT_EYE_X0, LY_RIGHT_EYE_X1 = 19, 22
LY_EYE_Y = 24

assert LY_BODY_Y1 == FRAME - 1, "쓰러진 몸의 아래쪽 끝도 바닥(y=31)에 붙어야 함"

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


# --- 머리 위 마크: 빠직(Blocked) / 물음표(YourTurn) ---
# 마크는 몸통이 아니라 그 위의 빈 공간(투명)에 뜬다. 즉 배경화면이 바로
# 뒤에 보이므로, 단색으로만 그리면 비슷한 밝기의 배경에서 사라진다.
# 그래서 두 마크 모두 1px 외곽선을 두른다 — 밝은 배경에서도 어두운
# 배경에서도 형태가 읽힌다.
MARK_CENTER_X, MARK_CENTER_Y = 23, 7

ANGER_COLOR, ANGER_OUTLINE = (226, 46, 40), (74, 12, 12)
QUESTION_COLOR, QUESTION_OUTLINE = (252, 246, 236), (62, 42, 30)


def _put_clipped(px, ox, oy, x, y, color):
    """마크 전용 쓰기. 몸통은 put()의 assert로 셀 밖 침범을 원천 차단하지만,
    마크는 펄스로 커졌다 작아지고 외곽선이 한 겹 더 붙어서 셀 경계에 닿을 수
    있다. 마크가 잘리는 것은 허용 가능한 결과이므로(형태는 그대로 읽힌다)
    여기서는 assert 대신 조용히 잘라낸다."""
    if 0 <= x < FRAME and 0 <= y < FRAME:
        px[oy + y][ox + x] = color


def _draw_marked_cells(px, ox, oy, cells, color, outline):
    oc = (*outline, 255)
    for (x, y) in cells:
        for ny in (-1, 0, 1):
            for nx in (-1, 0, 1):
                if (x + nx, y + ny) not in cells:
                    _put_clipped(px, ox, oy, x + nx, y + ny, oc)
    mc = (*color, 255)
    for (x, y) in cells:
        _put_clipped(px, ox, oy, x, y, mc)


def _rotate90(cells, times):
    out = set()
    for (x, y) in cells:
        for _ in range(times):
            x, y = -y, x
        out.add((x, y))
    return out


def draw_anger_mark(px, ox, oy, size):
    """빠직(💢): 'ㄴ' 모양 직각 획 네 개를 동·서·남·북 네 방위에 하나씩 두고,
    각각 90도씩 돌려 중심을 둘러싸는 바람개비를 만든다.

    size는 펄스용이다. 0이면 아무것도 그리지 않고(꺼진 프레임), 값이 줄면
    획 길이와 중심에서의 거리가 함께 줄어 같은 자리에서 작아지는 것처럼 보인다.
    """
    if size <= 0:
        return
    arm = max(1, round(size * 0.34))    # 획 하나의 길이
    rad = max(1, round(size * 0.30))    # 중심에서 꺾이는 모서리까지의 거리
    thick = 2 if size >= 7 else 1       # 작아지면 1px로 얇아진다

    # 기준 'ㄴ': 꺾이는 모서리를 원점에 두고 위로 올라갔다가 오른쪽으로 꺾인다.
    base = set()
    for t in range(thick):
        for i in range(arm + 1):
            base.add((t, -i))   # 세로획
            base.add((i, t))    # 가로획

    cells = set()
    for k, (ux, uy) in enumerate(((0, -1), (1, 0), (0, 1), (-1, 0))):
        for (x, y) in _rotate90(base, k):
            cells.add((MARK_CENTER_X + x + ux * rad, MARK_CENTER_Y + y + uy * rad))

    _draw_marked_cells(px, ox, oy, cells, ANGER_COLOR, ANGER_OUTLINE)


QUESTION_GLYPH = (
    ".####.",
    "##..##",
    "....##",
    "...##.",
    "..##..",
    "..##..",
    "......",
    "..##..",
    "..##..",
)


def draw_question_mark(px, ox, oy, dy):
    """YourTurn 전용: 머리 위에 뜬 물음표. dy로 위아래로 살짝 떠다녀
    "가만히 서서 기다리는" 느낌을 준다."""
    w, h = len(QUESTION_GLYPH[0]), len(QUESTION_GLYPH)
    x0 = MARK_CENTER_X - w // 2
    y0 = MARK_CENTER_Y - h // 2 + dy
    cells = {
        (x0 + u, y0 + v)
        for v, line in enumerate(QUESTION_GLYPH)
        for u, ch in enumerate(line)
        if ch == "#"
    }
    _draw_marked_cells(px, ox, oy, cells, QUESTION_COLOR, QUESTION_OUTLINE)


def draw_pet_lying(px, ox, oy, *, body_color, eye_color, squash=0):
    """Abandoned 전용 포즈: 다리 없이 바닥에 납작하게 퍼져 누운 몸.
    squash는 위쪽에서 눌리는 양(0/1) — 아주 느린 호흡처럼 보이게 한다."""
    body = (*body_color, 255)
    rect(px, ox, oy, LY_BODY_X0, LY_BODY_X1, LY_BODY_Y0 + squash, LY_BODY_Y1, 0, 0, body)
    rect(px, ox, oy, LY_LEFT_NUB_X0, LY_LEFT_NUB_X1, LY_NUB_Y0 + squash, LY_NUB_Y1, 0, 0, body)
    rect(px, ox, oy, LY_RIGHT_NUB_X0, LY_RIGHT_NUB_X1, LY_NUB_Y0 + squash, LY_NUB_Y1, 0, 0, body)
    # 눈은 항상 감겨 있다 — 가로 막대 한 줄.
    ec = (*eye_color, 255)
    ey = LY_EYE_Y + squash
    rect(px, ox, oy, LY_LEFT_EYE_X0, LY_LEFT_EYE_X1, ey, ey, 0, 0, ec)
    rect(px, ox, oy, LY_RIGHT_EYE_X0, LY_RIGHT_EYE_X1, ey, ey, 0, 0, ec)


def draw_pet(px, ox, oy, *, body_color, eye_color=EYE_COLOR, bob=0, lift=0, dx=0,
             lean_dx=0, eye_mode="normal", eye_dx=0, leg_dx=(0, 0, 0, 0)):
    """캐릭터 한 프레임을 그린다.

    bob           : 웅크림/뻗음 정도(0/1/2, 항상 >= 0). 몸통·눈썹혹·눈은 이
                     값만큼 위로 이동하지만, 다리는 "늘어난다" — 다리 위쪽 끝만
                     bob만큼 끌어올려지고 아래쪽 끝(발)은 그대로 LEG_Y1에
                     남는다. 그래서 몸이 떠 있는 것처럼 보이지 않고, 다리를
                     늘였다 줄였다 하며 웅크리고 뻗는 것처럼 보인다.
    lift          : 발을 포함해 캐릭터 전체를 진짜로 들어올리는 값(<=0). 다리의
                     위/아래 끝 모두에 그대로 더해지므로 발이 바닥(y=31)을
                     벗어난다 — NeedsYou의 호핑 정점 프레임에만 쓴다.
    dx            : 몸통·눈썹혹·다리·눈 전체에 적용되는 좌우 이동 (Error의 흔들림용).
    lean_dx       : 몸통·눈썹혹·눈에만 더해지는 추가 x 이동 (다리는 그대로 두어
                     "몸이 진행 방향으로 살짝 기운" 느낌을 만든다 — Running 전용)
    eye_mode/eye_dx: 눈 표현 방식과, 눈에만 더해지는 추가 x 이동 (Reading 스캔용)
    leg_dx        : 다리 4개 각각에 더해지는 x 이동 (보폭 표현용)
    """
    body = (*body_color, 255)
    dy = lift - bob  # 몸통/눈썹혹/눈에 적용되는 수직 이동: 웅크림 + (있다면) 실제 호핑

    rect(px, ox, oy, BODY_X0, BODY_X1, BODY_Y0, BODY_Y1, dx + lean_dx, dy, body)
    rect(px, ox, oy, LEFT_NUB_X0, LEFT_NUB_X1, NUB_Y0, NUB_Y1, dx + lean_dx, dy, body)
    rect(px, ox, oy, RIGHT_NUB_X0, RIGHT_NUB_X1, NUB_Y0, NUB_Y1, dx + lean_dx, dy, body)

    # 다리: 위쪽 끝만 bob으로 끌어올리고, 전체를 lift로 (보통 0으로) 이동한다.
    # lift == 0 이면 아래쪽 끝은 항상 LEG_Y1(=31) — 발이 절대 바닥을 안 뜬다.
    leg_top = LEG_Y0 - bob
    for x0, ldx in zip(LEG_X0S, leg_dx):
        rect(px, ox, oy, x0, x0 + 1, leg_top, LEG_Y1, dx + ldx, lift, body)

    draw_eyes(px, ox, oy, dx + lean_dx + eye_dx, dy, eye_mode, eye_color)


# ---------------------------------------------------------------------------
# 상태별 애니메이션 — 8프레임에 걸친 파라미터 표.
# 몸통 색은 행(row)마다 ROW_BODY_COLORS로 고정되고, 그 안에서 움직임/표정으로
# 프레임을 구별한다.
# ---------------------------------------------------------------------------

# 0 Idle — 완만한 웅크림/뻗음(bob) + 이따금 눈을 깜빡임. 발은 항상 y=31.
IDLE_BOB = (0, 1, 2, 1, 0, 0, 0, 0)
IDLE_BLINK_COLS = {4, 5}  # "a frame or two" — 두 프레임 연속으로 감았다 뜬다


def idle_frame(col):
    return dict(
        bob=IDLE_BOB[col],
        eye_mode="blink" if col in IDLE_BLINK_COLS else "normal",
    )


# 1 Reading — 몸은 가만히, 눈이 몸통 안에서 좌우로 스캔하듯 움직인다.
READING_EYE_DX = (0, -1, -2, -1, 0, 1, 2, 1)


def reading_frame(col):
    return dict(eye_dx=READING_EYE_DX[col])


# 2 Writing — 짧고 빠른 bob(웅크림) + 다리가 빠르게 번갈아 두드리듯 움직인다.
# bob은 주기 2, 다리는 주기 3으로 서로 엇갈리게 해서 프레임마다 조합이
# 계속 바뀌게 한다 (같은 주기로 묶으면 프레임 절반이 서로 동일해진다).
WRITING_BOB = (0, 1, 0, 1, 0, 1, 0, 1)
WRITING_LEG_SETS = ((0, 0, 0, 0), (1, -1, 1, -1), (-1, 1, -1, 1))


def writing_frame(col):
    return dict(bob=WRITING_BOB[col], leg_dx=WRITING_LEG_SETS[col % 3])


# 3 Running — 더 큰 보폭(다리가 크게 벌어짐)과 진행 방향으로의 살짝 기울임,
# 그리고 가장 큰 웅크림/뻗음(bob)으로 달리는 탄력을 표현한다.
RUNNING_BOB = (0, 1, 2, 2, 2, 2, 1, 0)
RUNNING_LEG_SETS = ((-2, -2, 2, 2), (1, 1, -1, -1), (2, 2, -2, -2), (1, 1, -1, -1))


def running_frame(col):
    return dict(bob=RUNNING_BOB[col], leg_dx=RUNNING_LEG_SETS[col % 4], lean_dx=1)


# 4 Error — 눈은 항상 질끈 감은 가로 막대(wince), 몸 전체가 좌우로 떨린다.
# 좌우 흔들림(dx)만 쓰므로 바닥 접지에는 영향이 없다 (bob=0 고정).
ERROR_DX = (0, 1, -1, 2, -2, 1, -1, 0)


def error_frame(col):
    return dict(dx=ERROR_DX[col], eye_mode="wince")


# 5 YourTurn — 턴이 끝나 사람 차례. Idle과 몸통 색이 같으므로(의도된 선택)
# 색만으로는 구분되지 않는다. 그래서 두 가지로 갈라 놓는다: 머리 위에 떠 있는
# 물음표, 그리고 "제자리에 서서 기다리는" 움직임(Idle은 배회한다).
YOURTURN_BOB = (0, 0, 1, 1, 0, 0, 0, 0)
YOURTURN_Q_DY = (0, 0, -1, -1, 0, 0, 1, 0)


def yourturn_frame(col):
    return dict(bob=YOURTURN_BOB[col], question_dy=YOURTURN_Q_DY[col])


# 6 Blocked — 권한 승인 대기. 클로드가 실제로 멈춰서 사람 없이는 진행할 수
# 없는 유일한 상태이므로 가장 강한 신호를 준다: 빨강 몸통 + 밝은 눈 + 진짜로
# 발이 바닥을 떠나는 호핑 + 펄스하는 빠직.
#
# 이 상태의 점프는 "진짜로 떠야" 하므로 bob이 아니라 lift를 쓴다 — lift는
# 발까지 포함해 캐릭터 전체를 들어올린다. 그래도 8프레임 중 과반(col
# 0,1,5,6,7)은 lift=0으로 발이 y=31에 붙어 있고, 도약 정점 근처(col 2,3,4)만
# 실제로 뜬다. col 1/6은 도약 직전/직후의 웅크림이라 착지가 "쿵" 눌리는
# 느낌을 준다.
BLOCKED_BOB = (0, 2, 0, 0, 0, 0, 2, 0)
BLOCKED_LIFT = (0, 0, -2, -4, -2, 0, 0, 0)
BLOCKED_MARK_SIZE = (0, 5, 9, 9, 9, 7, 5, 3)


def blocked_frame(col):
    return dict(
        bob=BLOCKED_BOB[col],
        lift=BLOCKED_LIFT[col],
        mark_size=BLOCKED_MARK_SIZE[col],
    )


# 7 Abandoned — 60초 방치. 기다리다 지쳐 쓰러진 모습. 검게 가라앉은 몸이
# 바닥에 납작하게 눌려 있고, 아주 느린 호흡(squash)만 남는다. 다리도 눈도
# 없어서 다른 어떤 상태와도 실루엣이 겹치지 않는다.
ABANDONED_SQUASH = (0, 0, 0, 1, 1, 1, 1, 0)


def abandoned_frame(col):
    return dict(lying=True, squash=ABANDONED_SQUASH[col])


# 8 Sleeping — 토큰 한도. 리셋까지 강제 휴식. Abandoned와 같은 누운 몸이지만
# 색이 청회색이고 머리 위로 Z가 떠오른다. 8프레임 순환: 작은 Z가 몸 가까이서
# 나타나 위로 떠오르며 커지고, 큰 Z가 옅어지듯 사라진다. 프레임마다 Z의
# 위치/조합이 달라 어느 두 프레임도 같지 않다.
Z_SMALL = (
    "111",
    "010",
    "111",
)
Z_BIG = (
    "11111",
    "00010",
    "00100",
    "01000",
    "11111",
)

# (dx, dy, glyph) — 몸 오른쪽 위(코 근처 x=22, 몸 윗변 y=20 기준)에서의 상대 위치.
# 위로 갈수록(작은 dy) 나중 단계다.
SLEEP_Z_FRAMES = (
    ((22, 14, Z_SMALL),),
    ((21, 12, Z_SMALL),),
    ((20, 10, Z_SMALL), (25, 15, Z_SMALL)),
    ((19, 7, Z_BIG), (24, 13, Z_SMALL)),
    ((18, 5, Z_BIG), (23, 11, Z_SMALL)),
    ((17, 3, Z_BIG), (22, 9, Z_SMALL)),
    ((16, 1, Z_BIG),),
    ((22, 15, Z_SMALL), (17, 2, Z_BIG)),
)
assert len(SLEEP_Z_FRAMES) == COLS
assert len(set(SLEEP_Z_FRAMES)) == COLS, "낮잠 Z 프레임이 전부 서로 달라야 함"

Z_COLOR = (222, 226, 240)  # 몸보다 밝은 청백색 — 어두운 배경에서도 뜬다.


def draw_z_glyph(px, ox, oy, gx, gy, glyph):
    for row_i, row in enumerate(glyph):
        for col_i, ch in enumerate(row):
            if ch == "1":
                put(px, ox, oy, gx + col_i, gy + row_i, Z_COLOR + (255,))


def sleeping_frame(col):
    return dict(lying=True, squash=ABANDONED_SQUASH[col], zs=SLEEP_Z_FRAMES[col])


ROW_FRAME_FNS = (
    idle_frame,       # 0 Idle
    reading_frame,    # 1 Reading
    writing_frame,    # 2 Writing
    running_frame,    # 3 Running
    error_frame,      # 4 Error
    yourturn_frame,   # 5 YourTurn
    blocked_frame,    # 6 Blocked
    abandoned_frame,  # 7 Abandoned
    sleeping_frame,   # 8 Sleeping
)
assert len(ROW_FRAME_FNS) == ROWS, "행 함수 개수가 PetState 값 개수와 다름"
assert len(ROW_BODY_COLORS) == ROWS, "행 색상 개수가 PetState 값 개수와 다름"
assert len(ROW_EYE_COLORS) == ROWS, "행 눈 색상 개수가 PetState 값 개수와 다름"


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
            # 오버레이/포즈 선택은 프레임 파라미터에서 꺼내 쓴다 — 행 번호를
            # 하드코딩하면 행이 늘어날 때마다 여기도 같이 고쳐야 한다.
            mark_size = params.pop("mark_size", 0)
            question_dy = params.pop("question_dy", None)
            lying = params.pop("lying", False)
            zs = params.pop("zs", ())

            ox, oy = col * FRAME, row * FRAME
            if lying:
                draw_pet_lying(px, ox, oy, body_color=body_color,
                               eye_color=eye_color, **params)
            else:
                draw_pet(px, ox, oy, body_color=body_color,
                         eye_color=eye_color, **params)

            if mark_size:
                draw_anger_mark(px, ox, oy, mark_size)
            if question_dy is not None:
                draw_question_mark(px, ox, oy, question_dy)
            for gx, gy, glyph in zs:
                draw_z_glyph(px, ox, oy, gx, gy, glyph)

    out = Path(__file__).resolve().parents[2] / "src" / "PetApp" / "assets" / "pet.png"
    write_png(out, px)
    print(f"wrote {out} ({W}x{H})")


if __name__ == "__main__":
    main()
