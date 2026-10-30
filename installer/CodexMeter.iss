; Build with scripts/build.ps1. Both editions intentionally share an AppId.
#ifndef AppVersion
  #define AppVersion "2.2.0"
#endif
#ifndef Edition
  #define Edition "Classic"
#endif
#ifndef PayloadDir
  #error PayloadDir must be provided
#endif
#ifndef DistDir
  #define DistDir "..\dist"
#endif
#ifdef TestMode
  #define ProductId "CodexMeter.PackagingTest"
  #define LinkName "Codex Meter Packaging Test"
  #define AppFolder "CodexMeter-PackagingTest"
  #define MutexName "Local\CodexMeter.PackagingTest"
  #define ExitEvent "Local\CodexMeter.PackagingTest.Exit"
#else
  #define ProductId "CodexMeter.Mackeabit"
  #define LinkName "Codex Meter"
  #define AppFolder "CodexMeter"
  #define MutexName "Local\CodexMeter.v1"
  #define ExitEvent "Local\CodexMeter.Exit.v2"
#endif

[Setup]
AppId={#ProductId}
AppName=CodexMeter
AppVersion={#AppVersion}
AppVerName=CodexMeter {#AppVersion} ({#Edition})
AppPublisher=Mackeabit
AppPublisherURL=https://github.com/Mackeabit/CodexMeter
AppSupportURL=https://github.com/Mackeabit/CodexMeter/issues
AppUpdatesURL=https://github.com/Mackeabit/CodexMeter/releases
DefaultDirName={localappdata}\Programs\{#AppFolder}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#DistDir}
OutputBaseFilename=CodexMeter-{#AppVersion}-{#Edition}-Setup-x64
SetupIconFile=..\src\Common\meter.ico
UninstallDisplayIcon={app}\app\CodexMeter.exe
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2
LZMANumBlockThreads=1
LZMADictionarySize=8192
SolidCompression=yes
CloseApplications=no
RestartApplications=no
UsePreviousTasks=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=CodexMeter {#Edition}
VersionInfoCopyright=Copyright (c) 2026 Mackeabit and contributors

[Languages]
Name: english; MessagesFile: compiler:Default.isl
#if FileExists(AddBackslash(CompilerPath) + "Languages\Korean.isl")
Name: korean; MessagesFile: compiler:Languages\Korean.isl
#endif

[CustomMessages]
english.AutoStart=Start CodexMeter when I sign in to Windows
english.Launch=Launch CodexMeter
#if FileExists(AddBackslash(CompilerPath) + "Languages\Korean.isl")
korean.AutoStart=Windows 로그인 시 CodexMeter 자동 실행
korean.Launch=CodexMeter 실행
#endif

[Tasks]
Name: autostart; Description: "{cm:AutoStart}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#LinkName}"; Filename: "{app}\app\CodexMeter.exe"; WorkingDir: "{app}\app"
Name: "{userstartup}\{#LinkName}"; Filename: "{app}\app\CodexMeter.exe"; Parameters: "--startup"; WorkingDir: "{app}\app"; Tasks: autostart

[Run]
#ifndef TestMode
Filename: "{app}\app\CodexMeter.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent
#endif

[Code]
var
  StartupChoiceInitialized: Boolean;

function OpenEvent(DesiredAccess: LongWord; InheritHandle: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function StopMeter: Boolean;
var
  Event: THandle;
  Attempt: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#MutexName}') then Exit;
  Event := OpenEvent($0002, False, '{#ExitEvent}');
  if Event <> 0 then begin
    SetEvent(Event);
    CloseHandle(Event);
  end;
  for Attempt := 1 to 100 do begin
    if not CheckForMutexes('{#MutexName}') then Exit;
    Sleep(100);
  end;
  Result := False;
end;

function TasksSpecifiedOnCommandLine: Boolean;
var
  Index: Integer;
  Argument: String;
begin
  Result := False;
  for Index := 1 to ParamCount do begin
    Argument := Uppercase(ParamStr(Index));
    if (Pos('/TASKS=', Argument) = 1) or (Pos('/MERGETASKS=', Argument) = 1) then begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { InitializeWizard runs before Inno restores its task list. Apply once when
    the task page is ready, preserving both user choices and silent overrides. }
  if (CurPageID = wpSelectTasks) and not StartupChoiceInitialized then begin
    StartupChoiceInitialized := True;
    if not TasksSpecifiedOnCommandLine then
      WizardForm.TasksList.Checked[0] := FileExists(ExpandConstant('{userstartup}\{#LinkName}.lnk'));
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopMeter then
    Result := 'Please exit CodexMeter from its tray menu, then retry. No files have been replaced.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    DeleteFile(ExpandConstant('{userstartup}\{#LinkName}.lnk'));
end;

function InitializeUninstall: Boolean;
begin
  Result := StopMeter;
  if not Result then
    MsgBox('Please exit CodexMeter from its tray menu, then retry uninstalling.', mbError, MB_OK);
end;

// Only files tracked by this installer are removed. Preferences and legacy
// versions are deliberately outside its uninstall file list.
