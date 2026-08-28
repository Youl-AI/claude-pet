namespace PetCore;

/// <summary>
/// 화면 좌표계의 사각형. Win32 <c>RECT</c>와 필드 구성이 같지만, 정수 네 개
/// 뿐인 순수 데이터 타입이고 Windows API를 전혀 참조하지 않는다 — 그래서
/// PetCore의 플랫폼 중립성(net10.0, Windows 전용 API 금지)을 해치지 않고
/// 여기 둘 수 있다. 인터롭 RECT는 PetApp.NativeMethods 안에만 머무르고,
/// 이 타입으로 변환한 뒤 넘어온다.
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
