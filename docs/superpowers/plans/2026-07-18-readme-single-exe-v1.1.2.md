# README, Single EXE, and v1.1.2 Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite the README around complex-image emblem conversion, add a tested self-extracting Windows executable, and publish GitHub Release v1.1.2 with both EXE and ZIP assets.

**Architecture:** Keep the audited portable directory as the only runtime payload. A small .NET Framework 4.8 x64 launcher embeds the portable ZIP, extracts it to a hash-qualified LocalAppData cache, validates the cache, and starts the existing WPF application without changing its runtime layout. The README presents product outcomes first, then explains the constrained search and systems work that produces them.

**Tech Stack:** C# 7.x, .NET Framework 4.8, WPF, `System.IO.Compression`, PowerShell 5.1, CUDA, ONNX Runtime DirectML, GitHub CLI.

## Global Constraints

- Keep the existing .NET Framework 4.8 and Windows x64 runtime baseline.
- Add no third-party packaging dependency.
- Publish both `GTAEmblemMaker-v1.1.2.exe` and `GTAEmblemMaker-v1.1.2.zip`.
- Keep all three production profile budgets at decimal `1250000` for reliable Social Club imports.
- Keep Beam Clean as the only default profile.
- Do not present transparency or CUDA availability as product selling points.
- Name Emblem Helper and compare the intended use cases without claiming universal superiority.
- Add no fake example images; mention the planned cat, anime, and optimization comparisons in prose.
- Keep source code, comments, release notes, and README copy in English.
- Do not alter the fitting algorithms or Rockstar exporter in this release task.

---

## File Map

- Create `native/GTAEmblemMaker.Bundle/GTAEmblemMaker.Bundle.csproj`: builds the one-file distribution wrapper and embeds a package ZIP supplied by MSBuild.
- Create `native/GTAEmblemMaker.Bundle/Program.cs`: obtains the embedded payload, prepares the cache, launches the real application, and reports startup errors.
- Create `native/GTAEmblemMaker.Bundle/BundleLauncher.cs`: owns hashing, zip-slip-safe extraction, cache validation, and process launch.
- Create `native/GTAEmblemMaker.Checks/BundleLauncherChecks.cs`: runnable cache, extraction, repair, and path-safety checks.
- Modify `native/GTAEmblemMaker.Checks/GTAEmblemMaker.Checks.csproj`: compile-link the launcher logic and add compression references.
- Modify `native/GTAEmblemMaker.Checks/Program.cs`: run `BundleLauncherChecks` in the standard Release check suite.
- Modify `scripts/package-windows.ps1`: build the wrapper from the audited ZIP and emit both release assets.
- Rewrite `README.md`: content-first product positioning, technical explanation, requirements, usage, and planned examples.
- Modify `assets/features-dark.svg` and `assets/features-light.svg`: replace generic implementation cards with outcome-oriented product cards.
- Delete `assets/install-dark.svg`, `assets/install-light.svg`, `assets/steps-dark.svg`, and `assets/steps-light.svg`: remove decorative components no longer referenced by the README.
- Create `docs/releases/v1.1.2.md`: exact public GitHub Release notes.

---

### Task 1: Self-Extracting Launcher Logic

**Files:**
- Create: `native/GTAEmblemMaker.Bundle/GTAEmblemMaker.Bundle.csproj`
- Create: `native/GTAEmblemMaker.Bundle/Program.cs`
- Create: `native/GTAEmblemMaker.Bundle/BundleLauncher.cs`
- Create: `native/GTAEmblemMaker.Checks/BundleLauncherChecks.cs`
- Modify: `native/GTAEmblemMaker.Checks/GTAEmblemMaker.Checks.csproj`
- Modify: `native/GTAEmblemMaker.Checks/Program.cs`

**Interfaces:**
- Consumes: a ZIP stream whose root contains `GTAEmblemMaker.exe`, `profiles`, `runtime`, and packaged dependency files.
- Produces: `BundleLauncher.PreparePayload(Stream payload, string localAppData, string version) -> string`, returning the validated extracted package directory.
- Produces: `BundleLauncher.Launch(string packageFolder) -> void`, starting the existing app with the correct working directory.

- [ ] **Step 1: Write the failing launcher checks**

Create `BundleLauncherChecks.cs` with one standard-library-only runnable check:

