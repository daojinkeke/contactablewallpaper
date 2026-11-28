; ContactableWallpaper 安装脚本（Inno Setup 6）
; 由 scripts\package.ps1 调用，可传入：
;   /DSourceDir=<发布目录>  /DOutputDir=<安装包输出目录>  /DMyAppVersion=<版本号>
; 特点：
;   * 安装到用户可写目录（%LOCALAPPDATA%），全程不弹 UAC
;   * 开始菜单 + 桌面快捷方式，卸载干净（含开机自启项）
;   * 开机自启动为安装时可选任务，默认勾选（升级时跟随用户既有设置）
;   * 覆盖升级：AppId 固定，保留用户数据；旧版 cef/libvlc/wmf 与旧 WebView2 运行时会被清掉
;   * WebView2 不随包：只带微软官方引导安装器（prereq\MicrosoftEdgeWebview2Setup.exe，约 2 MB）。
;     安装时检测注册表 pv，缺失则提示并在线安装微软组件（需要联网）。

#ifndef MyAppVersion
  #define MyAppVersion "2.2.1.10"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\..\outputs"
#endif

#ifndef IconFile
  #define IconFile "..\src\Lively\Lively\Resources\appicon.ico"
#endif

#define MyAppName "ContactableWallpaper"
#define MyAppPublisher "ContactableWallpaper"
#define MyAppExeName "Lively.exe"

