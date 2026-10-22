@echo off
setlocal EnableDelayedExpansion

rem ============================================================
rem  YunX Desktop 一键编译
rem
rem  用法：
rem    build.bat              编译 Release 并发布单文件到 publish\
rem    build.bat debug        只编译 Debug（快，用于改代码后验证）
rem    build.bat test         编译 Release + 跑全部离线断言
rem    build.bat clean        清理编译产物
rem
rem  设计说明：
rem    · dotnet 可能不在 PATH（本项目装在 C:\AI_Tools\dotnet），
rem      所以下面按「环境变量 → PATH → 常见安装位置」逐级查找。
rem    · 发布产物是**单文件自包含** exe，目标机无需装 .NET 运行时。
rem    · LICENSE 会被一并复制到 publish\（AGPL 的分发要求）。
rem ============================================================

rem ---- 切到脚本所在目录，双击运行时也正确 ----
cd /d "%~dp0"

set "MODE=%~1"
if "%MODE%"=="" set "MODE=release"

set "ROOT=%CD%"
set "PUBDIR=%ROOT%\publish"
set "EXENAME=YunX-Desktop.exe"

echo.
echo ============================================================
echo   YunX Desktop 编译器
echo   目录: %ROOT%
echo   模式: %MODE%
echo ============================================================
echo.

rem ================= 1. 查找 dotnet =================
set "DOTNET="

if defined DOTNET_ROOT (
    if exist "%DOTNET_ROOT%\dotnet.exe" set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
)

if not defined DOTNET (
    for /f "delims=" %%i in ('where dotnet.exe 2^>nul') do (
        if not defined DOTNET set "DOTNET=%%i"
    )
)

if not defined DOTNET (
    for %%p in (
        "C:\AI_Tools\dotnet\dotnet.exe"
        "%ProgramFiles%\dotnet\dotnet.exe"
        "%ProgramFiles(x86)%\dotnet\dotnet.exe"
        "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
    ) do (
        if not defined DOTNET if exist %%p set "DOTNET=%%~p"
    )
)

if not defined DOTNET (
    echo [错误] 找不到 dotnet.exe
    echo.
    echo   请安装 .NET 8 SDK： https://dotnet.microsoft.com/download
    echo   或把 dotnet 加入 PATH，或设置环境变量 DOTNET_ROOT。
    echo.
    goto :fail
)

for /f "tokens=1" %%v in ('"%DOTNET%" --version 2^>nul') do set "DOTNETVER=%%v"
echo [1/5] dotnet: %DOTNET%
echo       SDK:    !DOTNETVER!
echo.

rem ================= 2. 清理模式 =================
if /i "%MODE%"=="clean" (
    echo [2/5] 清理编译产物...
    for /d /r %%d in (bin,obj) do (
        if exist "%%d" (
            rmdir /s /q "%%d" 2>nul
        )
    )
    if exist "%PUBDIR%" rmdir /s /q "%PUBDIR%" 2>nul
    echo       完成。
    echo.
    goto :ok
)

rem ================= 3. 编译 =================
if /i "%MODE%"=="debug" (
    echo [2/5] 编译 Debug...
    "%DOTNET%" build YunX.sln -c Debug --nologo -v q
    if errorlevel 1 goto :buildfail
    echo.
    echo       完成。输出：src\YunX.Desktop\bin\Debug\net8.0-windows\win-x64\
    goto :ok
)

echo [2/5] 编译 Release...
"%DOTNET%" build YunX.sln -c Release --nologo -v q
if errorlevel 1 goto :buildfail
echo       通过。

rem ================= 4. 跑断言（仅 test 模式）=================
if /i "%MODE%"=="test" (
    echo.
    echo [3/5] 运行离线断言...
    "%DOTNET%" run --project src\YunX.Smoke -c Release --nologo
    if errorlevel 1 (
        echo.
        echo [错误] 断言未通过，已停止发布。
        goto :fail
    )
    echo       断言通过。
) else (
    echo [3/5] 跳过断言（用 build.bat test 可一并运行）
)

rem ================= 5. 发布单文件 =================
echo.
echo [4/5] 发布单文件...
rem 程序运行时会占用 exe，导致 GenerateBundle 失败（错误信息很晦涩）。
rem 先检测一下，避免用户看到一堆 MSB4018 堆栈。
if exist "%PUBDIR%\%EXENAME%" (
    tasklist /fi "imagename eq %EXENAME%" 2>nul | "%SystemRoot%\System32\find.exe" /i "%EXENAME%" >nul
    if not errorlevel 1 (
        echo.
        echo [错误] %EXENAME% 正在运行，无法覆盖。
        echo        请先关闭程序再重新编译。
        echo.
        goto :fail
    )
    del /f /q "%PUBDIR%\%EXENAME%" 2>nul
)

"%DOTNET%" publish src\YunX.Desktop -c Release -o "%PUBDIR%" --nologo -v q
if errorlevel 1 goto :buildfail

if not exist "%PUBDIR%\%EXENAME%" (
    echo [错误] 发布完成但没找到 %EXENAME%
    goto :fail
)
echo       完成。

rem ================= 6. 汇总 =================
echo.
echo [5/5] 结果
echo ------------------------------------------------------------
for %%f in ("%PUBDIR%\%EXENAME%") do (
    set /a "SIZEMB=%%~zf/1048576"
    echo   程序: %%~ff
    echo   大小: !SIZEMB! MB
)
if exist "%PUBDIR%\LICENSE" echo   协议: LICENSE 已随发布

rem 清理调试符号（发布目录不需要，省 200KB+）
del /f /q "%PUBDIR%\*.pdb" 2>nul

echo ------------------------------------------------------------
echo.
echo 编译成功。双击上面那个 exe 即可运行（无需安装 .NET）。
echo.
goto :ok

:buildfail
echo.
echo ============================================================
echo  [失败] 编译/发布出错，请查看上方错误信息。
echo ============================================================
echo.
pause
exit /b 1

:fail
echo.
pause
exit /b 1

:ok
echo.
endlocal
exit /b 0