```csharp
internal static class BundleLauncherChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "GTAEmblemMaker-BundleCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var payload = CreatePayload())
            {
                var first = BundleLauncher.PreparePayload(payload, root, "1.1.2");
                Check.True(File.Exists(Path.Combine(first, "GTAEmblemMaker.exe")), "bundle extracts application");
                Check.True(File.Exists(Path.Combine(first, "profiles", "v1-beam-clean.json")), "bundle extracts profiles");
                payload.Position = 0;
                Check.Equal(first, BundleLauncher.PreparePayload(payload, root, "1.1.2"), "bundle reuses cache");
                File.Delete(Path.Combine(first, "profiles", "v1-beam-clean.json"));
                payload.Position = 0;
                Check.Equal(first, BundleLauncher.PreparePayload(payload, root, "1.1.2"), "bundle repairs cache");
                Check.True(File.Exists(Path.Combine(first, "profiles", "v1-beam-clean.json")), "bundle repair restores file");
            }
            using (var unsafePayload = CreateUnsafePayload())
            {
                Check.Throws<InvalidDataException>(() => BundleLauncher.PreparePayload(unsafePayload, root, "1.1.2"), "bundle rejects zip traversal");
                Check.False(File.Exists(Path.Combine(root, "escape.txt")), "bundle traversal stays contained");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
```

`CreatePayload()` must build an in-memory ZIP with `GTAEmblemMaker.exe` and `profiles/v1-beam-clean.json`; `CreateUnsafePayload()` must contain `../../escape.txt`.

- [ ] **Step 2: Wire the failing checks into the existing suite**

In `GTAEmblemMaker.Checks.csproj`, link the production launcher source and reference the framework compression assemblies:

```xml
<Compile Include="..\GTAEmblemMaker.Bundle\BundleLauncher.cs" Link="BundleLauncher.cs" />
<Reference Include="System.IO.Compression" />
<Reference Include="System.IO.Compression.FileSystem" />
```

In `Program.Main`, add:

```csharp
BundleLauncherChecks.Run();
```

- [ ] **Step 3: Run the checks and verify RED**

Run:

```powershell
dotnet build native\GTAEmblemMaker.Checks\GTAEmblemMaker.Checks.csproj -c Release
```

Expected: FAIL because `BundleLauncher.cs` and `PreparePayload` do not exist.

- [ ] **Step 4: Implement minimal safe extraction and cache reuse**

Create `BundleLauncher.cs` in namespace `GTAEmblemMaker.Bundle` with these methods:

```csharp
internal static string PreparePayload(Stream payload, string localAppData, string version)
internal static void Launch(string packageFolder)
private static string ComputeSha256(Stream payload)
private static bool CacheMatches(Stream payload, string folder, string hash)
private static void Extract(Stream payload, string folder)
private static string EntryPath(string root, ZipArchiveEntry entry)
```

Required behavior:

```csharp
var hash = ComputeSha256(payload);
var appRoot = Path.Combine(localAppData, "GTAEmblemMaker", "app");
var folder = Path.Combine(appRoot, version + "-" + hash.Substring(0, 12).ToLowerInvariant());
```

- Reset `payload.Position` before every hash or ZIP read.
- Accept cache reuse only when `.payload-sha256` equals the complete hash and every non-directory ZIP entry exists with the expected uncompressed length.
- Normalize both slash forms in entry names, call `Path.GetFullPath`, and reject paths outside the extraction root with `InvalidDataException`.
- Extract to `.tmp-<version>-<guid>` under `appRoot`, validate all file lengths, write `.payload-sha256`, then use `Directory.Move` to publish the complete cache.
- If the exact hash-qualified cache exists but fails validation, delete only that validated path and rebuild it.
- On failure, delete only the temporary directory created by the current call.
- Launch `GTAEmblemMaker.exe` with `UseShellExecute = true` and `WorkingDirectory = packageFolder`.

- [ ] **Step 5: Create the wrapper project and entry point**

Create `GTAEmblemMaker.Bundle.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <OutputType>WinExe</OutputType>
    <UseWPF>true</UseWPF>
    <PlatformTarget>x64</PlatformTarget>
    <AssemblyName>GTAEmblemMaker-v1.1.2</AssemblyName>
    <RootNamespace>GTAEmblemMaker.Bundle</RootNamespace>
    <Version>1.1.2</Version>
  </PropertyGroup>
  <ItemGroup Condition="'$(BundlePayload)' != ''">
    <EmbeddedResource Include="$(BundlePayload)" LogicalName="GTAEmblemMaker.Payload.zip" />
  </ItemGroup>
</Project>
```

Create `Program.cs` with `[STAThread]`, read `GTAEmblemMaker.Payload.zip`, call `PreparePayload`, support `--prepare-only` for packaging smoke checks, call `Launch` otherwise, and show `MessageBox.Show(exception.Message, "GTA Emblem Maker", MessageBoxButton.OK, MessageBoxImage.Error)` before returning exit code 1 on failure.

