param(
  [string]$OutDir = "release\GTAEmblemMaker-v1.0.0",
  [string[]]$ProfilePaths = @(
    "profiles\v1-best-quality.json",
    "profiles\v1-beam-clean.json",
    "profiles\v1-perceptual.json"
  ),
  [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$out = [IO.Path]::GetFullPath((Join-Path $root $OutDir))
$rootPrefix = $root.TrimEnd('\') + '\'
if (-not $out.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Package output must stay inside the repository."
}
$profiles = @()
$models = @{}
foreach ($profilePath in $ProfilePaths) {
  $profileSource = [IO.Path]::GetFullPath((Join-Path $root $profilePath))
  if (-not $profileSource.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $profileSource)) {
    throw "Profile must be an existing file inside the repository: $profilePath"
  }
  $profile = Get-Content -LiteralPath $profileSource -Raw | ConvertFrom-Json
  $model = [string]$profile.stages[0].perceptualRerank.model
  if ([String]::IsNullOrWhiteSpace($model) -or [IO.Path]::GetFileName($model) -ne $model) { throw "Profile contains an invalid perceptual model ID." }
  $modelSource = Join-Path $root "third_party\lpips-winml\model\$model.onnx"
  if (-not (Test-Path -LiteralPath $modelSource)) { throw "Perceptual model is missing: $model" }
  $profiles += [pscustomobject]@{ Source = $profileSource; Value = $profile }
  $models[$model] = $modelSource
}
if ($profiles.Count -ne 3) { throw "The V1 package requires exactly three profiles." }

if (-not $NoBuild) {
  & (Join-Path $root "third_party\cuda-scorer\build.ps1")
  if ($LASTEXITCODE -ne 0) { throw "CUDA scorer build failed." }

  dotnet build (Join-Path $root "native\GTAEmblemMaker.sln") -c Release -p:Platform=x64 --no-restore
  if ($LASTEXITCODE -ne 0) { throw "Native application build failed." }
}

if (Test-Path -LiteralPath $out) {
  Remove-Item -LiteralPath $out -Recurse -Force
}

$cudaDir = Join-Path $out "runtime\cuda"
$perceptualDir = Join-Path $out "runtime\perceptual"
$profileDir = Join-Path $out "profiles"
$licenseDir = Join-Path $out "licenses"
New-Item -ItemType Directory -Force -Path $out, $cudaDir, $perceptualDir, $profileDir, $licenseDir | Out-Null

$appDir = Join-Path $root "native\GTAEmblemMaker.App\bin\x64\Release\net48"
foreach ($file in @(
  "GTAEmblemMaker.exe", "GTAEmblemMaker.exe.config", "GTAEmblemMaker.Core.dll",
  "Microsoft.ML.OnnxRuntime.dll", "SkiaSharp.dll", "System.Buffers.dll", "System.Memory.dll",
  "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll", "libSkiaSharp.dll",
  "onnxruntime.dll", "onnxruntime_providers_shared.dll", "DirectML.dll"
)) {
  Copy-Item -LiteralPath (Join-Path $appDir $file) -Destination (Join-Path $out $file) -Force
}
for ($index = 0; $index -lt $profiles.Count; $index++) {
  $profile = $profiles[$index].Value
  $profile.isDefault = $index -eq 0
  $packagedProfile = Join-Path $profileDir ("$($profile.id).json")
  $profile | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $packagedProfile -Encoding UTF8
}
Copy-Item -LiteralPath (Join-Path $root "third_party\cuda-scorer\bin\cuda-scorer.exe") -Destination (Join-Path $cudaDir "cuda-scorer.exe") -Force
foreach ($model in $models.Keys) {
  Copy-Item -LiteralPath $models[$model] -Destination (Join-Path $perceptualDir "$model.onnx") -Force
}
Copy-Item -LiteralPath (Join-Path $root "third_party\lpips-winml\LICENSE-LPIPS.txt") -Destination (Join-Path $licenseDir "LPIPS.txt") -Force
Copy-Item -LiteralPath (Join-Path $root "third_party\lpips-winml\LICENSE-TORCHVISION.txt") -Destination (Join-Path $licenseDir "Torchvision.txt") -Force
$packages = Join-Path $env:USERPROFILE ".nuget\packages"
Copy-Item -LiteralPath (Join-Path $packages "skiasharp\4.150.0\LICENSE.txt") -Destination (Join-Path $licenseDir "SkiaSharp.txt") -Force
Copy-Item -LiteralPath (Join-Path $packages "microsoft.ml.onnxruntime.directml\1.24.4\LICENSE") -Destination (Join-Path $licenseDir "ONNXRuntime.txt") -Force
Copy-Item -LiteralPath (Join-Path $packages "microsoft.ml.onnxruntime.directml\1.24.4\ThirdPartyNotices.txt") -Destination (Join-Path $licenseDir "ONNXRuntime-ThirdParty.txt") -Force
Copy-Item -LiteralPath (Join-Path $packages "microsoft.ai.directml\1.15.4\LICENSE-CODE.txt") -Destination (Join-Path $licenseDir "DirectML.txt") -Force

@"
GTA Emblem Maker

Run GTAEmblemMaker.exe.
Pipelines: Best Quality, Beam Clean, Perceptual AlexNet 224.

Requires Windows 10 version 1809 or newer, x64, .NET Framework 4.8, and a CUDA-capable NVIDIA GPU.
Perceptual reranking uses LPIPS v0.1 through ONNX Runtime DirectML.
Run outputs are stored under LocalAppData\GTAEmblemMaker\runs.
"@ | Set-Content -LiteralPath (Join-Path $out "README.txt") -Encoding ASCII

$prohibited = Get-ChildItem -LiteralPath $out -Recurse -File | Where-Object {
  $_.Name -eq "node.exe" -or $_.Extension -in @(".bat", ".cmd", ".ps1", ".mjs", ".js", ".html")
}
if ($prohibited) { throw "Production package contains a prohibited launcher or web runtime." }

$bytes = (Get-ChildItem -LiteralPath $out -Recurse -File | Measure-Object -Property Length -Sum).Sum
$zip = "$out.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host ("Package written to {0} ({1:N2} MB)" -f $out, ($bytes / 1MB))
Write-Host ("Release archive written to {0}" -f $zip)
