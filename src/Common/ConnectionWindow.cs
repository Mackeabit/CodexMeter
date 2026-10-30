using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexMeter;

public sealed class ConnectionWindow : Window
{
    private readonly TextBlock path = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    private readonly TextBlock startup = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 12) };
    private readonly UsageClient client;
    private readonly Action<string?> savePath;
    private bool busy;
    public ConnectionWindow(UsageClient client, Action<string?> savePath)
    {
        this.client = client; this.savePath = savePath;
        Title = "Codex Meter · 연결 및 자동 실행";
        Width = 530; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White; Foreground = Brushes.Black; FontFamily = new FontFamily("Malgun Gothic"); FontSize = 13;
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "Codex Meter", FontSize = 25, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "v" + Installation.Version + " · " + Installation.Edition + "  ·  연결 및 자동 실행", Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 26) });
        panel.Children.Add(new TextBlock { Text = "Codex 연결", FontWeight = FontWeights.Bold, FontSize = 16 });
        panel.Children.Add(path);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        Add(actions, "자동으로 찾기", async () => { savePath(null); client.PreferredPath = null; await Probe(); });
        Add(actions, "파일 선택…", async () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "CLI용 codex.exe 선택", Filter = "Codex CLI (codex.exe)|codex.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            savePath(picker.FileName); client.PreferredPath = picker.FileName; await Probe();
        });
        Add(actions, "연결 확인", Probe);
        panel.Children.Add(status);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "Windows 로그인 시 자동 실행", FontWeight = FontWeights.Bold, FontSize = 16 });
        panel.Children.Add(startup);
        var launch = new WrapPanel(); panel.Children.Add(launch);
        Add(launch, "자동 실행 등록", () =>
        {
            StartupRegistration.Enable(Environment.ProcessPath!);
            Update();
            return Task.CompletedTask;
        });
        Add(launch, "등록 해제", () => { StartupRegistration.Disable(); Update(); return Task.CompletedTask; });
        Add(launch, "Windows 시작 설정", () => { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); return Task.CompletedTask; });
        panel.Children.Add(new TextBlock { Text = Installation.IsInstalled ? "이 PC의 사용자 전용 폴더에 설치되어 있습니다." : "현재 폴더에서 실행 중입니다. 자동 실행 등록 후 폴더를 옮겼다면 다시 등록해 주세요.", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        Update();
    }
    private void Add(Panel panel, string title, Func<Task> action)
    {
        var button = new Button { Content = title, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 6), Background = Brushes.White, BorderBrush = Brushes.LightGray };
        button.Click += async (_, _) =>
        {
            if (busy) return;
            try { busy = true; button.IsEnabled = false; await action(); }
            catch (Exception e) { status.Text = "처리하지 못했습니다. 파일 권한과 Windows 설정을 확인하세요. (" + e.GetType().Name + ")"; }
            finally { busy = false; button.IsEnabled = true; }
        };
        panel.Children.Add(button);
    }
    private void Update()
    {
        path.Text = client.ResolvedPath ?? client.PreferredPath ?? "자동 탐색 · Desktop, Microsoft Store, PATH, CLI, VS Code";
        startup.Text = StartupRegistration.Status;
    }
    private async Task Probe()
    {
        status.Text = "실행 파일과 로그인 상태를 확인하고 있습니다…";
        path.Text = client.PreferredPath ?? "자동 탐색 중…";
        try
        {
            var snapshot = await client.ReadAsync();
            status.Text = snapshot.Main == null ? "Codex에 연결되었습니다. 계정에서 사용량 수치를 제공하지 않았습니다." : "연결되었습니다. 사용량을 정상적으로 가져왔습니다.";
        }
        catch (FileNotFoundException e) { status.Text = e.Message; }
        catch (OperationCanceledException) { status.Text = "응답 시간이 초과되었습니다. Codex를 실행해 로그인 상태와 네트워크를 확인하세요."; }
        catch { status.Text = "연결하지 못했습니다. 선택한 파일이 CLI용 codex.exe인지 확인하고 이 PC의 Codex에서 로그인하세요."; }
        Update();
    }
}