- [ ] **Step 6: Run the checks and verify GREEN**

Run:

```powershell
dotnet build native\GTAEmblemMaker.Checks\GTAEmblemMaker.Checks.csproj -c Release
native\GTAEmblemMaker.Checks\bin\Release\net48\GTAEmblemMaker.Checks.exe
```

Expected: build succeeds with 0 warnings and all checks exit 0.

- [ ] **Step 7: Commit the launcher slice**

```powershell
git add native/GTAEmblemMaker.Bundle native/GTAEmblemMaker.Checks/BundleLauncherChecks.cs native/GTAEmblemMaker.Checks/GTAEmblemMaker.Checks.csproj native/GTAEmblemMaker.Checks/Program.cs
git commit -m "Add self-extracting release launcher"
```

---

### Task 2: Dual-Asset Packaging

**Files:**
- Modify: `scripts/package-windows.ps1`

**Interfaces:**
- Consumes: the audited portable folder and `GTAEmblemMaker-v1.1.2.zip` already produced by the script.
- Produces: `release/GTAEmblemMaker-v1.1.2.exe` containing that exact ZIP as `GTAEmblemMaker.Payload.zip`.

- [ ] **Step 1: Add the bundle build after ZIP creation**

After `Compress-Archive`, define exact sibling paths:

```powershell
$bundleProject = Join-Path $root "native\GTAEmblemMaker.Bundle\GTAEmblemMaker.Bundle.csproj"
$bundleBuild = Join-Path $root "release\.bundle-v1.1.2"
$singleExe = "$out.exe"
```

Delete only `$bundleBuild` and `$singleExe` after their existing inside-repository path checks. Build with:

```powershell
dotnet build $bundleProject -c Release "-p:BundlePayload=$zip" "-p:OutputPath=$bundleBuild\"
```

Copy `GTAEmblemMaker-v1.1.2.exe` from `$bundleBuild` to `$singleExe`, run `$singleExe --prepare-only`, require exit code 0, then remove `$bundleBuild`.

- [ ] **Step 2: Add package assertions**

The script must fail unless:

```powershell
if (-not (Test-Path -LiteralPath $zip)) { throw "Portable ZIP was not created." }
if (-not (Test-Path -LiteralPath $singleExe)) { throw "Single-file launcher was not created." }
if ((Get-Item -LiteralPath $singleExe).Length -le (Get-Item -LiteralPath $zip).Length) {
  throw "Single-file launcher does not contain the portable payload."
}
```

Print both final asset paths and SHA-256 hashes.

- [ ] **Step 3: Build both assets**

Run:

```powershell
.\scripts\package-windows.ps1
```

Expected: CUDA smoke success, x64 Release build success, and both v1.1.2 asset paths printed.

- [ ] **Step 4: Verify wrapper extraction and launch**

Run the EXE once, detect the newly launched `GTAEmblemMaker.exe` whose executable path is under `%LOCALAPPDATA%\GTAEmblemMaker\app\v1.1.2-*`, verify the window process remains alive, and stop only that exact new process after the smoke check. Run `--prepare-only` again and verify the cache directory timestamp is unchanged.

- [ ] **Step 5: Commit the packaging slice**

```powershell
git add scripts/package-windows.ps1
git commit -m "Package v1.1.2 as EXE and ZIP"
```

---

### Task 3: Content-First README

**Files:**
- Modify: `README.md`
- Modify: `assets/features-dark.svg`
- Modify: `assets/features-light.svg`
- Delete: `assets/install-dark.svg`
- Delete: `assets/install-light.svg`
- Delete: `assets/steps-dark.svg`
- Delete: `assets/steps-light.svg`

**Interfaces:**
- Consumes: published asset names and the three existing production profiles.
- Produces: GitHub-renderable English documentation with a direct v1.1.2 EXE download and ZIP alternative.

- [ ] **Step 1: Replace the first-screen copy**

Use this value statement directly below the restrained banner:

```markdown
<p align="center"><strong>A quality-first GTA emblem converter for complex images.</strong></p>

GTA Emblem Maker converts photos, illustrations, anime artwork, and other detailed raster images into layered Rockstar emblems. It deliberately spends more compute time on reconstruction quality instead of trying to produce an instant outline-based result.
```

Keep the unofficial-project disclaimer and a compact badge row. Do not call WPF, CUDA, transparency, or pipeline count a selling point.

- [ ] **Step 2: Add the fair Emblem Helper comparison**

Add `## Why another emblem converter?` followed by:

