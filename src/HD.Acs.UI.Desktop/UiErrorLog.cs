using Avalonia.Threading;

namespace HD.Acs.UI.Desktop;

/// <summary>
/// UI 의 처리되지 않은 예외를 파일로 남기고, UI 스레드 예외로는 앱이 꺼지지 않게 한다.
/// 2026-11-24 현장에서 푸시 처리 중 예외 하나로 운영 UI 가 종료됐고, UI 로그 파일이 없어 이벤트 로그로만 원인을 찾았다.
/// 로그: 실행 폴더의 logs\ui-errors-yyyyMMdd.log (쓰기 실패 시 %LOCALAPPDATA%\HD.Acs\logs).
/// </summary>
internal static class UiErrorLog
{
    private static readonly object Gate = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // 푸시·바인딩 처리 중 예외 — 기록하고 계속 운영한다(관제 화면이 꺼지면 진행 상황을 볼 수 없다)
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write("UI 스레드 예외(처리하고 계속)", e.Exception);
            e.Handled = true;
        };
        // 백그라운드 스레드 예외 — 프로세스 종료는 막을 수 없으니 남기기만 한다
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.IsTerminating ? "처리되지 않은 예외(종료)" : "처리되지 않은 예외", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("관찰되지 않은 Task 예외", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string title, Exception? ex)
    {
        var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [ERR] {title}{Environment.NewLine}{ex}{Environment.NewLine}";
        var name = $"ui-errors-{DateTime.Now:yyyyMMdd}.log";
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "logs"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD.Acs", "logs"),
                 })
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, name), text);
                }
                return;
            }
            catch { /* 다음 위치로 */ }
        }
    }
}
