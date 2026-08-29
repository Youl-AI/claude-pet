"""README 상태 표용 GIF 추출기.

스프라이트 시트(src/PetApp/assets/pet.png)의 각 행을 잘라 상태별 반복 GIF를
docs/images/states/ 에 만든다. 행 순서·프레임 크기는 spritegen.py / SpriteSheet.cs
와 같은 값을 손으로 맞춰 두었다 — 시트 구조를 바꾸면 여기도 같이 바꿀 것.

배율은 최근접 보간 4배(32→128px). README 에서는 width 로 줄여 표시하므로
고해상도 화면에서도 픽셀이 뭉개지지 않는다. 프레임 간격 80ms 는 앱의 12fps
(83ms/프레임)에 GIF 규격이 허용하는 10ms 배수로 가장 가까운 값이다.

배경은 투명이 아니라 밝은 단색을 굽는다. GitHub 다크 테마에서 거의 검정인
Abandoned 몸통이 투명 배경 위에서는 보이지 않기 때문이다. 같은 이유로 GIF
1비트 알파의 가장자리 계단도 사라진다.
"""

from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parents[2]
SHEET = REPO / "src" / "PetApp" / "assets" / "pet.png"
OUT = REPO / "docs" / "images" / "states"

FRAME = 32
COLUMNS = 8
SCALE = 4
DURATION_MS = 80

# 시트 행 순서 (spritegen.py ROW_BODY_COLORS 와 동일).
STATES = (
    "idle", "reading", "writing", "running", "error",
    "yourturn", "blocked", "abandoned", "sleeping",
)

# README 의 어느 테마에서든 모든 상태가 보이는 중립 배경색.
BACKGROUND = (240, 240, 235)


def to_gif_frame(rgba: Image.Image) -> Image.Image:
    ground = Image.new("RGBA", rgba.size, (*BACKGROUND, 255))
    ground.alpha_composite(rgba)
    return ground.convert("RGB").convert("P", palette=Image.ADAPTIVE, colors=256)


def main() -> None:
    sheet = Image.open(SHEET).convert("RGBA")
    assert sheet.size == (FRAME * COLUMNS, FRAME * len(STATES)), sheet.size
    OUT.mkdir(parents=True, exist_ok=True)

    for row, name in enumerate(STATES):
        frames = []
        for col in range(COLUMNS):
            box = (col * FRAME, row * FRAME, (col + 1) * FRAME, (row + 1) * FRAME)
            cell = sheet.crop(box).resize((FRAME * SCALE, FRAME * SCALE), Image.NEAREST)
            frames.append(to_gif_frame(cell))

        path = OUT / f"{name}.gif"
        frames[0].save(
            path,
            save_all=True,
            append_images=frames[1:],
            duration=DURATION_MS,
            loop=0,
        )
        print(f"{path.relative_to(REPO)}  ({path.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
