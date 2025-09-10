@echo off
cd /d "%~dp0"

:: Ask for file to commit
set /p file="Enter file to commit (leave empty to commit all): "

if "%file%"=="" (
    git add .
) else (
    git add "%file%"
)

:: Ask for commit message
set /p msg="Enter commit message: "

if "%msg%"=="" (
    echo Commit message cannot be empty.
    pause
    exit /b 1
)

git commit -m "%msg%"

:: Ask if user wants to push
set /p push="Do you want to push to remote? (y/n): "
if /i "%push%"=="y" (
    git push
)

pause
