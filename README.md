# GTA Emblem Maker

An unofficial native Windows application that converts an image into a Rockstar-compatible GTA emblem payload. It preserves transparent backgrounds, shows a live fitting preview, and enforces the 1,250,000-character import limit.

## Pipelines

| Pipeline | Method |
| --- | --- |
| **Best Quality** | Beam search followed by local exact pair refinement. Recommended for final output. |
| **Beam Clean** | Beam search only. Faster and useful when pair refinement changes little. |
| **Perceptual** | Greedy search with LPIPS AlexNet 224 reranking. Provides a different visual tradeoff. |
| **Official Catalog Quality** | The recovered mixed-primitive schedule plus nine official Rockstar curves and two official round shapes, scored on CUDA and reranked with native edge detail. |

`Generate all` runs the shared Beam stage once, then overlaps CPU pair refinement with the GPU Perceptual pipeline. Completed results can be compared in the application before copying the import code.

## How It Works

1. The source is normalized to a 512 x 512 RGBA target. Transparent inputs use alpha-aware error weighting and export with a transparent SVG background.
2. A persistent CUDA service generates and scores rotated ellipses and the selected official Rockstar catalog shapes without repeatedly transferring the working image between CPU and GPU. Candidate selection combines weighted pixel error, code-length awareness, and local parameter search.
3. Beam search keeps the best two partial solutions and expands two branches per layer, reducing early greedy mistakes without making the search prohibitively large.
4. Best Quality ranks high-error image windows and jointly refines visible layer pairs. Position, size, rotation, color, and alpha changes are accepted only when local and global error improve and the payload remains within budget.
5. The Perceptual pipeline reranks top pixel-loss candidates with LPIPS v0.1 AlexNet 224 through ONNX Runtime DirectML, starting at the configured late fitting stage.

Pipeline behavior is stored in JSON profiles under `profiles`, so future versions can tune or add profiles without changing the UI.

Version 1.1 adds exact official curve and round-shape export, including independent horizontal and vertical scaling. Fitting quality still has room for improvement on fine facial and line-art details.

## Usage

Download `GTAEmblemMaker-v1.1.1.zip` from GitHub Releases, extract it, and run `GTAEmblemMaker.exe`. Select an image and pipeline, generate the emblem, then use **Copy Code** for the Rockstar import payload.

Runtime requirements: Windows x64, .NET Framework 4.8, and an NVIDIA CUDA-capable GPU.

## Build

Source builds also require the CUDA Toolkit and MSVC toolchain.

```powershell
.\scripts\package-windows.ps1
```

The portable package is written to `release\GTAEmblemMaker-v1.1.1.zip`.
