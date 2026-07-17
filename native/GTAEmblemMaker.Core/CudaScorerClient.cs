using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GTAEmblemMaker.Core
{
    public sealed class CudaScorerClient : IDisposable
    {
        private const int StartupTimeoutMilliseconds = 30000;
        private const int ExchangeTimeoutMilliseconds = 120000;
        private const int ShutdownTimeoutMilliseconds = 5000;

        private readonly Process process;
        private readonly Stream input;
        private readonly Stream output;
        private readonly int imageBytes;
        private readonly SemaphoreSlim access = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly StringBuilder standardError = new StringBuilder();
        private readonly object errorLock = new object();
        private int disposed;
        private int faulted;

        private CudaScorerClient(Process process, int imageBytes)
        {
            this.process = process;
            this.imageBytes = imageBytes;
            input = process.StandardOutput.BaseStream;
            output = process.StandardInput.BaseStream;
            process.ErrorDataReceived += CaptureStandardError;
            process.BeginErrorReadLine();
        }

        public string StandardError
        {
            get
            {
                lock (errorLock) return standardError.ToString();
            }
        }

        public static CudaScorerClient Start(string exePath, int width, int height, byte[] target, byte[] current)
        {
            return Start(exePath, width, height, target, current, CancellationToken.None);
        }

        public static CudaScorerClient Start(string exePath, int width, int height, byte[] target, byte[] current, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("CUDA scorer path is required.", "exePath");
            if (!string.Equals(Path.GetFileName(exePath), "cuda-scorer.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only the packaged cuda-scorer.exe is supported.", "exePath");
            if (!File.Exists(exePath)) throw new FileNotFoundException("CUDA scorer executable not found.", exePath);
            var imageBytes = CudaProtocol.ValidateInitImages(width, height, target, current);
            var request = CudaProtocol.CreateInitRequest(width, height, CalculateTotalError(target, current), target, current);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--mode server",
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException("CUDA scorer process did not start.");
            }
            catch
            {
                process.Dispose();
                throw;
            }

            CudaScorerClient client;
            try
            {
                client = new CudaScorerClient(process, imageBytes);
            }
            catch
            {
                CleanupStartedProcess(process);
                throw;
            }
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, client.lifetime.Token))
            {
                operation.CancelAfter(StartupTimeoutMilliseconds);
                using (operation.Token.Register(client.FaultSession))
                {
                    try
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        client.output.Write(request, 0, request.Length);
                        client.output.Flush();
                        var response = ReadExact(client.input, 12);
                        CudaProtocol.ParseTimingResponse("INIT", response);
                        return client;
                    }
                    catch (Exception exception)
                    {
                        var callerCanceled = cancellationToken.IsCancellationRequested;
                        var deadlineExpired = operation.IsCancellationRequested && !callerCanceled;
                        client.Abort();
                        if (callerCanceled) throw new OperationCanceledException(cancellationToken);
                        if (deadlineExpired) throw client.AddStandardError(new TimeoutException("CUDA scorer startup exceeded its deadline.", exception));
                        throw client.AddStandardError(exception);
                    }
                }
            }
        }

        public static ulong CalculateTotalError(byte[] target, byte[] current)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (current == null) throw new ArgumentNullException("current");
            if (target.Length != current.Length) throw new ArgumentException("Target and current images must have equal lengths.");
            if (target.Length > CudaProtocol.MaxImageBytes) throw new ArgumentOutOfRangeException("target", "Image length exceeds the production bound.");
            ulong total = 0;
            for (var index = 0; index < target.Length; index++)
            {
                var difference = target[index] - current[index];
                total += (ulong)(difference * difference);
            }
            return total;
        }

        public Task<double> SetWeightMapAsync(byte[] weights, CancellationToken cancellationToken)
        {
            if (weights == null) throw new ArgumentNullException("weights");
            if (weights.Length != imageBytes / 2) throw new ArgumentException("Weight map has the wrong length.", "weights");
            var request = CudaProtocol.CreateSetWeightMapRequest(weights);
            return ExchangeAsync(async token =>
            {
                await WriteAsync(request, token).ConfigureAwait(false);
                return CudaProtocol.ParseTimingResponse("SET_WEIGHT_MAP", await ReadExactAsync(12, token).ConfigureAwait(false));
            }, cancellationToken);
        }

        public Task<double> SetStrokeGuideAsync(byte[] saliencyQ8, byte[] tangentQ8, CancellationToken cancellationToken)
        {
            if (saliencyQ8 == null) throw new ArgumentNullException("saliencyQ8");
            if (tangentQ8 == null) throw new ArgumentNullException("tangentQ8");
            if (saliencyQ8.Length != imageBytes / 2 || tangentQ8.Length != imageBytes / 2) throw new ArgumentException("Stroke guide has the wrong length.");
            var request = CudaProtocol.CreateSetStrokeGuideRequest(saliencyQ8, tangentQ8);
            return ExchangeAsync(async token =>
            {
                await WriteAsync(request, token).ConfigureAwait(false);
                return CudaProtocol.ParseTimingResponse("SET_STROKE_GUIDE", await ReadExactAsync(12, token).ConfigureAwait(false));
            }, cancellationToken);
        }

        public Task<double> SetMultiScaleStrokeGuideAsync(byte[] detailSaliencyQ8, byte[] contourSaliencyQ8, byte[] tangentQ8, CancellationToken cancellationToken)
        {
            if (detailSaliencyQ8 == null) throw new ArgumentNullException("detailSaliencyQ8");
            if (contourSaliencyQ8 == null) throw new ArgumentNullException("contourSaliencyQ8");
            if (tangentQ8 == null) throw new ArgumentNullException("tangentQ8");
            if (detailSaliencyQ8.Length != imageBytes / 2 || contourSaliencyQ8.Length != imageBytes / 2 || tangentQ8.Length != imageBytes / 2) throw new ArgumentException("Multi-scale stroke guide has the wrong length.");
            var request = CudaProtocol.CreateSetMultiScaleStrokeGuideRequest(detailSaliencyQ8, contourSaliencyQ8, tangentQ8);
            return ExchangeAsync(async token =>
            {
                await WriteAsync(request, token).ConfigureAwait(false);
                return CudaProtocol.ParseTimingResponse("SET_MULTI_SCALE_STROKE_GUIDE", await ReadExactAsync(12, token).ConfigureAwait(false));
            }, cancellationToken);
        }

        public Task<double> SetStructuralGuideAsync(byte[] distanceQ8, byte[] tangentQ8, CancellationToken cancellationToken)
        {
            if (distanceQ8 == null) throw new ArgumentNullException("distanceQ8");
            if (tangentQ8 == null) throw new ArgumentNullException("tangentQ8");
            if (distanceQ8.Length != imageBytes / 2 || tangentQ8.Length != imageBytes / 2) throw new ArgumentException("Structural guide has the wrong length.");
            var request = CudaProtocol.CreateSetStructuralGuideRequest(distanceQ8, tangentQ8);
            return ExchangeAsync(async token =>
            {
                await WriteAsync(request, token).ConfigureAwait(false);
                return CudaProtocol.ParseTimingResponse("SET_STRUCTURAL_GUIDE", await ReadExactAsync(12, token).ConfigureAwait(false));
            }, cancellationToken);
        }

        public Task<double> UpdateCurrentAsync(ulong baseTotalError, byte[] current, CancellationToken cancellationToken)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (current.Length != imageBytes) throw new ArgumentException("Current image has the wrong length.", "current");
            var request = CudaProtocol.CreateUpdateCurrentRequest(baseTotalError, current);
            return ExchangeAsync(async token =>
            {
                await WriteAsync(request, token).ConfigureAwait(false);
                return CudaProtocol.ParseTimingResponse("UPDATE_CURRENT", await ReadExactAsync(12, token).ConfigureAwait(false));
            }, cancellationToken);
        }

        public Task<CudaSelectLayerResult> SelectLayerAsync(CudaSelectLayerRequest request, CancellationToken cancellationToken)
        {
            var expectedChainCount = request != null && request.Mode == CudaSelectLayerMode.MixedDeviceChunk
                ? CudaProtocol.ExpectedMixedChainCount(request)
                : 0;
            var bytes = CudaProtocol.CreateSelectLayerRequest(request);
            return ExchangeAsync(async token =>
            {
                await WriteAsync(bytes, token).ConfigureAwait(false);
                if (request.Mode == CudaSelectLayerMode.RotatedDeviceChunk)
                {
                    return CudaProtocol.ParseRotatedResponse(await ReadExactAsync(CudaProtocol.RotatedResponseSize, token).ConfigureAwait(false));
                }

                var prefix = await ReadExactAsync(CudaProtocol.MixedResponsePrefixSize, token).ConfigureAwait(false);
                var chainCount = CudaProtocol.ReadMixedChainCount(prefix);
                if (chainCount != expectedChainCount) throw new InvalidDataException("Mixed response chain count does not match the request.");
                var tail = await ReadExactAsync(checked((int)chainCount * CudaProtocol.MixedChainSize), token).ConfigureAwait(false);
                return CudaProtocol.ParseMixedResponse(prefix, tail);
            }, cancellationToken);
        }

        internal Task<CudaCatalogScoreResult> ScoreCatalogAsync(CatalogMaskAtlasEntry atlas, IReadOnlyList<CudaCatalogCandidate> candidates, bool weighted, CancellationToken cancellationToken)
        {
            var request = CudaProtocol.CreateCatalogScoreRequest(atlas, candidates, weighted);
            return ExchangeAsync(async token =>
            {
                await WriteAsync(request, token).ConfigureAwait(false);
                var prefix = await ReadExactAsync(CudaProtocol.CatalogResponsePrefixSize, token).ConfigureAwait(false);
                var tail = await ReadExactAsync(checked(candidates.Count * 32), token).ConfigureAwait(false);
                return CudaProtocol.ParseCatalogScoreResponse(prefix, tail, candidates.Count);
            }, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var acquired = false;
            try
            {
                acquired = access.Wait(ShutdownTimeoutMilliseconds);
                if (!acquired)
                {
                    TryCancel(lifetime);
                    FaultSession();
                    acquired = access.Wait(ShutdownTimeoutMilliseconds);
                }

                if (acquired && IsRunning()) TryShutdown();
                TryClose(output);
                if (!WaitForExitBounded(ShutdownTimeoutMilliseconds))
                {
                    TryKill();
                    WaitForExitBounded(ShutdownTimeoutMilliseconds);
                }
            }
            catch (Exception)
            {
                TryKill();
                WaitForExitBounded(ShutdownTimeoutMilliseconds);
            }
            finally
            {
                TryCancelErrorRead();
                TryCancel(lifetime);
                TryClose(output);
                TryClose(input);
                TryDispose(process);
                TryDispose(lifetime);
                TryDispose(access);
            }
        }

        private async Task<T> ExchangeAsync<T>(Func<CancellationToken, Task<T>> exchange, CancellationToken cancellationToken)
        {
            var acquired = false;
            var started = false;
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                operation.CancelAfter(ExchangeTimeoutMilliseconds);
                try
                {
                    await access.WaitAsync(operation.Token).ConfigureAwait(false);
                    acquired = true;
                    ThrowIfDisposed();
                    started = true;
                    using (operation.Token.Register(FaultSession))
                    {
                        return await exchange(operation.Token).ConfigureAwait(false);
                    }
                }
                catch (ObjectDisposedException) when (!started)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (started) FaultSession();
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    if (operation.IsCancellationRequested) throw AddStandardError(new TimeoutException("CUDA scorer exchange exceeded its deadline.", exception));
                    throw AddStandardError(exception);
                }
                finally
                {
                    if (acquired)
                    {
                        try { access.Release(); } catch (ObjectDisposedException) { }
                    }
                }
            }
        }

        private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await output.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            var bytes = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await input.ReadAsync(bytes, offset, count - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("CUDA scorer closed stdout after " + offset + " of " + count + " response bytes.");
                offset += read;
            }
            return bytes;
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var bytes = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(bytes, offset, count - offset);
                if (read == 0) throw new EndOfStreamException("CUDA scorer closed stdout after " + offset + " of " + count + " response bytes.");
                offset += read;
            }
            return bytes;
        }

        private void TryShutdown()
        {
            using (var deadline = new CancellationTokenSource(ShutdownTimeoutMilliseconds))
            using (deadline.Token.Register(FaultSession))
            {
                try
                {
                    WriteAsync(CudaProtocol.CreateShutdownRequest(), deadline.Token).GetAwaiter().GetResult();
                    CudaProtocol.ParseShutdownResponse(ReadExactAsync(4, deadline.Token).GetAwaiter().GetResult());
                }
                catch (Exception)
                {
                    FaultSession();
                }
            }
        }

        private void CaptureStandardError(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null) return;
            lock (errorLock) standardError.AppendLine(args.Data);
        }

        private Exception AddStandardError(Exception exception)
        {
            var error = StandardError.Trim();
            var message = error.Length == 0 ? exception.Message : exception.Message + Environment.NewLine + "cuda-scorer stderr:" + Environment.NewLine + error;
            if (exception is TimeoutException) return new TimeoutException(message, exception);
            if (exception is InvalidDataException) return new InvalidDataException(message, exception);
            if (exception is IOException) return new IOException(message, exception);
            return new InvalidOperationException(message, exception);
        }

        private void Abort()
        {
            Interlocked.Exchange(ref disposed, 1);
            TryCancel(lifetime);
            FaultSession();
            WaitForExitBounded(ShutdownTimeoutMilliseconds);
            TryCancelErrorRead();
            TryDispose(process);
            TryDispose(lifetime);
            TryDispose(access);
        }

        private void FaultSession()
        {
            Interlocked.Exchange(ref faulted, 1);
            TryClose(output);
            TryClose(input);
            TryKill();
        }

        // The packaged scorer is a single process and does not spawn descendants.
        private void TryKill()
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception) { }
        }

        private bool IsRunning()
        {
            try { return !process.HasExited; }
            catch (Exception) { return false; }
        }

        private bool WaitForExitBounded(int milliseconds)
        {
            try { return process.WaitForExit(milliseconds); }
            catch (Exception) { return false; }
        }

        private void TryCancelErrorRead()
        {
            try { process.CancelErrorRead(); }
            catch (Exception) { }
        }

        private static void TryClose(Stream stream)
        {
            try { stream.Close(); }
            catch (Exception) { }
        }

        private static void TryDispose(IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception) { }
        }

        private static void TryCancel(CancellationTokenSource source)
        {
            try { source.Cancel(); }
            catch (Exception) { }
        }

        private static void CleanupStartedProcess(Process startedProcess)
        {
            try { if (!startedProcess.HasExited) startedProcess.Kill(); } catch (Exception) { }
            try { startedProcess.WaitForExit(ShutdownTimeoutMilliseconds); } catch (Exception) { }
            TryDispose(startedProcess);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("CudaScorerClient");
            if (Volatile.Read(ref faulted) != 0) throw new InvalidOperationException("The CUDA scorer session is no longer usable.");
        }
    }
}
