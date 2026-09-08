---
project: HD_ACS
type: source-snapshot
status: snapshot
updated: 2026-09-07
tags:
  - hd-acs
  - llm-wiki
---

# 코드 관찰 snapshot

이 snapshot은 현재 코드의 근거를 휴대할 수 있도록 보존합니다. 이후 수정은 자동 반영되지 않습니다. [[HD_ACS - 현재 상태]]와 함께 읽습니다.

## src/HD.Acs.UI.Desktop/HD.Acs.UI.Desktop.csproj
전체 파일 SHA256: `c26a0adf156744ea511f9ea3598414db0e9c4e21ec841f1922c787d98cc78762`. 아래는 전체 또는 관련 발췌입니다.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!--
    HD.Acs.UI.Desktop — Avalonia 11 크로스플랫폼 운영 앱 헤드 (Windows / macOS / Linux).
    Models·Services·ViewModels는 HD.Acs.UI.Core(프레임워크 중립)를 그대로 공유하고,
    이 프로젝트는 뷰(axaml)·Avalonia 어댑터(디스패처·대화상자·컨버터)만 가진다.
    이름을 HD.Acs.UI.Avalonia로 짓지 않은 이유: C#에서 자기 네임스페이스 안의 `Avalonia.*` 참조가
    프레임워크 네임스페이스 대신 자기 하위 네임스페이스로 해석되는 함정 회피.
  -->
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>HD.Acs.UI.Desktop</RootNamespace>
    <AssemblyName>HD.Acs.UI.Desktop</AssemblyName>
    <Version>0.1.0</Version>
    <Product>HD_ACS 관제</Product>
    <Company>HD현대중공업</Company>
    <!-- 배포(tools/publish_desktop.sh): 자체 포함 publish → macOS .app 번들 / Windows·Linux 폴더 -->
    <SatelliteResourceLanguages>en;ko</SatelliteResourceLanguages>
    <InvariantGlobalization>false</InvariantGlobalization>
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    <!-- 바인딩은 리플렉션 기본(WPF 뷰와 동일 표기). 컴파일 바인딩(x:DataType)은 후속 최적화 -->
    <AvaloniaUseCompiledBindingsByDefault>false</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>

  <ItemGroup>
    <!-- 브랜드 로고 등 이미지 자산 — avares://HD.Acs.UI.Desktop/Assets/... -->
    <AvaloniaResource Include="Assets\**\*.png" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.20" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.20" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.20" />
    <!-- DataGrid 11.3.13 = 최신 11.x, 의존 Avalonia >= 11.3.13 -->
    <PackageReference Include="Avalonia.Controls.DataGrid" Version="11.3.13" />
    <PackageReference Include="Avalonia.Diagnostics" Version="11.3.20" Condition="'$(Configuration)' == 'Debug'" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\HD.Acs.UI.Core\HD.Acs.UI.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- macOS 번들 템플릿은 빌드 산출물이 아니라 publish 스크립트가 읽는다 -->
    <None Remove="macos\**" />
  </ItemGroup>
  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

