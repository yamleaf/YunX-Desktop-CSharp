@echo off
setlocal EnableDelayedExpansion

rem ============================================================
rem  YunX Desktop 一键编译
rem
rem  用法：
rem    build.bat             默认编译 net48 -> publish\net48\（3.7 MB 单文件）
rem    build.bat net8        编译 net8 自包含 -> publish\net8\（70 MB）
rem    build.bat debug       只编译 Debug（快，用于改代码后验证）
rem    build.bat test        编译 + 跑全部离线断言（默认 net48）
rem    build.bat test net8   net8 + 断言
rem    build.bat clean       清理编译产物（两个框架都清）
rem
rem  设计说明：
rem    · dotnet 可能不在 PATH（本项目装在 C:\AI_Tools\dotnet），
rem      所以下面按「环境变量 -> PATH -> 常见安装位置」逐级查找。
rem      编译 net48 目标也需要 .NET 8 SDK（SDK 风格项目 + 多目标），
rem      只是产物运行时不依赖它。
rem    · 默认目标 net48：产物只有 3.7 MB，目标机用系统自带的 .NET Framework 4.8
rem      （Win10 1903+ / Win11 默认就有）。net8 版保留给需要跑在 Win7/8 的场景。
rem    · 两个框架的产物分别落 publish\net48\ 与 publish\net8\，互不覆盖。
rem    · LICENSE 会被一并复制到发布目录（AGPL 的分发要求）。
rem ============================================================

rem ---- 切到脚本所在目录，双击运行时也正确 ----
cd /d "%~dp0"

rem ------------------------------------------------------------
rem  参数解析
rem
rem  MODE 与 TFM 分开收集，互不覆盖。
rem  踩过的坑：旧版先把 MODE 覆盖成 release 再去比较 MODE=="net48"，
rem  那个条件永远不成立，net48 产物会落到 publish\ 并悄悄盖掉 net8 的。
rem ------------------------------------------------------------
set "MODE="
set "TFM="

:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="net48"   set "TFM=net48"
if /i "%~1"=="net8"    set "TFM=net8"
if /i "%~1"=="all"     set "TFM="
if /i "%~1"=="release" set "MODE=release"
if /i "%~1"=="debug"   set "MODE=debug"
if /i "%~1"=="test"    set "MODE=test"
if /i "%~1"=="clean"   set "MODE=clean"
shift
goto :parse_args
:args_done

if not defined MODE set "MODE=release"
rem 默认框架：net48。改这里就等于改了所有人的默认产物，三思。
if not defined TFM  set "TFM=net48"

set "ROOT=%CD%"
rem 发布目录名只由 TFM 派生，单一真源 —— 不要写成分支判断，
rem 否则「哪个框架落哪个目录」就得靠记忆。
set "PUBDIR=%ROOT%\publish\%TFM%"
set "EXENAME=YunX-Desktop.exe"

echo.
echo ============================================================
echo   YunX Desktop 编译器
echo   目录: %ROOT%
echo   模式: %MODE%
echo   框架: %TFM%
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
    rem clean 的语义是「回到干净状态」，两个框架的发布目录都清，
    rem 否则留一个 publish\net8 在那儿会让人以为它还有效。
    if exist "%ROOT%\publish\net48" rmdir /s /q "%ROOT%\publish\net48" 2>nul
    if exist "%ROOT%\publish\net8" rmdir /s /q "%ROOT%\publish\net8" 2>nul
    echo       完成。
    echo.
    goto :ok
)

rem ================= 3. 编译 =================
if /i "%MODE%"=="debug" (
    echo [2/5] 编译 Debug 框架%TFM%...
    "%DOTNET%" build YunX.sln -c Debug -f %TFM% --nologo -v q
    if errorlevel 1 goto :buildfail
    echo.
    echo       完成。看 bin\Debug\ 下的 %TFM% 目录
    goto :ok
)

echo [2/5] 编译 Release 框架%TFM%...
"%DOTNET%" build YunX.sln -c Release -f %TFM% --nologo -v q
if errorlevel 1 goto :buildfail
echo       通过。

rem ================= 4. 跑断言（仅 test 模式）=================
rem 断言必须跟着同一个 TFM 跑：net48 的下载引擎走的是完全不同的写入路径
rem （PwriteStream / BCrypt / System.Data.SQLite），拿 net8 的结果去证明
rem net48 没问题是不成立的。
if /i "%MODE%"=="test" (
    echo.
    echo [3/5] 运行离线断言 框架%TFM%...
    "%DOTNET%" run --project src\YunX.Smoke -c Release -f %TFM% --nologo
    if errorlevel 1 (
        echo.
        echo [错误] 断言未通过，已停止发布。
        goto :fail
    )
    echo       断言通过。
) else (
    echo [3/5] 跳过断言（用 build.bat test 可一并运行）
)

rem ================= 5. 发布 =================
echo.
echo [4/5] 发布 %TFM% 单文件...
rem 程序运行时会占用 exe，导致覆盖失败（错误信息很晦涩）。
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

rem Costura.Fody 的织入发生在 MSBuild 判定「输出已是最新」之后。
rem obj\Release 里残留上次的织入产物会让它认为无事可做：exe 内嵌的还是
rem 旧的托管 payload，新编的 dll 又被丢进发布目录。这个坑踩过三次，别删。
if exist "src\YunX.Desktop\obj\Release" rmdir /s /q "src\YunX.Desktop\obj\Release" 2>nul

"%DOTNET%" publish src\YunX.Desktop -f %TFM% -c Release -o "%PUBDIR%" --nologo -v q
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
    rem Show one decimal: 3.7 MB rather than a truncated "3 MB"
    set /a "SIZEK=%%~zf/1024"
    set /a "SIZEI=!SIZEK!/1024"
    set /a "SIZED=((!SIZEK!-!SIZEI!*1024)*10)/1024"
    echo   程序: %%~ff
    echo   大小: !SIZEI!.!SIZED! MB
)
if exist "%PUBDIR%\LICENSE" echo   协议: LICENSE 已随发布

rem 清理调试符号（发布目录不需要，省 200KB+）
del /f /q "%PUBDIR%\*.pdb" 2>nul

rem 顺便检查发布目录里有没有多余文件。冒出 dll 通常意味着 Costura 没织入。
set "EXTRA="
for %%f in ("%PUBDIR%\*") do (
    if /i not "%%~nxf"=="%EXENAME%" if /i not "%%~nxf"=="LICENSE" set "EXTRA=!EXTRA! %%~nxf"
)
if defined EXTRA (
    echo.
    echo   [警告] 发布目录里还有 exe / LICENSE 之外的文件：!EXTRA!
    echo          多半是 Costura 没织入，先 build.bat clean 再编。
)

echo ------------------------------------------------------------
echo.
if /i "%TFM%"=="net48" goto :msg_net48
echo 编译成功。双击上面那个 exe 即可运行（无需安装 .NET 运行时）。
goto :msg_done

:msg_net48
echo 编译成功。目标机需自带 .NET Framework 4.8：
echo   Win10 1903+ / Win11 默认已安装，可直接运行。

:msg_done

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
