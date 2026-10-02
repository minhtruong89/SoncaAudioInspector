# Build and package script for Sonca RewAudioInspector
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\dist"
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building and Packaging Sonca RewAudioInspector (Standalone)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
}

$projectPath = "$PSScriptRoot\RewAudioInspector.csproj"

Write-Host "Publishing project: $projectPath" -ForegroundColor Yellow
dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=true -o $OutputDir

if ($LASTEXITCODE -eq 0) {
    # Create batch launcher
    $batContent = @"
@echo off
title Sonca REW Audio Inspector Bridge
cd /d "%~dp0"
SoncaRewAudioInspector.exe
pause
"@
    Set-Content -Path "$OutputDir\run.bat" -Value $batContent -Encoding Ascii

    Write-Host "`n[SUCCESS] Package built successfully!" -ForegroundColor Green
    Write-Host "Output directory: $OutputDir" -ForegroundColor Green
    Write-Host "Executable: $OutputDir\SoncaRewAudioInspector.exe" -ForegroundColor Green
    Write-Host "Launcher:   $OutputDir\run.bat" -ForegroundColor Green
} else {
    Write-Host "`n[ERROR] Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
}