## src/HD.Acs.UI.Desktop/Views/AreaManagementView.axaml
전체 파일 SHA256: `e51441d56196eb45fa1d8fed46dfd25b15d8688974348337f0b0fe86bc639a86`. 아래는 전체 또는 관련 발췌입니다.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="using:HD.Acs.UI.Desktop.Views"
             x:Class="HD.Acs.UI.Desktop.Views.AreaManagementView">
    <!-- DataContext = AreaPlanningViewModel. ESC: 도면 4점 선택(픽) 모드 해제 (폼·전개도 캔버스 공통 서브트리) -->
    <UserControl.KeyBindings>
        <KeyBinding Gesture="Escape" Command="{Binding CancelPickCommand}" />
    </UserControl.KeyBindings>

    <Grid Margin="14" ColumnDefinitions="Auto,*" TextBlock.Foreground="{DynamicResource AppTextBrush}">
        <!-- ── 좌: 등록 폼 (영역 → 작업) ── -->
        <ScrollViewer Grid.Column="0" VerticalScrollBarVisibility="Auto" Margin="0,0,12,0">
            <StackPanel Width="450">
                <!-- 선창 정의는 [파일 ▸ 새 프로젝트] 팝업에서 수행 -->
                <Border Background="{DynamicResource AppInfoBgBrush}" BorderBrush="{DynamicResource AppInfoBorderBrush}" BorderThickness="1" CornerRadius="3" Padding="10" Margin="0,0,0,10">
                    <StackPanel TextBlock.Foreground="{DynamicResource AppInfoFgBrush}">
                        <TextBlock Text="{Binding TankId, StringFormat='현재 선창: {0}'}" FontWeight="Bold" Margin="0,0,0,2" />
                        <TextBlock Text="{Binding DerivedText}" TextWrapping="Wrap" FontSize="12" />
                        <TextBlock Text="선창 3D 정의는 [파일 ▸ 새 프로젝트] 또는 [열기]에서 수행합니다." FontSize="11" TextWrapping="Wrap" Margin="0,4,0,0" />
                    </StackPanel>
                </Border>

                <!-- ② 영역 등록 (면 로컬 u,v) -->
                <Border BorderBrush="{DynamicResource AppBorderBrush}" BorderThickness="1" CornerRadius="3" Padding="10" Margin="0,0,0,10">
                    <StackPanel>
                        <TextBlock Text="② 영역 등록 (면 로컬 u,v · m)" FontWeight="SemiBold" Margin="0,0,0,6" />
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <TextBlock Text="층 필터" Classes="lbl" />
                            <ComboBox Classes="field" Width="100" ItemsSource="{Binding Levels}" DisplayMemberBinding="{Binding Label}"
                                      SelectedItem="{Binding SelectedLevel}" />
                            <TextBlock Text="면" Classes="lbl" Margin="10,0,4,0" MinWidth="0" />
                            <ComboBox Classes="field" Width="100" ItemsSource="{Binding Walls}" DisplayMemberBinding="{Binding WallCode}"
                                      SelectedItem="{Binding SelectedWall}" />
                        </StackPanel>
                        <TextBlock Text="층을 고르면 그 층에서 도달 가능한 면만 표시됩니다. 층은 영역 z범위로 서버가 자동 유도합니다." Foreground="{DynamicResource AppMutedTextBrush}" FontSize="11" Margin="0,0,0,2" TextWrapping="Wrap" />
                        <TextBlock Text="{Binding SelectedWallInfo}" Foreground="{DynamicResource AppMutedTextBrush}" FontSize="11" Margin="0,0,0,4" TextWrapping="Wrap" />
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <TextBlock Text="이름" Classes="lbl" />
                            <TextBox Width="100" Classes="field" Text="{Binding AreaName}" />
                        </StackPanel>
                        <ToggleButton Content="도면에서 4점 선택" HorizontalAlignment="Left" MinWidth="130" Margin="0,2,0,4"
                                      IsChecked="{Binding PickMode}"
                                      ToolTip.Tip="켜면 전개도 커서가 크로스헤어로 바뀌고 클릭으로 P1~P4를 지정합니다. 캔버스 우클릭 또는 ESC로 해제." />
                        <TextBlock Text="사각형 4점 (면 로컬 u,v) — 숫자 입력 또는 [도면에서 4점 선택] 후 캔버스를 순서대로 4번 클릭(우클릭=해제)" Foreground="{DynamicResource AppMutedTextBrush}" FontSize="11" Margin="0,0,0,2" TextWrapping="Wrap" />
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <TextBlock Text="P1 u,v" Classes="lbl" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C1U}" Margin="0,0,6,0" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C1V}" />
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <TextBlock Text="P2 u,v" Classes="lbl" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C2U}" Margin="0,0,6,0" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C2V}" />
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <TextBlock Text="P3 u,v" Classes="lbl" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C3U}" Margin="0,0,6,0" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C3V}" />
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                            <TextBlock Text="P4 u,v" Classes="lbl" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C4U}" Margin="0,0,6,0" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding C4V}" />
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,6"
                                    ToolTip.Tip="정차점 = 영역 중심에서 벽 내부향 법선(수평) 방향으로 이 거리만큼 이격. 빈값=서버 기본(0.8m). 바닥/천장(B/T) 면은 적용 불가 — 수동 지정 사용.">
                            <TextBlock Text="정차 이격[m]" Classes="lbl" />
                            <NumericUpDown Classes="field" MinWidth="150" Minimum="0" Value="{Binding StationStandoffM}" />
                        </StackPanel>
                        <CheckBox Content="정차 수동 지정 (전역 x,y,θ) — 바닥/천장(B/T) 면은 필수" IsChecked="{Binding StationOverride}" Margin="0,0,0,4" />
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,6" IsEnabled="{Binding StationOverride}">
                            <TextBlock Text="x,y" Classes="lbl" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding StationX}" Margin="0,0,6,0" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding StationY}" Margin="0,0,6,0" />
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,6" IsEnabled="{Binding StationOverride}">
                            <TextBlock Text="θ" Classes="lbl" />
                            <NumericUpDown Classes="field" MinWidth="150" Value="{Binding StationTheta}" />
                        </StackPanel>
                        <Button Classes="form" Content="영역 등록" HorizontalAlignment="Left" MinWidth="110" Command="{Binding RegisterAreaCommand}" />
                    </StackPanel>
                </Border>

                <!-- ③ 검사 작업 등록 (선택 영역, 경계 내) -->
                <Border BorderBrush="{DynamicResource AppBorderBrush}" BorderThickness="1" CornerRadius="3" Padding="10">
                    <StackPanel>
                        <TextBlock Text="{Binding SelectedArea.Name, StringFormat='③ 검사 작업 등록 — 영역 {0}', FallbackValue='③ 검사 작업 등록 (영역 선택)'}"
                                   FontWeight="SemiBold" Margin="0,0,0,6" />
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <TextBlock Text="시작 u,v" Classes="lbl" />
                            <NumericUpDown Classes="field" Value="{Binding StartU}" Margin="0,0,6,0" />
                            <NumericUpDown Classes="field" Value="{Binding StartV}" />
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                            <TextBlock Text="끝 u,v" Classes="lbl" />
                            <NumericUpDown Classes="field" Value="{Binding EndU}" Margin="0,0,6,0" />
                            <NumericUpDown Classes="field" Value="{Binding EndV}" />
                        </StackPanel>
                        <Button Classes="form" Content="작업 등록" HorizontalAlignment="Left" MinWidth="110" Command="{Binding RegisterTaskCommand}" />
                    </StackPanel>
                </Border>
                <TextBlock Text="{Binding StatusMessage}" TextWrapping="Wrap" Margin="0,8,0,0" Foreground="{DynamicResource AppErrorBrush}" FontSize="12" />
            </StackPanel>
        </ScrollViewer>

        <!-- ── 우: 실시간 (u,v) 전개도 + 영역/작업 목록 ── -->
        <Grid Grid.Column="1" RowDefinitions="2*,Auto,Auto">
            <!-- 실시간 전개도 (좌측 폼 입력이 즉시 반영) -->
            <views:AreaLayoutView Grid.Row="0" Margin="0,0,0,8" />

            <DockPanel Grid.Row="1" MinHeight="130" Margin="0,0,0,8">
                <TextBlock DockPanel.Dock="Top" Text="선택 면 영역 목록 (선택 시 작업 등록)" FontWeight="Bold" Margin="0,0,0,4" />
                <DataGrid x:Name="AreaGrid" ItemsSource="{Binding Areas}" SelectedItem="{Binding SelectedArea}"
                          AutoGenerateColumns="False" IsReadOnly="True" MaxHeight="220">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="이름" Binding="{Binding Name}" Width="70" />
                        <DataGridTextColumn Header="Lv" Binding="{Binding Level}" Width="40" />
                        <DataGridTextColumn Header="u" Binding="{Binding UMin, StringFormat='{}{0:N2}'}" Width="60" />
                        <DataGridTextColumn Header="~u" Binding="{Binding UMax, StringFormat='{}{0:N2}'}" Width="60" />
                        <DataGridTextColumn Header="v" Binding="{Binding VMin, StringFormat='{}{0:N2}'}" Width="60" />
                        <DataGridTextColumn Header="~v" Binding="{Binding VMax, StringFormat='{}{0:N2}'}" Width="60" />
                        <DataGridTextColumn Header="작업" Binding="{Binding TaskCount}" Width="50" />
                        <DataGridTemplateColumn Header="" Width="Auto">
                            <DataGridTemplateColumn.CellTemplate>
                                <DataTemplate>
                                    <Button Content="삭제" Padding="8,1"
                                            Command="{Binding $parent[DataGrid].DataContext.DeleteAreaCommand}"
                                            CommandParameter="{Binding}" />
                                </DataTemplate>
                            </DataGridTemplateColumn.CellTemplate>
                        </DataGridTemplateColumn>
                    </DataGrid.Columns>
                </DataGrid>
            </DockPanel>

            <DockPanel Grid.Row="2" MinHeight="120">
                <TextBlock DockPanel.Dock="Top" Text="선택 영역 작업 목록" FontWeight="Bold" Margin="0,0,0,4" />
                <DataGrid x:Name="TaskGrid" ItemsSource="{Binding AreaTasks}" AutoGenerateColumns="False" IsReadOnly="True" MinHeight="100" MaxHeight="220">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="seq" Binding="{Binding Seq}" Width="46" />
                        <DataGridTextColumn Header="시작 u" Binding="{Binding StartU, StringFormat='{}{0:N2}'}" Width="64" />
                        <DataGridTextColumn Header="시작 v" Binding="{Binding StartV, StringFormat='{}{0:N2}'}" Width="64" />
                        <DataGridTextColumn Header="끝 u" Binding="{Binding EndU, StringFormat='{}{0:N2}'}" Width="64" />
                        <DataGridTextColumn Header="끝 v" Binding="{Binding EndV, StringFormat='{}{0:N2}'}" Width="64" />
                        <DataGridTemplateColumn Header="" Width="Auto">
                            <DataGridTemplateColumn.CellTemplate>
                                <DataTemplate>
                                    <Button Content="삭제" Padding="8,1"
                                            Command="{Binding $parent[DataGrid].DataContext.DeleteTaskCommand}"
                                            CommandParameter="{Binding}" />
                                </DataTemplate>
                            </DataGridTemplateColumn.CellTemplate>
                        </DataGridTemplateColumn>
                    </DataGrid.Columns>
                </DataGrid>
            </DockPanel>
        </Grid>
    </Grid>
