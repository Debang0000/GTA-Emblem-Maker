using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEmblemMaker.Core;
using Microsoft.Win32;

namespace GTAEmblemMaker
{
    public partial class MainWindow : Window
    {
        private readonly RuntimePaths paths;
        private readonly ProfileCatalog catalog;
        private readonly ObservableCollection<CompletedRun> completedRuns = new ObservableCollection<CompletedRun>();
        private SourceImage source;
        private CompletedRun selectedRun;
        private CancellationTokenSource cancellation;
        private bool running;

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                paths = RuntimePaths.Find();
                catalog = ProfileCatalog.Load(paths.ProfileFolder);
                ProfileBox.ItemsSource = catalog.Profiles;
                ProfileBox.SelectedItem = catalog.Default;
                ResultsBox.ItemsSource = completedRuns;
                StatusText.Text = "Ready";
            }
            catch (Exception exception)
            {
                StatusText.Text = exception.Message;
                OpenButton.IsEnabled = false;
            }
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open Image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|All files|*.*"
            };
            if (dialog.ShowDialog(this) == true) LoadSource(dialog.FileName);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (running) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length == 1) LoadSource(files[0]);
        }

        private void LoadSource(string path)
        {
            try
            {
                source = SourceImage.Load(path);
                var preview = new BitmapImage();
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.UriSource = new Uri(Path.GetFullPath(path));
                preview.EndInit();
                preview.Freeze();
                SourcePreview.Source = preview;
                SourceEmptyText.Visibility = Visibility.Collapsed;
                SourceInfoText.Text = source.Width + " x " + source.Height + "  |  " + (source.IsTransparent ? "Transparent" : "Opaque");
                completedRuns.Clear();
                selectedRun = null;
                OutputPreview.Source = null;
                OutputEmptyText.Visibility = Visibility.Visible;
                OutputInfoText.Text = "";
                var profile = ProfileBox.SelectedItem as FitProfile;
                var maximumLayers = profile != null ? profile.Stages[0].MaxLayers : 1500;
                RunProgress.Maximum = maximumLayers;
                RunProgress.Value = 0;
                ProgressText.Text = "0 / " + maximumLayers;
                CopyButton.IsEnabled = false;
                FolderButton.IsEnabled = false;
                ResultsBox.IsEnabled = false;
                StatusText.Text = "Source loaded";
                UpdateStartState();
            }
            catch (Exception exception)
            {
                StatusText.Text = exception.Message;
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ProfileBox.SelectedItem as FitProfile;
            if (source == null || profile == null || running) return;
            running = true;
            cancellation = new CancellationTokenSource();
            SetRunningState(true);
            completedRuns.Clear();
            selectedRun = null;
            OutputPreview.Source = null;
            OutputEmptyText.Visibility = Visibility.Visible;
            RunProgress.Maximum = profile.Stages[0].MaxLayers;
            RunProgress.Value = 0;
            ProgressText.Text = "0 / " + profile.Stages[0].MaxLayers;
            StatusText.Text = "Starting";
            try
            {
                if (GenerateAllBox.IsChecked == true) await RunAllAsync(cancellation.Token);
                else AddCompletedRun(await RunProfileAsync(profile, cancellation.Token));
                var preferred = FindCompleted("beam-pair");
                ResultsBox.SelectedItem = preferred ?? (completedRuns.Count == 0 ? null : completedRuns[completedRuns.Count - 1]);
                StatusText.Text = "Completed";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Canceled";
            }
            catch (Exception exception)
            {
                StatusText.Text = exception.Message;
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                running = false;
                SetRunningState(false);
            }
        }

        private async Task RunAllAsync(CancellationToken cancellationToken)
        {
            FitProfile beamProfile = null;
            FitProfile pairProfile = null;
            var remaining = new List<FitProfile>();
            for (var index = 0; index < catalog.Profiles.Count; index++)
            {
                var profile = catalog.Profiles[index];
                if (profile.Pipeline.Runner == "beam" && beamProfile == null) beamProfile = profile;
                else if (profile.Pipeline.Runner == "beam-pair" && pairProfile == null) pairProfile = profile;
                else remaining.Add(profile);
            }

            if (beamProfile == null || pairProfile == null || !PipelineEngine.CanShareBeam(beamProfile, pairProfile))
            {
                for (var index = 0; index < catalog.Profiles.Count; index++) AddCompletedRun(await RunProfileAsync(catalog.Profiles[index], cancellationToken));
                return;
            }

            StatusText.Text = "Beam Clean  |  starting";
            var beamRequest = Request(beamProfile);
            var beam = await PipelineEngine.RunBeamAsync(beamRequest, ProgressFor(beamProfile), cancellationToken);
            AddCompletedRun(Complete(beamProfile, beamRequest, beam));

            var pairRequest = Request(pairProfile);
            var pairTask = CompletePairAsync(pairProfile, pairRequest, beam, cancellationToken);
            Task<CompletedRun> concurrentTask = null;
            if (remaining.Count > 0)
            {
                concurrentTask = RunProfileAsync(remaining[0], cancellationToken);
                remaining.RemoveAt(0);
            }
            if (concurrentTask != null)
            {
                await Task.WhenAll(pairTask, concurrentTask);
                AddCompletedRun(pairTask.Result);
                AddCompletedRun(concurrentTask.Result);
            }
            else AddCompletedRun(await pairTask);
            for (var index = 0; index < remaining.Count; index++) AddCompletedRun(await RunProfileAsync(remaining[index], cancellationToken));
        }

        private async Task<CompletedRun> RunProfileAsync(FitProfile profile, CancellationToken cancellationToken)
        {
            StatusText.Text = profile.DisplayName + "  |  starting";
            var request = Request(profile);
            var fit = await PipelineEngine.RunAsync(request, ProgressFor(profile), cancellationToken);
            return Complete(profile, request, fit);
        }

        private async Task<CompletedRun> CompletePairAsync(FitProfile profile, FitRequest request, FitResult beam, CancellationToken cancellationToken)
        {
            var fit = await PipelineEngine.RefinePairAsync(request, beam, ProgressFor(profile), cancellationToken);
            return Complete(profile, request, fit);
        }

        private FitRequest Request(FitProfile profile)
        {
            return new FitRequest(profile, source, paths.CudaScorer, paths.PerceptualModelFolder);
        }

        private IProgress<FitProgress> ProgressFor(FitProfile profile)
        {
            return new Progress<FitProgress>(value => UpdateProgress(profile.DisplayName, value));
        }

        private static CompletedRun Complete(FitProfile profile, FitRequest request, FitResult fit)
        {
            return new CompletedRun(profile, fit, RunArtifacts.Write(request, fit));
        }

        private void AddCompletedRun(CompletedRun completed)
        {
            completedRuns.Add(completed);
            ResultsBox.IsEnabled = true;
            ResultsBox.SelectedItem = completed;
        }

        private CompletedRun FindCompleted(string runner)
        {
            for (var index = 0; index < completedRuns.Count; index++) if (completedRuns[index].Profile.Pipeline.Runner == runner) return completedRuns[index];
            return null;
        }

        private void UpdateProgress(string profileName, FitProgress progress)
        {
            RunProgress.Maximum = progress.MaximumLayers;
            RunProgress.Value = progress.Layer;
            ProgressText.Text = progress.Layer + " / " + progress.MaximumLayers;
            StatusText.Text = profileName + "  |  " + progress.ShapeFamily + "  |  " + progress.GeneratedCodeLength.ToString("N0") + " chars  |  energy " + progress.Energy.ToString("0.000000");
            if (progress.PreviewRgba != null)
            {
                OutputPreview.Source = PreviewBitmap(progress.PreviewRgba);
                OutputEmptyText.Visibility = Visibility.Collapsed;
                OutputInfoText.Text = "Preview at " + progress.Layer + " layers";
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (cancellation != null) cancellation.Cancel();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedRun == null) return;
            try
            {
                Clipboard.SetText(selectedRun.Result.Payload.ConsoleCode);
                StatusText.Text = "Import code copied";
            }
            catch (Exception exception)
            {
                StatusText.Text = exception.Message;
            }
        }

        private void FolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedRun == null) return;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + selectedRun.Artifacts.RunFolder + "\"") { UseShellExecute = true });
        }

        private void ProfileBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateStartState();
        }

        private void GenerateAllBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ProfileBox != null) ProfileBox.IsEnabled = !running && GenerateAllBox.IsChecked != true;
            UpdateStartState();
        }

        private void ResultsBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            selectedRun = ResultsBox.SelectedItem as CompletedRun;
            if (selectedRun == null) return;
            OutputPreview.Source = PreviewBitmap(RunArtifacts.RenderPayloadPreview(selectedRun.Result.Shapes, selectedRun.Result.Payload));
            OutputEmptyText.Visibility = Visibility.Collapsed;
            OutputInfoText.Text = selectedRun.Result.CompletedLayers + " layers  |  " + selectedRun.Result.Payload.GeneratedCodeLength.ToString("N0") + " characters";
            CopyButton.IsEnabled = !running;
            FolderButton.IsEnabled = !running;
        }

        private void SetRunningState(bool value)
        {
            OpenButton.IsEnabled = !value;
            ProfileBox.IsEnabled = !value && GenerateAllBox.IsChecked != true;
            GenerateAllBox.IsEnabled = !value;
            StopButton.IsEnabled = value;
            CopyButton.IsEnabled = !value && selectedRun != null;
            FolderButton.IsEnabled = !value && selectedRun != null;
            UpdateStartState();
        }

        private void UpdateStartState()
        {
            StartButton.IsEnabled = !running && source != null && ProfileBox.SelectedItem != null && paths != null;
        }

        private static BitmapSource PreviewBitmap(byte[] rgba)
        {
            var bgra = (byte[])rgba.Clone();
            for (var index = 0; index < bgra.Length; index += 4)
            {
                var red = bgra[index];
                bgra[index] = bgra[index + 2];
                bgra[index + 2] = red;
            }
            var bitmap = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Pbgra32, null, bgra, 512 * 4);
            bitmap.Freeze();
            return bitmap;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (cancellation != null) cancellation.Cancel();
        }
    }

    internal sealed class CompletedRun
    {
        internal FitProfile Profile { get; private set; }
        internal FitResult Result { get; private set; }
        internal RunArtifactSet Artifacts { get; private set; }
        public string DisplayName { get; private set; }

        internal CompletedRun(FitProfile profile, FitResult result, RunArtifactSet artifacts)
        {
            Profile = profile;
            Result = result;
            Artifacts = artifacts;
            DisplayName = profile.DisplayName + "  |  " + result.Payload.GeneratedCodeLength.ToString("N0");
        }
    }

    internal sealed class RuntimePaths
    {
        internal string ProfileFolder { get; private set; }
        internal string CudaScorer { get; private set; }
        internal string PerceptualModelFolder { get; private set; }

        private RuntimePaths(string profileFolder, string cudaScorer, string perceptualModelFolder)
        {
            ProfileFolder = profileFolder;
            CudaScorer = cudaScorer;
            PerceptualModelFolder = perceptualModelFolder;
        }

        internal static RuntimePaths Find()
        {
            var baseFolder = AppDomain.CurrentDomain.BaseDirectory;
            var packaged = new RuntimePaths(
                Path.Combine(baseFolder, "profiles"),
                Path.Combine(baseFolder, "runtime", "cuda", "cuda-scorer.exe"),
                Path.Combine(baseFolder, "runtime", "perceptual"));
            if (packaged.Exists()) return packaged;

            var folder = new DirectoryInfo(baseFolder);
            while (folder != null)
            {
                var development = new RuntimePaths(
                    Path.Combine(folder.FullName, "profiles"),
                    Path.Combine(folder.FullName, "third_party", "cuda-scorer", "bin", "cuda-scorer.exe"),
                    Path.Combine(folder.FullName, "third_party", "lpips-winml", "model"));
                if (development.Exists()) return development;
                folder = folder.Parent;
            }
            throw new FileNotFoundException("The packaged profiles and runtime assets were not found.");
        }

        private bool Exists()
        {
            return Directory.Exists(ProfileFolder) && File.Exists(CudaScorer) && Directory.Exists(PerceptualModelFolder);
        }
    }
}
