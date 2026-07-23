param(
  [string]$OutDir = "release\GTAEmblemMaker-v1.1.3",
  [string[]]$ProfilePaths = @(
    "profiles\v1-beam-clean.json",
    "profiles\v1-perceptual.json",
    "profiles\v1-catalog-quality.json",
    "profiles\v1-clean-logo.json"
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
  $rerank = $profile.stages[0].perceptualRerank
  if ($null -ne $rerank) {
    $backend = [string]$rerank.backend
    $model = [string]$rerank.model
    if ($backend -eq "lpips-directml") {
      if ([String]::IsNullOrWhiteSpace($model) -or [IO.Path]::GetFileName($model) -ne $model) { throw "Profile contains an invalid perceptual model ID." }
      $modelSource = Join-Path $root "third_party\lpips-winml\model\$model.onnx"
      if (-not (Test-Path -LiteralPath $modelSource)) { throw "Perceptual model is missing: $model" }
      $models[$model] = $modelSource
    } elseif ($backend -ne "native-edge-detail") {
      throw "Profile contains an unsupported perceptual backend: $backend"
    }
  }
  $profiles += [pscustomobject]@{ Source = $profileSource; Value = $profile }
}
if ($profiles.Count -ne 4) { throw "The V1.1.3 package requires exactly four profiles." }

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
Pipelines: Beam Clean, Perceptual AlexNet 224, Official Catalog Quality, Clean Logo.

Requires Windows 10 version 1809 or newer, x64, .NET Framework 4.8, and a CUDA-capable NVIDIA GPU.
The Perceptual profile uses LPIPS v0.1 through ONNX Runtime DirectML. Official Catalog Quality uses its native edge-detail reranker. Clean Logo protects source edges and colors for simple transparent graphics.
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

$bundleProject = Join-Path $root "native\GTAEmblemMaker.Bundle\GTAEmblemMaker.Bundle.csproj"
$bundleBuild = [IO.Path]::GetFullPath((Join-Path $root "release\.bundle-v1.1.3"))
$singleExe = [IO.Path]::GetFullPath("$out.exe")
foreach ($bundlePath in @($bundleBuild, $singleExe)) {
  if (-not $bundlePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Bundle output must stay inside the repository: $bundlePath"
  }
}
if (Test-Path -LiteralPath $bundleBuild) { Remove-Item -LiteralPath $bundleBuild -Recurse -Force }
if (Test-Path -LiteralPath $singleExe) { Remove-Item -LiteralPath $singleExe -Force }
try {
  dotnet build $bundleProject -c Release "-p:BundlePayload=$zip" "-p:OutputPath=$bundleBuild\"
  if ($LASTEXITCODE -ne 0) { throw "Single-file launcher build failed." }
  $builtBundle = Join-Path $bundleBuild "GTAEmblemMaker-v1.1.3.exe"
  Copy-Item -LiteralPath $builtBundle -Destination $singleExe -Force
  $prepareProcess = Start-Process -FilePath $singleExe -ArgumentList "--prepare-only" -WindowStyle Hidden -Wait -PassThru
  if ($prepareProcess.ExitCode -ne 0) { throw "Single-file launcher preparation check failed." }
} finally {
  if (Test-Path -LiteralPath $bundleBuild) { Remove-Item -LiteralPath $bundleBuild -Recurse -Force }
}

if (-not (Test-Path -LiteralPath $zip)) { throw "Portable ZIP was not created." }
if (-not (Test-Path -LiteralPath $singleExe)) { throw "Single-file launcher was not created." }
if ((Get-Item -LiteralPath $singleExe).Length -le (Get-Item -LiteralPath $zip).Length) {
  throw "Single-file launcher does not contain the portable payload."
}

Write-Host ("Package written to {0} ({1:N2} MB)" -f $out, ($bytes / 1MB))
Write-Host ("Release archive written to {0}" -f $zip)
Write-Host ("Single-file launcher written to {0}" -f $singleExe)
Write-Host ("ZIP SHA-256: {0}" -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)
Write-Host ("EXE SHA-256: {0}" -f (Get-FileHash -LiteralPath $singleExe -Algorithm SHA256).Hash)