</UserControl>
```

## src/HD.Acs.UI.Core/ViewModels/AreaPlanningViewModel.cs
전체 파일 SHA256: `75607eb7d746211d4fb0f5b204bd03ca54c03b967f1e4f839bf21805dbd44b5d`. 아래는 전체 또는 관련 발췌입니다.

```csharp
    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private bool CanRegisterArea() => SelectedWall is not null;

    /// <summary>입력 코너 P1~P4 (면-로컬 u,v, v는 층-로컬).</summary>
    private double[][] InputCorners() => new[]
    {
        new[] { C1U, C1V }, new[] { C2U, C2V }, new[] { C3U, C3V }, new[] { C4U, C4V },
    };

    [RelayCommand(CanExecute = nameof(CanRegisterArea))]
    private async Task RegisterAreaAsync()
    {
        if (SelectedWall is not { } w) return;
        var local = InputCorners();
        var (miu, miv, mau, mav) = AreaBboxLocal(local);
        if (mau - miu < 1e-6 || mav - miv < 1e-6) { StatusMessage = "영역이 퇴화했습니다 — 유효한 사각형 4점을 입력하세요."; return; }
        if (mav > SliceH + 1e-6 || miv < -1e-6) { StatusMessage = $"코너 v가 선택 층 구간(0~{SliceH:0.###})을 벗어났습니다."; return; }
        double off = VOff;   // 층-로컬 → 면-전체 v 변환 후 저장(각 코너 v)
        var corners = local.Select(p => new[] { p[0], p[1] + off }).ToArray();
        try
        {
            var (_, level) = await _api.CreateAreaAsync(TankId, w.WallCode, AreaName, corners,
                StationOverride ? StationX : null, StationOverride ? StationY : null, StationOverride ? StationTheta : null,
                _operatorId, StationStandoffM);
            StatusMessage = $"영역 등록: {w.WallCode}/{AreaName} (4점) → 유도 층 L{level}";
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"영역 등록 실패: {ex.Message}"; }   // 면범위·층유도 400·중복 409
    }

    private static (double MinU, double MinV, double MaxU, double MaxV) AreaBboxLocal(double[][] pts)
    {
        double miu = double.MaxValue, miv = double.MaxValue, mau = double.MinValue, mav = double.MinValue;
        foreach (var p in pts)
        {
            if (p[0] < miu) miu = p[0]; if (p[0] > mau) mau = p[0];
            if (p[1] < miv) miv = p[1]; if (p[1] > mav) mav = p[1];
        }
        return (miu, miv, mau, mav);
    }

    [RelayCommand]
    private async Task DeleteAreaAsync(AreaDto? area)
    {
        if (area is null) return;
        try { await _api.DeleteAreaAsync(area.AreaId); StatusMessage = "영역 삭제됨."; await RefreshAreasAsync(); }
        catch (Exception ex) { StatusMessage = $"영역 삭제 실패: {ex.Message}"; }
    }

    private bool CanRegisterTask() => SelectedArea is not null;

    [RelayCommand(CanExecute = nameof(CanRegisterTask))]
    private async Task RegisterTaskAsync()
    {
        if (SelectedArea is not { } a) return;
        double off = VOff;   // 층-로컬 → 면-전체 v 변환 후 저장
        try
        {
            int seq = await _api.CreateAreaTaskAsync(a.AreaId, StartU, StartV + off, EndU, EndV + off, "LINE", "DXF-1", "PROF-1", _operatorId);
            StatusMessage = $"작업 등록: seq {seq} ({StartU},{StartV})–({EndU},{EndV})(로컬)";
            await LoadTasksAndProjectAsync();
            await RefreshAreasAsync();
        }
        catch (Exception ex) { StatusMessage = $"작업 등록 실패: {ex.Message}"; }   // 경계 밖 400
    }

    [RelayCommand]
