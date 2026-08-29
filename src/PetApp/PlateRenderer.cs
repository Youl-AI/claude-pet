using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PetApp;

/// <summary>
/// 레벨 표기를 그린다. 펫 왼쪽에 놓이는 "Lv" 접두 + 3x5 픽셀 숫자. 판은 없고,
/// 모든 글리프에 1px 어두운 외곽선을 둘러 밝은 배경에서도 어두운 배경에서도 읽히게 한다.
///
/// 왜 판이 없는가 — 판 있는 시안과 나란히 비교한 결과 사용자가 "덜 거슬리는" 쪽을
/// 골랐다. 외곽선(어두움) + 숫자(밝음)가 명도의 양 극단을 갖고 있어서 판 없이도
/// 임의의 배경에서 분리가 생긴다.
///
/// 왜 "Lv" 접두인가 — 맨 숫자는 그것이 레벨이라는 것을 설명하지 못한다. "Lv"는 게임
/// 문법이라 별도 설명 없이 레벨로 읽힌다. 접두는 살짝 흐린 색으로 눌러 시선이 숫자에
/// 가게 한다.
///
/// 왜 왼쪽인가 — 빠직(MARK_CENTER_X = 23)과 물음표가 셀 오른쪽 위를 쓴다. 오른쪽이나 머리
/// 위에 두면 Blocked / YourTurn 상태에서 겹친다 (스펙 §5.3).
/// </summary>
internal static class PlateRenderer
{
    /// <summary>표기 오른쪽 끝과 펫 왼쪽 첫 픽셀(LEFT_NUB_X0 = 4) 사이의 간격.</summary>
    public const int GapPx = 1;

    public const int PlateHeight = 9;

    private const int DigitWidth = 3;
    private const int DigitHeight = 5;
    private const int DigitSpacing = 1;
    private const int PaddingX = 2;
    private const int PaddingY = 2;

    /// <summary>"Lv" 접두가 차지하는 폭(글리프 7px) + 숫자와의 간격(2px).</summary>
    private const int PrefixWidth = 9;

    private static readonly Color Outline    = Color.FromRgb(0x12, 0x11, 0x16);
    private static readonly Color Ink        = Color.FromRgb(0xFF, 0xFD, 0xF8);
    private static readonly Color PrefixInk  = Color.FromRgb(0xD6, 0xD0, 0xC4);

    /// <summary>3x5 픽셀 숫자. 각 문자열 한 줄이 한 행이고 '1'이 켜진 픽셀이다.</summary>
    private static readonly string[][] Glyphs =
    {
        new[] { "111", "101", "101", "101", "111" }, // 0
        new[] { "010", "110", "010", "010", "111" }, // 1
        new[] { "111", "001", "111", "100", "111" }, // 2
        new[] { "111", "001", "111", "001", "111" }, // 3
        new[] { "101", "101", "111", "001", "001" }, // 4
        new[] { "111", "100", "111", "001", "111" }, // 5
        new[] { "111", "100", "111", "101", "111" }, // 6
        new[] { "111", "001", "001", "001", "001" }, // 7
        new[] { "111", "101", "111", "101", "111" }, // 8
        new[] { "111", "101", "111", "001", "111" }, // 9
    };

    /// <summary>대문자 L(3x5). 접두 첫 글자.</summary>
    private static readonly string[] GlyphL = { "100", "100", "100", "100", "111" };

    /// <summary>소문자 v(3x3, 아래 정렬). x-height 라서 "Lv"가 단어처럼 읽힌다.</summary>
    private static readonly string[] GlyphV = { "000", "000", "101", "101", "010" };

    /// <summary>
    /// 자릿수에 따라 폭이 변한다. 자릿수가 느는 순간은 평생 두 번뿐이다 (스펙 §5.4).
    ///
    /// level을 <see cref="Render"/>와 동일하게 [1, 9999]로 클램프한 뒤 자릿수를 센다 —
    /// 그래야 이 함수가 돌려주는 폭이 Render가 실제로 그리는 픽셀 폭과 항상 일치한다.
    /// 클램프 없이 원본 level의 자릿수를 쓰면, 범위 밖 level(범위 밖 값이 여기까지
    /// 올라오는 일은 없어야 하지만)에서 두 함수가 서로 다른 자릿수를 기준으로 계산해
    /// 표기 폭과 실제로 그려지는 숫자 폭이 어긋난다.
    /// </summary>
    public static int PlateWidthFor(int level)
    {
        var digits = Math.Clamp(level, 1, 9999).ToString().Length;
        var inner = digits * DigitWidth + (digits - 1) * DigitSpacing;
        return PrefixWidth + inner + PaddingX * 2;
    }

    public static BitmapSource Render(int level)
    {
        var text = Math.Clamp(level, 1, 9999).ToString();
        var width = PlateWidthFor(level);
        var height = PlateHeight;

        // 글리프를 먼저 마스크에 찍고, 그 다음 외곽선을 두른다. 색을 바로 찍으면
        // 나중에 찍는 외곽선이 이웃 글리프의 픽셀을 덮어쓸 수 있다.
        var glyphColor = new Color?[width * height];

        void Put(string[] rows, int originX, Color color)
        {
            for (var gy = 0; gy < DigitHeight; gy++)
                for (var gx = 0; gx < DigitWidth; gx++)
                    if (rows[gy][gx] == '1')
                    {
                        var x = originX + gx;
                        var y = PaddingY + gy;
                        if (x >= 0 && y >= 0 && x < width && y < height)
                            glyphColor[y * width + x] = color;
                    }
        }

        Put(GlyphL, PaddingX, PrefixInk);
        Put(GlyphV, PaddingX + 4, PrefixInk);

        var cursorX = PaddingX + PrefixWidth;
        foreach (var ch in text)
        {
            Put(Glyphs[ch - '0'], cursorX, Ink);
            cursorX += DigitWidth + DigitSpacing;
        }

        var pixels = new uint[width * height];

        static uint Bgra(Color c) =>
            (uint)((0xFFu << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var own = glyphColor[y * width + x];
                if (own is { } c) { pixels[y * width + x] = Bgra(c); continue; }

                // 글리프 픽셀의 8방향 이웃이면 외곽선.
                var nearGlyph = false;
                for (var dy = -1; dy <= 1 && !nearGlyph; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height
                            && glyphColor[ny * width + nx] is not null)
                        {
                            nearGlyph = true;
                            break;
                        }
                    }
                if (nearGlyph) pixels[y * width + x] = Bgra(Outline);
            }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, width * 4);
        bitmap.Freeze();   // 렌더 스레드에서 안전하게 쓰려면 얼려야 한다
        return bitmap;
    }
}
