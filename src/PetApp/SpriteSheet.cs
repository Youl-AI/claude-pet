using System;
using System.Windows;
using System.Windows.Media.Imaging;
using PetCore;

namespace PetApp;

/// <summary>
/// 32x32 프레임 8개 x 6행. 행 순서는 PetState enum 순서와 일치한다.
/// 모든 크롭은 생성자에서 한 번만 만들어 캐시한다 — 12fps로 하루 종일 도는
/// 렌더 루프에서 매 틱마다 CroppedBitmap을 새로 할당하지 않기 위해서다.
/// </summary>
internal sealed class SpriteSheet
{
    public const int FrameSize = 32;
    public const int Columns = 8;
    private const int Rows = 6; // pet.png 실제 행 수 (256x192 / 32 = 8x6), PetState 값 개수와 일치

    private readonly CroppedBitmap[,] _frames;

    public SpriteSheet()
    {
        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.CacheOption = BitmapCacheOption.OnLoad; // 즉시 디코드해 Freeze 가능하게 함
        sheet.UriSource = new Uri("pack://application:,,,/assets/pet.png", UriKind.Absolute);
        sheet.EndInit();
        sheet.Freeze();

        _frames = new CroppedBitmap[Rows, Columns];
        for (var row = 0; row < Rows; row++)
        {
            for (var col = 0; col < Columns; col++)
            {
                var crop = new CroppedBitmap(
                    sheet,
                    new Int32Rect(col * FrameSize, row * FrameSize, FrameSize, FrameSize));
                crop.Freeze();
                _frames[row, col] = crop;
            }
        }
    }

    /// <summary>
    /// 캐시된 크롭을 반환한다. 행·열 인덱스는 모두 모듈로로 감싸므로
    /// state 캐스팅이나 frameIndex 값이 무엇이든 배열 범위를 벗어날 수 없다.
    /// </summary>
    public CroppedBitmap Frame(PetState state, int frameIndex)
    {
        var row = (((int)state % Rows) + Rows) % Rows;
        var col = ((frameIndex % Columns) + Columns) % Columns;
        return _frames[row, col];
    }
}