```markdown
Emblem Helper is the tool most creators already know: it runs online, finishes extremely quickly, requires no installation, and works well for structurally simple logo-like images. GTA Emblem Maker does not try to replace that workflow.

This project covers the opposite use case. It is for people who want to turn a complex picture into an emblem and are willing to download a Windows application, use an NVIDIA GPU, and wait longer for a more involved layered fit.

| | Emblem Helper | GTA Emblem Maker |
| --- | --- | --- |
| Best suited to | Simple logos and clean shapes | Photos, illustrations, anime art, and complex compositions |
| Delivery | Online | Local Windows application |
| Speed | Extremely fast | Quality-first and compute-intensive |
| Hardware | No local GPU requirement | NVIDIA CUDA-capable GPU |
| Goal | Fast conversion of simple structure | Constrained reconstruction of complex structure and detail |
```

- [ ] **Step 3: Replace generic feature cards**

Rewrite both feature SVGs with these exact headings and concise supporting lines:

```text
Made for Complex Images — Fit layered structure beyond simple logo outlines.
Quality Over Instant Results — Spend search time where additional detail can survive.
Budget-Aware Detail Fitting — Improve the image without crossing Rockstar's payload budget.
Save-Ready Rockstar Output — Produce verified SVG and layer data for Social Club.
```

Keep the existing restrained green/amber palette and dark/light variants. Do not add animation or new SVG files.

- [ ] **Step 4: Add technical explanation tied to the product outcome**

Create `## How it handles complex images` with these subsections:

```markdown
### Constrained layered reconstruction
The source is treated as a 512 × 512 target that must be rebuilt from Rockstar-supported layered primitives while the generated import code stays within a fixed size budget. Each candidate can change position, size, rotation, color, opacity, and shape family.

### Search that does not commit too early
Beam search keeps multiple partial reconstructions alive instead of trusting every early greedy choice. The other profiles can rerank strong pixel-loss candidates with LPIPS AlexNet 224 or native edge-detail signals, helping preserve structure that a single error metric can miss.

### Mixed primitives and verified catalog geometry
The catalog pipeline combines rotated ellipses, rectangles, triangles, line-like shapes, nine official Rockstar curves, and two official round shapes. Export uses the geometry, cleanup precision, and expanded SVG paths observed from Rockstar's own editor.

### Payload cost is part of the optimization
The fitter tracks generated-code cost while adding layers. A visually useful candidate still has to fit inside the decimal 1,250,000-character production budget and produce layer data that Social Club accepts.
```

- [ ] **Step 5: Add factual engineering depth**

Create `## Engineering the search` and explain:

- Persistent CUDA scorer and batched candidate evaluation make the large search feasible; CUDA is an implementation requirement, not the product claim.
- Candidate scoring stays resident across layers to reduce repeated CPU/GPU transfer and process startup.
- ONNX Runtime DirectML hosts LPIPS reranking, while catalog fitting can use a native edge-detail backend.
- The WPF app provides progress, cancellation, source/output comparison, multiple-result selection, and complete run artifacts.
- The save-compatible exporter came from testing official editor output and correcting dimensions, rounding, transforms, and serialized paths.

- [ ] **Step 6: Keep usage, requirements, profiles, examples, and build information concise**

Quick Start must lead with:

```markdown
1. Download [`GTAEmblemMaker-v1.1.2.exe`](https://github.com/Debang0000/GTA-Emblem-Maker/releases/download/v1.1.2/GTAEmblemMaker-v1.1.2.exe). The [portable ZIP](https://github.com/Debang0000/GTA-Emblem-Maker/releases/download/v1.1.2/GTAEmblemMaker-v1.1.2.zip) is also available.
```

Retain the three-profile table without calling any profile best. Add a tradeoff paragraph stating that difficult images can take a long time and remain approximations constrained by Rockstar primitives and payload size. Mention transparent inputs only under supported behavior.

Add:

```markdown
## Examples

Real comparisons are coming later, including cat photos, anime illustrations, and optimization/conversion comparisons. No placeholder output is shown here because the examples should come from reproducible runs of the released build.
```

Keep the current build commands and third-party/unofficial disclaimers.

- [ ] **Step 7: Remove unused decorative assets and validate references**

Delete the four install/steps SVGs after removing their README references. Run:

```powershell
git grep -n -i -e "Best Quality" -e "Four Pipelines" -e "Native Windows UI" -- README.md assets
```

Expected: no matches. Parse every `srcset="./assets/..."` and `src="./assets/..."` reference in README and require every referenced local asset to exist.

- [ ] **Step 8: Commit the README slice**

```powershell
git add README.md assets
git commit -m "Rewrite README around complex image fitting"
```

---

### Task 4: Release Notes and Full Verification

**Files:**
- Create: `docs/releases/v1.1.2.md`

