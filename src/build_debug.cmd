@echo off
set W=C:\Users\17336\ZCodeProject\ggbh_work
C:\Users\17336\ZCodeProject\ggbh_work\roslyn\tools\csc.exe -nologo -target:library -optimize+ ^
 -out:%W%\out\MOD_dbgAgent.dll ^
 -reference:%W%\dll\Assembly-CSharp.dll ^
 -reference:%W%\dll\Il2Cppmscorlib.dll ^
 -reference:%W%\dll\Il2CppSystem.Core.dll ^
 -reference:%W%\dll\Il2CppSystem.dll ^
 -reference:%W%\dll\MelonLoader.dll ^
 -reference:%W%\dll\0Harmony.dll ^
 -reference:%W%\dll\UnhollowerBaseLib.dll ^
 -reference:%W%\dll\UnhollowerRuntimeLib.dll ^
 -reference:%W%\dll\UnityEngine.CoreModule.dll ^
 -reference:%W%\dll\UnityEngine.UI.dll ^
 -reference:%W%\dll\UnityEngine.TextRenderingModule.dll ^
 -reference:%W%\dll\UnityEngine.InputLegacyModule.dll ^
 %W%\debugagent\ModMain.cs ^
 %W%\debugagent\UnitIO.cs