```

## src/HD.Acs.UI.Core/Services/AcsApiClient.cs
전체 파일 SHA256: `aa391a371d96ded9779b168be88084a3ce9a4169f1940d429710ec5bc6d10e4c`. 아래는 전체 또는 관련 발췌입니다.

```csharp
    // ── 영역·검사 작업 [SPEC v3 §4] — 벽면-로컬 (u,v). v3.1: level은 서버가 유도(응답에 유도 층) ──────────
    public async Task<(Guid AreaId, int Level)> CreateAreaAsync(string tankId, string wallCode, string name,
        double[][] corners,
        double? stationX, double? stationY, double? stationTheta, string userId,
        double? stationStandoffM = null, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/api/areas", new
        {
            TankId = tankId, WallCode = wallCode, Name = name,
            Corners = corners,
            StationX = stationX, StationY = stationY, StationTheta = stationTheta, UserId = userId,
            StationStandoffM = stationStandoffM
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 면범위 400·층유도실패 400·중복 409·면없음 404 메시지 노출
        var r = await resp.Content.ReadFromJsonAsync<IdResult>(ct);
        return (r?.AreaId ?? Guid.Empty, r?.Level ?? 0);
    }

    public async Task<IReadOnlyList<AreaDto>> GetAreasAsync(string tankId, string? wallCode = null, int? level = null, CancellationToken ct = default)
    {
        var url = $"/api/areas?tankId={Uri.EscapeDataString(tankId)}"
                  + (wallCode is not null ? $"&wallCode={Uri.EscapeDataString(wallCode)}" : "")
                  + (level is int l ? $"&level={l}" : "");
        return await _http.GetFromJsonAsync<List<AreaDto>>(url, ct) ?? new();
    }

    public async Task DeleteAreaAsync(Guid areaId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/areas/{areaId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<int> CreateAreaTaskAsync(Guid areaId, double startU, double startV, double endU, double endV,
        string seamType, string sectionDxfId, string profileId, string userId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/api/areas/{areaId}/tasks", new
        {
            StartU = startU, StartV = startV, EndU = endU, EndV = endV,
            SeamType = seamType, SectionDxfId = sectionDxfId, ProfileId = profileId, UserId = userId
        }, ct);
        await EnsureSuccessOrThrowAsync(resp, ct);   // 경계 밖 400 메시지 노출
        return (await resp.Content.ReadFromJsonAsync<AreaTaskResult>(ct))?.Seq ?? 0;
    }

    public async Task<IReadOnlyList<AreaTaskDto>> GetAreaTasksAsync(Guid areaId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<AreaTaskDto>>($"/api/areas/{areaId}/tasks", ct) ?? new();

    public async Task DeleteAreaTaskAsync(Guid taskId, CancellationToken ct = default)
    {
```

## src/HD.Acs.Core/Planning/TankGeometry.cs
전체 파일 SHA256: `afb8e15ff4364ca8b9864f03effa4e37fdc9faa4a2b5fb4c9340cbcd49397c51`. 아래는 전체 또는 관련 발췌입니다.

```csharp
using HD.Acs.Core.Geometry;

namespace HD.Acs.Core.Planning;

/// <summary>선창 파생 치수 [SPEC v3 §2]. w_low/w_up = 챔퍼 수평 run, B=전폭, W_ceil=천장폭, H=전체높이.</summary>
public sealed record TankDerived(double WLow, double B, double WUp, double WCeil, double H);

/// <summary>
/// 자동 생성된 벽면(면) 프레임 [SPEC v3 §3]. Pose(P0,U,V)=벽면-로컬 2D→도면 3D, Normal=내부향 단위법선,
/// U/V Len=면 크기(m), FacingYaw=AMR이 면을 바라보는 도면 yaw(수평법선 없으면 null=바닥/천장).
/// </summary>
public sealed record GeneratedWall(string WallCode, WallPose Pose, double[] Normal, double ULen, double VLen, double? FacingYaw)
{
    /// <summary>벽면-로컬 (u,v) → 도면 3D [x,y,z].</summary>
    public double[] To3D(double u, double v) => Pose.LocalToDrawing(u, v);

    internal static GeneratedWall Build(string code, double[] p0, double[] u, double[] v, double[] n, double uLen, double vLen)
    {
        var U = Vec3.Normalize(u) ?? throw new WallPoseInvalidException($"{code}: U축 산출 불가.");
        var V = Vec3.Normalize(v) ?? throw new WallPoseInvalidException($"{code}: V축 산출 불가.");
        var N = Vec3.Normalize(n) ?? throw new WallPoseInvalidException($"{code}: 법선 산출 불가.");
        double? facing = null;
        double nh = Math.Sqrt(N[0] * N[0] + N[1] * N[1]);   // 법선 수평 성분 크기
        if (nh >= 1e-9) facing = Math.Atan2(-N[1], -N[0]);   // AMR이 면을 바라봄(−법선 방향) [§3]
        return new GeneratedWall(code, new WallPose(p0, U, V), N, uLen, vLen, facing);
    }
}

/// <summary>
/// 선창 파라메트릭 정의 [SPEC v3 §2/§3]. 팔각 단면(좌우대칭) × 길이 L 프리즘 → 10면 자동 생성.
/// 전역 프레임: 원점=바닥 중심, x=길이, y=폭(+y 좌현), z=상방, 바닥 z=0 (단위 m). 각도 rad.
/// </summary>
public sealed record TankGeometry(
    double L, double WFloor, double ThetaLow, double HLow,
    double HWall, double ThetaUp, double HUp,
    double[] LevelZ, double Ox = 0, double Oy = 0,
    double? ReachZMin = null, double? ReachZMax = null)
{
    /// <summary>층 도달 밴드 [SPEC v3.1 §5-A] — level_z·reach_z·H로 산출.</summary>
    public IReadOnlyList<LevelBand> LevelBandList() =>
        LevelBands.Compute(LevelZ, ReachZMin, ReachZMax, Derived().H);

    public TankDerived Derived()
    {
        double wLow = HLow / Math.Tan(ThetaLow);
        double b = WFloor + 2 * wLow;
        double wUp = HUp / Math.Tan(ThetaUp);
        double wCeil = b - 2 * wUp;
        double h = HLow + HWall + HUp;
        return new TankDerived(wLow, b, wUp, wCeil, h);
```

## tools/analyze_dxf_regions.py
전체 파일 SHA256: `50bdd6c8477f70997430b2c9c3beb834a992ea5a2853f4bad2e928304981004d`. 아래는 전체 또는 관련 발췌입니다.

```python
"""Read ASCII DXF entities without executing embedded content."""
from pathlib import Path
from collections import Counter, defaultdict
import json

ROOT = Path(__file__).resolve().parents[1]

def entities(path):
    lines = path.read_text(encoding='cp949', errors='replace').splitlines()
    pairs = [(int(lines[i].strip()), lines[i+1].strip()) for i in range(0, len(lines)-1, 2)]
    section = None
    result = []
    current = None
    for i, (code, value) in enumerate(pairs):
        if code == 2 and i and pairs[i-1] == (0, 'SECTION'):
            section = value
        if code == 0:
            if current is not None:
                result.append(current)
                current = None
            if section == 'ENTITIES' and value != 'ENDSEC':
                current = {'type': value, 'groups': defaultdict(list)}
        elif current is not None:
            current['groups'][code].append(value)
    return result

def first(e, code, default=''):
    return e['groups'].get(code, [default])[0]

def points(e):
    g = e['groups']
    if e['type'] == 'LINE':
        return [(float(first(e,10)), float(first(e,20))), (float(first(e,11)), float(first(e,21)))]
    return list(zip(map(float,g.get(10,[])), map(float,g.get(20,[]))))

if __name__ == '__main__':
    for path in sorted((ROOT/'drawing'/'2D도면').glob('*.dxf')):
        es = entities(path)
        print('\n',path.name)
        for layer in sorted(set(first(e,8) for e in es)):
            if 'Membrane' not in layer and 'Steel Wall' not in layer:
                continue
            subset = [e for e in es if first(e,8)==layer]
            pts = [p for e in subset if e['type'] in ('LINE','LWPOLYLINE') for p in points(e)]
            print(layer, Counter(e['type'] for e in subset), 'bounds', (min(p[0] for p in pts),min(p[1] for p in pts),max(p[0] for p in pts),max(p[1] for p in pts)) if pts else None)
            horizontal, vertical = [], []
            for e in subset:
                if e['type']!='LINE': continue
                a,b=points(e)
                if abs(a[1]-b[1])<.01: horizontal.append((round(a[1],3),round(abs(a[0]-b[0]),3)))
                if abs(a[0]-b[0])<.01: vertical.append((round(a[0],3),round(abs(a[1]-b[1]),3)))
            print('H lengths',Counter(l for _,l in horizontal).most_common(8),'V lengths',Counter(l for _,l in vertical).most_common(8))
            print('H positions', sorted(set(p for p,l in horizontal))[:35], 'V positions', sorted(set(p for p,l in vertical))[:35])
```

## tools/build_dxf_area_preview.py
전체 파일 SHA256: `710f614e918482065112a55a6bee08e706898143fbb5a5f4c5c4de0c13358b2b`. 아래는 전체 또는 관련 발췌입니다.

```python
"""Build a review-only area/task coordinate list from DXF line geometry.

Coordinates are drawing-local millimetres, not calibrated ACS wall coordinates.
"""
from pathlib import Path
from collections import defaultdict, Counter
import heapq
import json
from analyze_dxf_regions import ROOT, entities, first, points

OUT = ROOT / 'drawing' / 'area-preview'
EPS = .03

def segment_records(es, layers):
    result=[]
    for e in es:
        if first(e,8) not in layers: continue
        if e['type'] not in ('LINE','LWPOLYLINE'): continue
        if e['type']=='LWPOLYLINE' and (any(abs(float(v))>1e-8 for v in e['groups'].get(42,[])) or abs(float(first(e,230,'1'))-1)>1e-8):
            continue
        pts=points(e)
        edges=list(zip(pts,pts[1:]))
        if e['type']=='LWPOLYLINE' and int(first(e,70,'0')) & 1 and pts[-1]!=pts[0]:
            edges.append((pts[-1],pts[0]))
        for a,b in edges:
            if abs(a[1]-b[1])<EPS:
                axis,fixed,lo,hi='H',a[1],min(a[0],b[0]),max(a[0],b[0])
            elif abs(a[0]-b[0])<EPS:
                axis,fixed,lo,hi='V',a[0],min(a[1],b[1]),max(a[1],b[1])
            else: continue
            if hi-lo<EPS: continue
            result.append({'axis':axis,'fixed':round(fixed,2),'lo':round(lo,3),'hi':round(hi,3),'handles':[first(e,5)]})
    return result

def merge_segments(segs):
    grouped=defaultdict(list)
    for s in segs: grouped[(s['axis'],s['fixed'])].append(s)
    out=[]
    for key,ss in sorted(grouped.items()):
        current=None
        for s in sorted(ss,key=lambda x:x['lo']):
            if current and s['lo']<=current['hi']+EPS:
                current['hi']=max(current['hi'],s['hi'])
                current['handles']=sorted(set(current['handles']+s['handles']))
            else:
                if current: out.append(current)
                current=dict(s)
        if current: out.append(current)
    return out

def near_point(axis,along,fixed,origin):
    xy=(along,fixed) if axis=='H' else (fixed,along)
    return [round(xy[i]-origin[i],3) for i in (0,1)]

def build_wall(path):
    es=entities(path)
    wall=path.stem.split()[-1]
    layers={first(e,8) for e in es if 'Membrane Sheet' in first(e,8)}
    seams=merge_segments(segment_records(es,layers))
    pts=[p for e in es if first(e,8) in layers and e['type'] in ('LINE','LWPOLYLINE') for p in points(e)]
    origin=[min(p[i] for p in pts) for i in (0,1)]
    size=[max(p[i] for p in pts)-origin[i] for i in (0,1)]
    center_es=[e for e in es if first(e,8)=='KC-2B Steel Wall' and first(e,6).startswith('CENTER')]
    topsegs=merge_segments(segment_records(center_es,{'KC-2B Steel Wall'}))
    topcoords={a:sorted({s['fixed'] for s in topsegs if s['axis']==a}) for a in ('H','V')}
    pitches={a:Counter(round(b-a0,2) for a0,b in zip(topcoords[a],topcoords[a][1:])).most_common(3) for a in ('H','V')}
    common={'wall':wall,'source_file':path.name,'origin_dxf':origin,'size_drawing':size,'observed_pitch':pitches,
            'coordinate_system':'DXF membrane bounding-box lower-left origin; +u=DXF x, +v=DXF y; millimetres assumed',
            'seams':seams,'top_lines':topsegs,'areas':[],'tasks':[]}
    if wall in ('PL','SL','PU','SU'):
        return {**common,'status':'pending_coordinate_transform','reason':'DXF vertical pitch is about 254.6, not 360. Surface-distance transform is not confirmed.'}
    tasks=[]
    short_runs=0
    for s in seams:
        cross=[t for t in topsegs if t['axis']!=s['axis'] and t['lo']-EPS<=s['fixed']<=t['hi']+EPS and s['lo']-EPS<=t['fixed']<=s['hi']+EPS]
        vals=sorted({t['fixed'] for t in cross})
        runs=[]
        for val in vals:
            if not runs or abs(val-runs[-1][-1]-360)>EPS: runs.append([val])
            else: runs[-1].append(val)
        for run in runs:
            short_runs+=(len(run)-1)%2
            for i in range(0,len(run)-2,2):
                pp=[near_point(s['axis'],v,s['fixed'],origin) for v in run[i:i+3]]
                tasks.append({'id':f'{wall}-T{len(tasks)+1:05d}','direction':s['axis'],'start':pp[0],'middle':pp[1],'end':pp[2],
                              'seam_handles':s['handles'],'top_handles':sorted({h for t in cross if t['fixed'] in run[i:i+3] for h in t['handles']})})
    assert len({(t['direction'],tuple(t['start']),tuple(t['end'])) for t in tasks})==len(tasks)
    # Candidate 800 x 1600 windows around task endpoints and on a 720 x 1440 grid.
    boxes=[]
    for t in tasks:
        boxes.append((min(t['start'][0],t['end'][0]),min(t['start'][1],t['end'][1]),max(t['start'][0],t['end'][0]),max(t['start'][1],t['end'][1])))
    candidates=set()
    def clamp(v,dim,width): return round(max(0,min(v,dim-width)),3)
    for x0,y0,x1,y1 in boxes:
        xs=[x0-40,x1-760,(x0//720)*720-40]
        ys=[y0-80,y1-1520,(y0//1440)*1440-80]
        for x in xs:
            for y in ys:
                candidates.add((clamp(x,size[0],800),clamp(y,size[1],1600)))
    candidates=sorted(candidates,key=lambda c:(c[1],c[0]))
    covers=[]
    inverse=[[] for _ in tasks]
    for ci,(x,y) in enumerate(candidates):
        covered={i for i,(x0,y0,x1,y1) in enumerate(boxes) if x0>=x-EPS and y0>=y-EPS and x1<=x+800+EPS and y1<=y+1600+EPS}
        covers.append(covered)
        for i in covered: inverse[i].append(ci)
    remaining=[set(c) for c in covers]
    heap=[(-len(c),i) for i,c in enumerate(remaining) if c]
    heapq.heapify(heap)
    assigned=set()
    selected=[]
    while len(assigned)<len(tasks):
        neg,ci=heapq.heappop(heap)
        if -neg!=len(remaining[ci]): continue
        owned=set(remaining[ci])
        assert owned
        selected.append((ci,owned))
        assigned.update(owned)
        changed=set()
        for ti in owned:
            for cj in inverse[ti]:
                remaining[cj].discard(ti)
                changed.add(cj)
        for cj in changed:
            if remaining[cj]: heapq.heappush(heap,(-len(remaining[cj]),cj))
    areas=[]
    for k,(ci,owned) in enumerate(sorted(selected,key=lambda item:(candidates[item[0]][1],candidates[item[0]][0]))):
        x,y=candidates[ci]
        aid=f'{wall}-A{k+1:04d}'
        included=sorted(covers[ci])
        corners=[[x,y],[round(x+800,3),y],[round(x+800,3),round(y+1600,3)],[x,round(y+1600,3)]]
        areas.append({'id':aid,'corners':corners,'included_task_ids':[tasks[i]['id'] for i in included],
                      'assigned_task_ids':[tasks[i]['id'] for i in sorted(owned)],'included_count':len(included),'assigned_count':len(owned),
                      'assigned_H':sum(tasks[i]['direction']=='H' for i in owned),'assigned_V':sum(tasks[i]['direction']=='V' for i in owned)})
        for ti in owned: tasks[ti]['area_id']=aid
    assert sum(a['assigned_count'] for a in areas)==len(tasks)
    assert all(abs(((t['end'][0]-t['start'][0])**2+(t['end'][1]-t['start'][1])**2)**.5-720)<EPS for t in tasks)
    return {**common,'status':'draft','tasks':tasks,'areas':areas,'unpaired_one_pitch_runs':short_runs,
            'counts':{'areas':len(areas),'tasks':len(tasks),'H':sum(t['direction']=='H' for t in tasks),'V':sum(t['direction']=='V' for t in tasks)}}

def main():
    OUT.mkdir(parents=True,exist_ok=True)
    walls=[]
    for path in sorted((ROOT/'drawing'/'2D도면').glob('*.dxf')):
        w=build_wall(path)
        walls.append(w)
        print(w['wall'],w['status'],w.get('counts',{}),flush=True)
    data={'units':'mm','pitch':360,'task_length':720,'area_width':800,'area_height':1600,
          'status':'review_only_not_registered','algorithm':'Greedy maximum uncovered-task coverage over task-aligned candidate windows; no global optimum claim.',
          'limitations':['Membrane Sheet straight segments are candidate weld seams; detailed lines may need filtering.',
                         'CENTER line intersections are treated as tops as agreed by the user.',
                         'Coordinates use drawing membrane bounding box, not ACS wall origin. Unit metadata is unspecified in DXF.',
                         'Areas are clamped to bounding boxes only. Outer contour, holes, robot reach and floor bands are not validated.',
                         'Four chamfer walls are pending coordinate transform and excluded from totals.',
                         'IDs are stable preview labels for this calculation, not database IDs.'],
          'walls':walls}
    (OUT/'area-task-coordinates.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    def coord(p): return '('+', '.join(f'{v:.3f}'.rstrip('0').rstrip('.') for v in p)+')'
    md=['# 검사영역 좌표와 작업 매칭', '',
        '**검토용 산출물입니다. 실제 ACS 등록 좌표나 DB ID가 아닙니다.**', '',
        '- 단위: mm로 가정. 각 DXF의 멤브레인 도형 경계상자 좌하단이 (0, 0), +u=도면 x, +v=도면 y입니다.',
        '- 원시 DXF 좌표 = 이 목록의 좌표 + 해당 면의 원점. ACS 면 좌표로의 원점·방향 변환은 별도입니다.',
        '- Area: 800 × 1600mm, 작업: 360mm 피치의 top–top–top 직선 720mm.',
        '- 포함 수: 해당 Area에 완전히 들어오는 작업 수. 배정 수: 그 Area에만 연결된 고유 작업 수.',
        '- Area 간 겹침은 허용하고 작업 ID는 중복 배정하지 않았습니다.',
        '- 도면의 Membrane Sheet 직선을 용접선 후보로 사용했습니다. 상세 형상선 구분은 추가 확인이 필요합니다.',
        '- 경계상자 내부만 검사했습니다. 외곽 윤곽·개구부·장애물·층·로봇 도달 범위는 미검증입니다.',
        '- 작업을 많이 포함하는 후보부터 선택한 탐욕적 배치이며 전역 최적해는 아닙니다.',
        '- 경사면 PL·SL·PU·SU는 세로 반복 간격 약 254.6의 실거리 변환 미확정으로 집계에서 제외했습니다.', '',
        '## 면별 집계', '', '| 면 | Area 수 | 가로 작업 | 세로 작업 | 고유 작업 합계 |', '|---|---:|---:|---:|---:|']
    for w in walls:
        c=w.get('counts')
        md.append(f"| {w['wall']} | {c['areas']} | {c['H']} | {c['V']} | {c['tasks']} |" if c else f"| {w['wall']} | 변환 확인 필요 | — | — | — |")
    md += ['', '## Area당 실제 배정 작업 개수', '', '| 배정 작업 수 | Area 수 |', '|---:|---:|']
    distribution=Counter(a['assigned_count'] for w in walls for a in w['areas'])
    md += [f'| {n} | {count} |' for n,count in sorted(distribution.items())]
    for w in walls:
        if not w['areas']: continue
        md += ['',f"## {w['wall']} 검사영역",'',f"원점 DXF: {coord(w['origin_dxf'])}. 출처: {w['source_file']}",'',
               '| Area ID | P1 (u,v) | P2 (u,v) | P3 (u,v) | P4 (u,v) | 포함 | 배정 | 가로 | 세로 | 실제 배정 작업 ID |',
               '|---|---|---|---|---|---:|---:|---:|---:|---|']
        for a in w['areas']:
            md.append('| '+' | '.join([a['id'],*[coord(p) for p in a['corners']],str(a['included_count']),str(a['assigned_count']),str(a['assigned_H']),str(a['assigned_V']),', '.join(a['assigned_task_ids'])])+' |')
    (OUT/'area-coordinates.md').write_text('\n'.join(md)+'\n',encoding='utf-8')
    template=(ROOT/'tools'/'dxf_area_preview.html').read_text(encoding='utf-8')
    (OUT/'area-task-preview.html').write_text(template.replace('__DATA__',json.dumps(data,ensure_ascii=False,separators=(',',':'))),encoding='utf-8')

if __name__=='__main__': main()
```
