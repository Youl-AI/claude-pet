using System;
using System.Windows;
using System.Windows.Media.Imaging;
using PetCore;

namespace PetApp;

/// <summary>
/// 32x32 프레임 8개 x 9행. 행 순서는 PetState enum 순서와 일치한다.
/// 모든 크롭은 생성자에서 한 번만 만들어 캐시한다 — 12fps로 하루 종일 도는
/// 렌더 루프에서 매 틱마다 CroppedBitmap을 새로 할당하지 않기 위해서다.
/// </summary>
internal sealed class SpriteSheet
{
    public const int FrameSize = 32;
    public const int Columns = 8;
    private const int Rows = 9; // pet.png 실제 행 수 (256x288 / 32 = 8x9), PetState 값 개수와 일치

    private readonly CroppedBitmap[,] _frames;

    public SpriteSheet()
    {
        // Rows가 PetState 값 개수와 어긋나면 (int)state % Rows가 조용히 기존
        // 행으로 랩어라운드해서, 새로 추가된 상태가 크래시도 에러도 없이
        // 영원히 남의 그림으로 렌더링된다. 이 생성자는 앱 시작 시 딱 한 번만
        // 실행되고 12fps 렌더 루프 안에서는 절대 실행되지 않으므로, 여기서
        // fail-fast로 던지는 것은 "렌더 루프는 절대 던지지 않는다"는 계약을
        // 조금도 약화시키지 않는다 — 이 예외는 애니메이션이 실제로 시작되기
        // 전, 프로세스 초기화 단계에서만 발생할 수 있다.
        var expectedRows = Enum.GetValues<PetState>().Length;
        if (Rows != expectedRows)
        {
            throw new InvalidOperationException(
                $"SpriteSheet.Rows ({Rows})가 PetState 값 개수({expectedRows})와 다릅니다. " +
                "pet.png와 이 상수를 함께 갱신하세요.");
        }

        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.CacheOption = BitmapCacheOption.OnLoad; // 즉시 디코드해 Freeze 가능하게 함
        sheet.UriSource = new Uri("pack://application:,,,/assets/pet.png", UriKind.Absolute);
        sheet.EndInit();
        sheet.Freeze();

        _frames = new CroppedBitmap[Rows, Columns];
        for (var row = 0; row < Rows; row++)
        {
            // 같은 행 안에서 그림이 완전히 동일한 열들(예: 팔레트/자세가
            // 같은 프레임을 애니메이션 속도 조절용으로 중복 배치한 경우)은
            // 같은 CroppedBitmap 인스턴스를 공유하도록 캐시한다. 이러면
            // PetWindow.Tick()이 매 틱 그림이 "실제로" 바뀌었는지를 참조
            // 동일성(ReferenceEquals)만으로 정확히 판단할 수 있어, 시각적
            // 변화가 없는 틱에서 Sprite.Source 재대입(→레이어드 윈도우
            // 재합성)을 건너뛸 수 있다. 이 비교는 픽셀 값 자체를 보는
            // 것이라 스프라이트 시트가 바뀌어도 항상 올바르게 동작한다 —
            // 오늘의 배치(어느 열이 중복인지)를 하드코딩하지 않는다.
            var rowPixels = new byte[Columns][];
            for (var col = 0; col < Columns; col++)
            {
                var crop = new CroppedBitmap(
                    sheet,
                    new Int32Rect(col * FrameSize, row * FrameSize, FrameSize, FrameSize));
                crop.Freeze();
                rowPixels[col] = ReadPixels(crop);

                CroppedBitmap? reuse = null;
                for (var prior = 0; prior < col; prior++)
                {
                    if (PixelsEqual(rowPixels[prior], rowPixels[col]))
                    {
                        reuse = _frames[row, prior];
                        break;
                    }
                }

                _frames[row, col] = reuse ?? crop;
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

    private static byte[] ReadPixels(CroppedBitmap crop)
    {
        var bytesPerPixel = (crop.Format.BitsPerPixel + 7) / 8;
        var stride = crop.PixelWidth * bytesPerPixel;
        var buffer = new byte[stride * crop.PixelHeight];
        crop.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    private static bool PixelsEqual(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}
