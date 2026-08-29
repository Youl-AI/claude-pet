using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PetApp;

/// <summary>
/// 레벨 명패를 그린다. 펫 왼쪽에 놓이는 단색 판 + 3x5 픽셀 숫자.
///
/// 왜 판을 그리는가 — 마크는 몸통이 아니라 그 위의 투명한 공간에 뜨므로 배경화면이 바로 뒤에
/// 보인다. 외곽선만 두른 맨 숫자는 대부분의 배경에서 읽히지만, 판은 모든 배경에서 읽힌다.
/// 판이 명도의 양 극단을 다 갖고 있어서 밝은 배경에서는 검은 바탕이, 어두운 배경에서는 밝은
/// 테두리가 분리를 만든다 (스펙 §5.2).
///
/// 왜 왼쪽인가 — 빠직(MARK_CENTER_X = 23)과 물음표가 셀 오른쪽 위를 쓴다. 오른쪽이나 머리
/// 위에 두면 Blocked / YourTurn 상태에서 겹친다 (스펙 §5.3).
/// </summary>
internal static class PlateRenderer
{
    /// <summary>판 오른쪽 끝과 펫 왼쪽 첫 픽셀(LEFT_NUB_X0 = 4) 사이의 간격.</summary>
    public const int GapPx = 6;

    public const int PlateHeight = 9;

    private const int DigitWidth = 3;
    private const int DigitHeight = 5;
    private const int DigitSpacing = 1;
    private const int PaddingX = 2;
    private const int PaddingY = 2;

    private static readonly Color Fill   = Color.FromRgb(0x12, 0x11, 0x16);
    private static readonly Color Edge   = Color.FromRgb(0xEE, 0xEA, 0xE2);
    private static readonly Color Ink    = Color.FromRgb(0xFF, 0xFD, 0xF8);

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

    /// <summary>
    /// 자릿수에 따라 폭이 변한다. 자릿수가 느는 순간은 평생 두 번뿐이다 (스펙 §5.4).
    ///
    /// level을 <see cref="Render"/>와 동일하게 [1, 9999]로 클램프한 뒤 자릿수를 센다 —
    /// 그래야 이 함수가 돌려주는 폭이 Render가 실제로 그리는 픽셀 폭과 항상 일치한다.
    /// 클램프 없이 원본 level의 자릿수를 쓰면, 범위 밖 level(범위 밖 값이 여기까지
    /// 올라오는 일은 없어야 하지만)에서 두 함수가 서로 다른 자릿수를 기준으로 계산해
    /// 판 폭과 실제로 그려지는 숫자 폭이 어긋난다.
    /// </summary>
    public static int PlateWidthFor(int level)
    {
        var digits = Math.Clamp(level, 1, 9999).ToString().Length;
        var inner = digits * DigitWidth + (digits - 1) * DigitSpacing;
        return inner + PaddingX * 2;
    }

    public static BitmapSource Render(int level)
    {
        var text = Math.Clamp(level, 1, 9999).ToString();
        var width = PlateWidthFor(level);
        var height = PlateHeight;

        var pixels = new uint[width * height];

        void Set(int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            pixels[y * width + x] = (uint)((0xFFu << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
        }

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Set(x, y, Fill);

        for (var x = 0; x < width; x++) { Set(x, 0, Edge); Set(x, height - 1, Edge); }
        for (var y = 0; y < height; y++) { Set(0, y, Edge); Set(width - 1, y, Edge); }

        var cursorX = PaddingX;
        foreach (var ch in text)
        {
            var glyph = Glyphs[ch - '0'];
            for (var gy = 0; gy < DigitHeight; gy++)
                for (var gx = 0; gx < DigitWidth; gx++)
                    if (glyph[gy][gx] == '1')
                        Set(cursorX + gx, PaddingY + gy, Ink);

            cursorX += DigitWidth + DigitSpacing;
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, width * 4);
        bitmap.Freeze();   // 렌더 스레드에서 안전하게 쓰려면 얼려야 한다
        return bitmap;
    }
}
