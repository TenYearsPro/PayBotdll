@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

set "CONFIG=Release"
if /i "%~1"=="Debug" set "CONFIG=Debug"
if /i "%~1"=="Release" set "CONFIG=Release"

set "PROJECT=%~dp0GBot.Plugins.EpayShop.csproj"
set "OUT_DIR=%~dp0bin\%CONFIG%"
set "DEPLOY_DIR=D:\GBOT\plugin"
set "DLL_NAME=PayBot.dll"

echo ========================================
echo  编译 PayBot  [%CONFIG%]
echo ========================================
echo.

REM ---- 查找 dotnet ----
set "DOTNET="
where dotnet >nul 2>&1 && for /f "delims=" %%i in ('where dotnet') do (
  if not defined DOTNET set "DOTNET=%%i"
)

if not defined DOTNET if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
  set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
)
if not defined DOTNET if exist "%ProgramFiles%\dotnet\dotnet.exe" (
  set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
)
if not defined DOTNET if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" (
  set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
)

if not defined DOTNET (
  echo [错误] 未找到 dotnet，请先安装 .NET 8 SDK
  echo        https://dotnet.microsoft.com/download/dotnet/8.0
  goto :fail
)

echo [信息] DOTNET = %DOTNET%
"%DOTNET%" --version
echo.

REM ---- 检查 Abstractions 引用 ----
if not exist "%~dp0lib\GBot.PluginAbstractions.dll" (
  if not exist "%~dp0..\..\src\GBot.PluginAbstractions\GBot.PluginAbstractions.csproj" (
    echo [警告] 未找到 lib\GBot.PluginAbstractions.dll
    echo         也未找到源码工程引用，编译可能会失败。
    echo.
  )
)

echo [1/3] 还原 NuGet...
"%DOTNET%" restore "%PROJECT%"
if errorlevel 1 goto :fail

echo.
echo [2/3] 编译 %CONFIG%...
"%DOTNET%" build "%PROJECT%" -c %CONFIG% --no-restore -v minimal
if errorlevel 1 goto :fail

if not exist "%OUT_DIR%\%DLL_NAME%" (
  echo [错误] 未找到输出：%OUT_DIR%\%DLL_NAME%
  goto :fail
)

echo.
echo [3/3] 部署到 %DEPLOY_DIR% ...
if not exist "%DEPLOY_DIR%" mkdir "%DEPLOY_DIR%"

copy /y "%OUT_DIR%\%DLL_NAME%" "%DEPLOY_DIR%\" >nul
if errorlevel 1 goto :fail

REM 依赖 DLL（宿主未自带时需要一起拷）
if exist "%OUT_DIR%\System.Text.Encoding.CodePages.dll" (
  copy /y "%OUT_DIR%\System.Text.Encoding.CodePages.dll" "%DEPLOY_DIR%\" >nul
)

REM 清理旧文件名，避免加载到两份插件
if exist "%DEPLOY_DIR%\GBot.Plugins.EpayShop.dll" (
  del /f /q "%DEPLOY_DIR%\GBot.Plugins.EpayShop.dll" >nul 2>&1
)

echo.
echo ========================================
echo  编译成功
echo  输出：%OUT_DIR%\%DLL_NAME%
echo  已复制到：%DEPLOY_DIR%\%DLL_NAME%
echo ========================================
echo.
echo 提示：如 GBot 正在运行，请重载插件或重启后再测。
echo.
pause
exit /b 0

:fail
echo.
echo ========================================
echo  编译失败
echo ========================================
pause
exit /b 1
