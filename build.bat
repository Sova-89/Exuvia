@echo off
rem Build Exuvia.dll. Usage: build.bat "C:\path\to\Hollow Knight Silksong"
rem Needs .NET SDK 8 and the game with BepInEx 5 installed.
setlocal
if "%~1"=="" (
  echo Usage: build.bat "path to the game folder"
  exit /b 1
)
dotnet build src\Exuvia.csproj -c Release -p:GameDir="%~1" || exit /b 1
if not exist out mkdir out
copy /y src\bin\Release\netstandard2.1\Exuvia.dll out\Exuvia.dll >nul
echo Done: out\Exuvia.dll
