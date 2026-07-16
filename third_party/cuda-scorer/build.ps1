param(
  [switch]$NoSmoke
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "bin"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$nvcc = $env:NVCC_PATH
if (-not $nvcc) {
  $defaultNvcc = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3\bin\nvcc.exe"
  if (Test-Path $defaultNvcc) {
    $nvcc = $defaultNvcc
  } else {
    $nvcc = "nvcc"
  }
}

$clBin = $env:CUDA_CL_BIN
if (-not $clBin) {
  $cl = Get-ChildItem "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter cl.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\bin\Hostx64\x64\cl.exe" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
  if ($cl) {
    $clBin = Split-Path -Parent $cl.FullName
  }
}

$args = @(
  "-std=c++17",
  "-O2",
  "-arch=sm_89"
)
if ($clBin) {
  $args += @("-ccbin", $clBin)
}
$args += @(
  "-Xcompiler", "/utf-8",
  "-Xcompiler", "/Zc:preprocessor",
  "-I", (Join-Path $root "src"),
  "-o", (Join-Path $out "cuda-scorer.exe"),
  (Join-Path $root "src\main.cu")
)

& $nvcc @args
if ($LASTEXITCODE -ne 0) {
  throw "nvcc failed with exit code $LASTEXITCODE"
}

if (-not $NoSmoke) {
  & (Join-Path $out "cuda-scorer.exe") --mode smoke
  if ($LASTEXITCODE -ne 0) {
    throw "cuda-scorer smoke failed with exit code $LASTEXITCODE"
  }
}
