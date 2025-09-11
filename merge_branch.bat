@echo off

echo remote branches :
git branch -r
echo ------
:: Ask for source branch
set /p source_branch="Enter the branch you want to merge from: "
if "%source_branch%"=="" (
    echo No branch specified. Exiting.
    pause
    exit /b 1
)

:: Ask for target branch
set /p target_branch="Enter the target branch (default: main): "
if "%target_branch%"=="" set target_branch=main

:: Checkout and update source branch
git checkout %source_branch%
if errorlevel 1 (
    echo Branch %source_branch% does not exist.
    pause
    exit /b 1
)

::display changes
echo changes :
echo ____
git status

:: Fetch latest changes
git fetch origin
if errorlevel 1 (
    echo Failed to fetch from origin.
    pause
    exit /b 1
)

git pull origin %source_branch%
if errorlevel 1 (
    echo Failed to pull %source_branch%. Resolve manually.
    pause
    exit /b 1
)

::push to target branch
git push --set-upstream origin %source_branch%
if errorlevel 1 (
	echo Branch %target_branch% 
)

:: Checkout and update target branch
git checkout %target_branch%
if errorlevel 1 (
    echo Branch %target_branch% does not exist.
    pause
    exit /b 1
)

git pull origin %target_branch%
if errorlevel 1 (
    echo Failed to pull %target_branch%. Resolve manually.
    pause
    exit /b 1
)

:: Merge source into target
git merge %source_branch%
if errorlevel 1 (
    echo Merge conflicts detected. Resolve conflicts manually.
    pause
    exit /b 1
)

:: Push merged target branch
git push origin %target_branch%
if errorlevel 1 (
    echo Push failed. Resolve manually.
    pause
    exit /b 1
)

:: Ask if source branch should be deleted
set /p delete_branch="Do you want to delete the source branch '%source_branch%'? (y/n): "
if /i "%delete_branch%"=="y" (
    git branch -d %source_branch%
    git push origin --delete %source_branch%
    echo Branch '%source_branch%' deleted locally and remotely.
)

echo Merge complete.
pause
