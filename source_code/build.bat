@echo off
setlocal

set "PROJECT_DIR=%~dp0"
set "OUTPUT_DIR=%PROJECT_DIR%bin\Debug\net8.0"
set "TARGET_DIR=%PROJECT_DIR%..\dll"
set "CSPROJ_PATH=%PROJECT_DIR%Live2D.csproj"

echo Building %CSPROJ_PATH% ...
dotnet build "%CSPROJ_PATH%"
if %ERRORLEVEL% neq 0 (
    echo BUILD FAILED
    goto :pause_and_exit
)

echo Build succeeded. Copying files to %TARGET_DIR% ...

set "ERRORS=0"
for %%f in (Live2D.dll Live2D.pdb Live2D.deps.json) do (
    if not exist "%OUTPUT_DIR%\%%f" (
        echo ERROR: Source file not found: %OUTPUT_DIR%\%%f
        set "ERRORS=1"
    ) else (
        copy /y "%OUTPUT_DIR%\%%f" "%TARGET_DIR%\%%f" >nul
        echo   Copied: %%f
    )
)

if %ERRORS% neq 0 (
    echo COPY ERRORS DETECTED
    goto :pause_and_exit
)

echo Done.

:pause_and_exit
pause
