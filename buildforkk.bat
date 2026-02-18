@echo off

set CSC="C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe"

set GAME=D:\Games\Koikatu
set MANAGED=%GAME%\Koikatu_Data\Managed

set SRC=AffectedDynamicBonesEnabledPersistForKKS.cs
set OUT=%GAME%\BepInEx\plugins\AffectedDynamicBonesEnabledPersist.dll

%CSC% /noconfig /target:library /optimize+ /warn:4 /warnaserror- ^
 /r:System.dll ^
 /r:System.Core.dll ^
 /r:%MANAGED%\UnityEngine.dll ^
 /r:%MANAGED%\Assembly-CSharp.dll ^
 /r:%GAME%\BepInEx\core\BepInEx.dll ^
 /r:%GAME%\BepInEx\core\0Harmony.dll ^
 /out:%OUT% ^
 %SRC%

if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)

echo.
echo Build OK:
echo %OUT%
pause