[Setup]
; 固定 AppId：升级时能被识别为同一个应用
AppId={{6F1D3B9E-4C2A-4E77-9A21-7D5F0C8B3E14}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup

; 每用户安装，不需要管理员权限
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ContactableWallpaper
DefaultGroupName={#MyAppName}
UsePreviousAppDir=yes
AllowNoIcons=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#OutputDir}
OutputBaseFilename=ContactableWallpaper-Setup-{#MyAppVersion}-x64
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

WizardStyle=modern
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableDirPage=auto

Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; 安装/卸载前由 [Code] 主动结束进程，交给 Restart Manager 反而会卡住
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "lang\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
; 默认勾选：装完开机即自动运行。升级时由 [Code] 按用户既有设置调整，不会把用户关掉的自启重新打开。
Name: "autostart"; Description: "开机自动启动（默认勾选）"; GroupDescription: "附加任务："

[InstallDelete]
; 升级时清掉旧版本里已不再随包的组件，避免残留占空间
Type: filesandordirs; Name: "{app}\plugins\cef"
Type: filesandordirs; Name: "{app}\plugins\libvlc"
Type: filesandordirs; Name: "{app}\plugins\wmf"
Type: filesandordirs; Name: "{app}\plugins\webview2\webview2-runtime"
; Core 与 AI CLI 改为单文件发布后，旧版平铺在根目录的运行时 dll、旧版 AI CLI 目录必须清掉，
; 否则升级后会残留 300+ 个无效 dll（100 MB 以上）。{app}\*.dll 只作用于根目录，不进子目录。
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\Lively.deps.json"
Type: files; Name: "{app}\Lively.runtimeconfig.json"
Type: filesandordirs; Name: "{app}\zh-CN"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"
Type: filesandordirs; Name: "{app}\plugins\Ai"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; WebView2 引导安装器额外解压到临时目录，供 PrepareToInstall 在缺运行时调用
Source: "{#SourceDir}\prereq\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; 仅当用户在安装时勾选“开机自动启动”才写入；卸载时自动删除
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "Lively"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'Lively';
  AutostartTaskDesc = '开机自动启动（默认勾选）';
  WebView2ClientKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function GetDataDir(): String;
begin
  Result := ExpandConstant('{localappdata}\Lively Wallpaper');
end;

function CmdLineParamExists(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function IsWebView2Installed(): Boolean;
var
  Value: String;
begin
  // 诊断开关：仅用于在已装 WebView2 的机器上验证“缺失”分支，不改变真实安装行为
  if CmdLineParamExists('/SIMULATEMISSINGWEBVIEW2') then
  begin
    Result := False;
    Exit;
  end;

  Result := RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', Value) and (Value <> '');
  if not Result then
    Result := RegQueryStringValue(HKLM64, WebView2ClientKey, 'pv', Value) and (Value <> '');
  if not Result then
    Result := RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', Value) and (Value <> '');
end;

procedure KillRunningApp();
var
  ResultCode: Integer;
begin
  // 只结束本安装目录里的进程，避免误杀用户机器上同名的其他 Lively 程序
  Exec(ExpandConstant('{cmd}'),
       '/c powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Process Lively,Lively.UI.WinUI,Lively.Player.WebView2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | Stop-Process -Force"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(600);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  // {app} 已确定：先结束占用安装目录的自身进程，再装文件
  KillRunningApp();

  if IsWebView2Installed() then
  begin
    Log('WebView2 installed = yes');
    Exit;
  end;
  Log('WebView2 installed = no');

  // 明确告知：装的是微软组件，需要联网。
  // 静默安装时默认按钮取 IDYES（SuppressibleMsgBox 的第 4 个参数），保证无人值守安装也会装上运行时。
  if SuppressibleMsgBox('本机缺少 Microsoft Edge WebView2 运行时。' + #13#10 + #13#10 +
            '将自动安装微软官方组件 Microsoft Edge WebView2 Runtime：' + #13#10 +
            '安装器约 2 MB，运行时由微软在线下载，需要联网。' + #13#10 + #13#10 +
            '网页壁纸、交互壁纸和 AI 预览依赖它。现在安装吗？',
            mbConfirmation, IDYES, IDYES) <> IDYES then
  begin
    MsgBox('已跳过 WebView2 安装：网页/交互壁纸暂不可用。' + #13#10 +
           '稍后可手动运行：' + ExpandConstant('{app}\prereq\MicrosoftEdgeWebview2Setup.exe'),
           mbInformation, MB_OK);
    Exit;
  end;

  if CmdLineParamExists('/SIMULATEMISSINGWEBVIEW2') then
  begin
    Log('SIMULATED missing WebView2: skip running bootstrapper.');
    Exit;
  end;

  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if (not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '',
               SW_SHOW, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    Log('WebView2 bootstrapper failed, exit code = ' + IntToStr(ResultCode));
    MsgBox('WebView2 运行时安装未成功（退出码 ' + IntToStr(ResultCode) + '）。' + #13#10 +
           '请检查网络后手动运行：' + ExpandConstant('{app}\prereq\MicrosoftEdgeWebview2Setup.exe'),
           mbError, MB_OK);
  end
  else if not IsWebView2Installed() then
  begin
    Log('WebView2 bootstrapper ran but runtime still not detected.');
    MsgBox('WebView2 安装器已运行，但暂未检测到运行时。' + #13#10 +
           '建议重启后再试；也可以手动运行 prereq 目录下的引导安装器。',
           mbInformation, MB_OK);
  end;
end;

function IsAutostartRegistered(): Boolean;
var
  Value: String;
begin
  // 以实际的 Run 项为准：应用里关掉自启会删除该项，升级时据此保持未勾选
  Result := RegQueryStringValue(HKCU, RunKey, RunValueName, Value);
end;

procedure InitializeWizard();
var
  I: Integer;
begin
  // 全新安装：保持默认勾选。覆盖升级：勾选状态跟随当前实际自启状态，
  // 用户在应用里关掉自启后（Run 项已删），升级不会又把它勾上。
  // 注意：InitializeWizard 阶段 {app} 还未初始化，必须用 WizardDirValue。
  if not FileExists(AddBackslash(WizardDirValue) + '{#MyAppExeName}') then
    Exit;

  try
    for I := 0 to WizardForm.TasksList.Items.Count - 1 do
    begin
      if WizardForm.TasksList.Items[I] = AutostartTaskDesc then
      begin
        WizardForm.TasksList.Checked[I] := IsAutostartRegistered();
        Break;
      end;
    end;
  except
    // 任务列表不可用时保持默认值，不影响安装
  end;
end;

function InitializeSetup(): Boolean;
begin
  // 此时 {app} 尚未初始化，不能结束进程；改在 PrepareToInstall 里做。
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  KillRunningApp();
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SettingsPath, Json: String;
begin
  if CurStep = ssPostInstall then
  begin
    // 应用自身启动时会按 Settings.Startup 同步自启项。首次安装先落一份 Settings.json，
    // 让安装器的勾选状态成为应用的初始设置，避免第一次启动就把用户的选择改回去。
    // 已有 Settings.json（覆盖升级）则完全不动，尊重用户既有设置。
    SettingsPath := GetDataDir() + '\Settings.json';
    if not FileExists(SettingsPath) then
    begin
      ForceDirectories(GetDataDir());
      if WizardIsTaskSelected('autostart') then
        Json := '{"Startup":true}'
      else
        Json := '{"Startup":false}';
      SaveStringToFile(SettingsPath, Json, False);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 清掉应用自己写入的自启项（只清指向本安装目录的那条）
    if RegQueryStringValue(HKCU, RunKey, RunValueName, RunValue) then
    begin
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(RunValue)) > 0 then
        RegDeleteValue(HKCU, RunKey, RunValueName);
    end;

    // 用户数据（壁纸库、设置、AI 配置）默认保留，由用户决定
    if not UninstallSilent then
    begin
      if MsgBox('是否同时删除用户数据（壁纸库、设置、AI 配置）？' + #13#10 + #13#10 +
                '选择“否”将保留：' + GetDataDir(), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(GetDataDir(), True, True, True);
    end;
  end;
end;