**Interfaces:**
- Consumes: verified EXE/ZIP hashes and the final implementation commit set.
- Produces: exact public release body for `gh release create`.

- [ ] **Step 1: Write exact release notes**

Create `docs/releases/v1.1.2.md`:

```markdown
## What changed

- Fixed Official Catalog Quality exports to use Rockstar-compatible geometry, cleanup precision, transforms, and expanded SVG paths.
- Kept the production generated-code budget at decimal 1,250,000 for more reliable Social Club imports.
- Removed the misleading Best Quality profile; Beam Clean is now the default.
- Ships three fitting profiles: Beam Clean, Perceptual AlexNet 224, and Official Catalog Quality.
- Added a self-extracting EXE for one-file download while retaining the portable ZIP.

## Downloads

- `GTAEmblemMaker-v1.1.2.exe` — recommended one-file download; extracts a validated versioned cache under LocalAppData and then launches the app.
- `GTAEmblemMaker-v1.1.2.zip` — portable package and fallback for users who prefer a visible directory layout.

## Requirements and tradeoffs

- Windows 10 version 1809 or newer, x64, .NET Framework 4.8, and an NVIDIA CUDA-capable GPU.
- Complex images can take a long time to fit.
- The application is unsigned, so Windows SmartScreen or antivirus software may warn about the self-extracting EXE. Use the ZIP if preferred.

This is an unofficial community project and is not affiliated with or endorsed by Rockstar Games or Take-Two Interactive.
```

- [ ] **Step 2: Run the verification loop**

Run:

```powershell
.\third_party\cuda-scorer\build.ps1
dotnet build native\GTAEmblemMaker.sln -c Release -p:Platform=x64 --no-restore
native\GTAEmblemMaker.Checks\bin\x64\Release\net48\GTAEmblemMaker.Checks.exe
.\scripts\package-windows.ps1 -NoBuild
git diff --check
```

Expected: CUDA smoke success, 0 build warnings/errors, checks exit 0, both assets rebuilt, and no whitespace errors.

- [ ] **Step 3: Audit both release assets**

Require:

- ZIP contains exactly three profile JSON files and no `v1-best-quality` entry.
- ZIP contains no `.js`, `.html`, `.ps1`, `.bat`, `.cmd`, `.mjs`, or `node.exe` runtime.
- EXE has embedded resource `GTAEmblemMaker.Payload.zip`.
- Extracted wrapper cache contains the same file names and lengths as the portable ZIP.
- Second `--prepare-only` run reuses the same cache.
- Record SHA-256 and byte length for both assets.

- [ ] **Step 4: Commit release notes**

```powershell
git add docs/releases/v1.1.2.md
git commit -m "Add v1.1.2 release notes"
```

---

### Task 5: Publish main and GitHub Release v1.1.2

**Files:**
- No additional source changes.

**Interfaces:**
- Consumes: clean verified git HEAD and both local release assets.
- Produces: updated `main`, tag `v1.1.2`, and a public GitHub Release with two assets.

- [ ] **Step 1: Confirm publication state**

Run:

```powershell
git status --short
git fetch origin --prune
gh auth status
gh release view v1.1.2
```

Expected: clean worktree, authenticated GitHub CLI, `origin/main` is an ancestor of HEAD, and release v1.1.2 does not yet exist.

- [ ] **Step 2: Push the verified HEAD to the feature branch and main**

Run:

```powershell
git push origin codex/v1.1.2-catalog-export
git push origin HEAD:main
```

Fetch `origin/main` and require it equals local HEAD.

- [ ] **Step 3: Create the tag and public release**

Run:

```powershell
git tag -a v1.1.2 -m "GTA Emblem Maker v1.1.2"
git push origin v1.1.2
gh release create v1.1.2 `
  "release\GTAEmblemMaker-v1.1.2.exe" `
  "release\GTAEmblemMaker-v1.1.2.zip" `
  --title "GTA Emblem Maker v1.1.2" `
  --notes-file "docs\releases\v1.1.2.md" `
  --verify-tag
```

- [ ] **Step 4: Verify the live release**

Run:

```powershell
gh release view v1.1.2 --json tagName,name,isDraft,isPrerelease,publishedAt,assets,url
```

Require: tag/name v1.1.2, `isDraft=false`, `isPrerelease=false`, exactly two assets with the expected names and byte sizes, and the latest release points to v1.1.2.

- [ ] **Step 5: Final repository check**

Require clean `git status`, local HEAD equals `origin/main`, README at `origin/main` contains the v1.1.2 EXE link and Emblem Helper comparison, and the package hashes still match the uploaded assets.
