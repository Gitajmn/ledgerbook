; 记账本 安装包（Inno Setup 6.7.3 编译）—— 单文件、原生安装器、不需要解压任何运行时
;
; 为什么换成 Inno（2026-10-09 决定的路线"丙"）：
;   实测本机明确允许 Inno 的 UseSetupLdr=no 单文件模式在 %TEMP% 不可用的情况下正常安装，
;   而自研的"存根 → 解压 lb-setup → 跑 WPF 向导"那套又慢又容易撞锁/残留（用户连续踩坑）。
;
; 三个关键取舍（都有实测依据，改之前先看 继承文档 §3.9）：
;   ① UseSetupLdr=no      —— 不用引导器，安装器本体不需要往 %TEMP% 解压（本机 %TEMP% 被安全策略锁死）
;   ② Uninstallable=no    —— 本机禁止往 HKCU\...\Uninstall 写子键（连 Inno 默认流程都报"拒绝访问"、退 4），
;                            所以不用 Inno 的卸载器，改用自带的「卸载记账本.exe」（不依赖注册表）
;   ③ PrivilegesRequired=lowest —— 默认装到用户目录、不弹 UAC；想装到 Program Files 的用户可在
;                            向导里选"为所有用户安装"（PrivilegesRequiredOverridesAllowed=dialog）
;
; 负载按目标机有没有 .NET 8 桌面运行时自动二选一：
;   有 → 精简本体（约 0.7MB，用系统 .NET）    没有 → 自包含本体（约 146MB，运行时就在程序目录里）
;
; 编译：pwsh -File v0.2\工具\打包安装包-Inno.ps1
;       或直接：ISCC.exe /DTestIcons 记账本.iss（本文件存 UTF-8 带 BOM，否则中文变乱码）

#define AppName "记账本"
#define AppVersion "2.0.1"
#define AppExeName "记账本.exe"
#define UninstallerName "卸载记账本.exe"
#define HelperExe "D:\desktop\记账\v0.2\安装\安装器\记账本小工具.exe"
#define SlimDir "D:\desktop\记账\newest\便携版-精简"
#define FullDir "D:\desktop\记账\临时\发布-内置运行时"
#define OutDir "D:\desktop\记账\newest"
#define IconFile "D:\desktop\记账\v0.2\安装\素材\appicon.ico"

[Setup]
AppId={{8E31A9C4-7F2B-4C6E-9A11-3D5C7E9B2A44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ajmn
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableReadyPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UseSetupLdr=no
Uninstallable=no
OutputDir={#OutDir}
OutputBaseFilename=记账本-安装包-v{#AppVersion}-单文件
SetupIconFile={#IconFile}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
#ifdef TestIcons
; 自检用：把快捷方式建在安装目录里（沙箱里写不了开始菜单/桌面，这样也能测到建快捷方式这条代码路径）
#define IconRoot "{app}\_测试快捷方式"
#else
#define IconRoot "{group}"
#endif

[Languages]
Name: "cn"; MessagesFile: "compiler:Default.isl"

; 中文界面：Inno 没有自带简体中文语言文件，这里直接覆盖向导会显示的那些文案
[Messages]
SetupAppTitle={#AppName} 安装程序
SetupWindowTitle={#AppName} {#AppVersion} 安装
WelcomeLabel1=欢迎安装 {#AppName}
WelcomeLabel2=安装程序会把「{#AppName}」装到你的电脑上。%n%n装好后账本数据就放在程序目录旁边（设置里可以看和改）。%n%n继续请点「下一步」。
SelectDirLabel3=安装程序会把「{#AppName}」装到下面这个文件夹。
SelectDirBrowseLabel=要继续就点「下一步」；想换个文件夹就点「浏览」。
DiskSpaceMBLabel=至少需要 %1 MB 的磁盘空间。
WizardSelectTasks=选择附加任务
SelectTasksDesc=你想让安装程序顺便做哪些事？
SelectTasksLabel2=勾选你想顺便做的事，然后点「下一步」：
WizardReady=准备安装
ReadyLabel1=安装程序已经准备好把「{#AppName}」装到你的电脑上。
WizardInstalling=正在安装
InstallingLabel=正在把「{#AppName}」装到你的电脑上，请稍候…
PreparingDesc=安装程序正在准备安装「{#AppName}」…
WizardFinished={#AppName} 安装完成
FinishedLabel=「{#AppName}」已经装好了。%n%n桌面和开始菜单里都能找到它；想卸载就双击安装目录里的「{#UninstallerName}」（开始菜单里也有）。
FinishedLabelNoIcons=「{#AppName}」已经装好了。%n%n想卸载就双击安装目录里的「{#UninstallerName}」。
ClickFinish=点「完成」退出安装程序。
ConfirmExit=安装还没完成。现在退出的话，「{#AppName}」不会被装上。%n%n确定要退出吗？
ConfirmExitTitle=正在取消安装
ExitSetupTitle=退出安装
ExitSetupMessage=安装还没完成，现在退出的话「{#AppName}」不会被装上。%n%n确定要退出吗？
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)…
ButtonOK=确定
ButtonYes=是(&Y)
ButtonNo=否(&N)
ErrorCreatingShortcut=创建快捷方式失败：%1

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "trustcert"; Description: "信任签名证书（不再提示“未知发布者”）"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
; 有 .NET 8 桌面运行时 → 只装精简本体（0.7MB）
Source: "{#SlimDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: HasDotNet8
; 没有 → 装自包含本体（146MB，运行时就在程序目录里）
Source: "{#FullDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not HasDotNet8
; 自带卸载器/安装后置工具（同一个 exe，两个角色）
Source: "{#HelperExe}"; DestDir: "{app}"; DestName: "{#UninstallerName}"; Flags: ignoreversion

[Icons]
Name: "{#IconRoot}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{#IconRoot}\{#UninstallerName}"; Filename: "{app}\{#UninstallerName}"; WorkingDir: "{app}"
#ifndef TestIcons
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
#endif

[Run]
; 装完写 安装信息.txt（可选顺带配置签名证书）
Filename: "{app}\{#UninstallerName}"; Parameters: "--post-install --target ""{app}"" --payload ""{code:PayloadKind}""{code:TrustArg}"; Flags: runhidden waituntilterminated
; 立刻启动
Filename: "{app}\{#AppExeName}"; Description: "立刻启动{#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
var
  PayloadIsSlim: Boolean;

// 目标机有没有 .NET 8 桌面运行时（决定装精简还是自包含本体）
function HasDotNet8: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then Exit;
  try
    Result := FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0;
  finally
    FindClose(FindRec);
  end;
end;

function PayloadKind(Param: String): String;
begin
  if PayloadIsSlim then Result := '精简（使用系统 .NET）' else Result := '内置运行时（自带 .NET 8）';
end;

function TrustArg(Param: String): String;
begin
  if WizardIsTaskSelected('trustcert') then Result := ' --trust' else Result := '';
end;

procedure InitializeWizard;
begin
  PayloadIsSlim := HasDotNet8;
end;
