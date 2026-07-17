using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GTAEmblemMaker.Core
{
    internal sealed class CatalogSelection
    {
        internal FitCandidate Best { get; private set; }
        internal IReadOnlyList<FitCandidate> Candidates { get; private set; }
        internal IReadOnlyList<FitCandidate> BestByIdentity { get; private set; }
        internal double ServerMilliseconds { get; private set; }

        internal CatalogSelection(FitCandidate best, List<FitCandidate> candidates, List<FitCandidate> bestByIdentity, double serverMilliseconds)
        {
            Best = best;
            Candidates = new ReadOnlyCollection<FitCandidate>(candidates);
            BestByIdentity = new ReadOnlyCollection<FitCandidate>(bestByIdentity);
            ServerMilliseconds = serverMilliseconds;
        }
    }

    internal static class CatalogCandidateSearch
    {
        private const int GroupCount = 16;
        private const int Age = 100;
        private const int Fanout = 8;
        private const int EarlyStopRounds = 48;
        private const int MaxHillSteps = 5000;

        internal static async Task<CatalogSelection> SelectAsync(CudaScorerClient scorer, CatalogSearch config, int layer, int minAxis, CancellationToken cancellationToken)
        {
            if (scorer == null) throw new ArgumentNullException("scorer");
            if (config == null) throw new ArgumentNullException("config");
            var clock = Stopwatch.StartNew();
            var finalists = new List<FitCandidate>(config.Identities.Count * GroupCount);
            var bestByIdentity = new List<FitCandidate>(config.Identities.Count);
            for (var identityIndex = 0; identityIndex < config.Identities.Count; identityIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = config.Identities[identityIndex];
                var atlas = CatalogMaskAtlas.Find(identity);
                var seed = unchecked(CandidateGenerator.SeedForLayer(layer) + (uint)((identityIndex + 1) * 2000003));
                var chains = await SearchIdentityAsync(scorer, atlas, config.CandidatesPerGroup, minAxis, seed, layer, cancellationToken).ConfigureAwait(false);
                finalists.AddRange(chains);
                bestByIdentity.Add(chains.OrderBy(candidate => candidate.Energy).ThenBy(candidate => candidate.CandidateId).First());
            }
            clock.Stop();
            var best = finalists.OrderBy(candidate => candidate.Energy).ThenBy(candidate => candidate.PoolShapeFamily, StringComparer.Ordinal).ThenBy(candidate => candidate.CandidateId).First();
            return new CatalogSelection(best, finalists, bestByIdentity, clock.Elapsed.TotalMilliseconds);
        }

        private static async Task<List<FitCandidate>> SearchIdentityAsync(CudaScorerClient scorer, CatalogMaskAtlasEntry atlas, int candidatesPerGroup, int minAxis, uint seed, int layer, CancellationToken cancellationToken)
        {
            var random = new CatalogRandom(seed);
            var initial = new List<CudaCatalogCandidate>(checked(candidatesPerGroup * GroupCount));
            for (var group = 0; group < GroupCount; group++)
                for (var index = 0; index < candidatesPerGroup; index++)
                    initial.Add(new CudaCatalogCandidate
                    {
                        CandidateId = (uint)(initial.Count + 1),
                        Cx = random.Intn(512),
                        Cy = random.Intn(512),
                        Rx = random.Intn(29) + minAxis,
                        Ry = random.Intn(29) + minAxis,
                        Alpha = CandidateGenerator.InitialAlpha,
                        AngleDegrees = (float)(random.NextFloat() * 180)
                    });
            var initialScores = (await scorer.ScoreCatalogAsync(atlas, initial, true, cancellationToken).ConfigureAwait(false)).Scores;
            var chains = new List<CatalogChain>(GroupCount);
            for (var group = 0; group < GroupCount; group++)
            {
                var first = group * candidatesPerGroup;
                var best = first;
                for (var index = first + 1; index < first + candidatesPerGroup; index++) if (Better(initialScores[index], initialScores[best])) best = index;
                chains.Add(new CatalogChain(group, initial[best], initialScores[best], new CatalogRandom(unchecked((uint)(97531 + layer * 1009 + group * 9176)))));
            }

            var roundsWithoutAccept = 0;
            for (var round = 1; roundsWithoutAccept < EarlyStopRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proposals = new List<CudaCatalogCandidate>();
                var owners = new List<int>();
                for (var chainIndex = 0; chainIndex < chains.Count; chainIndex++)
                {
                    var chain = chains[chainIndex];
                    if (chain.RemainingAge <= 0 || chain.Steps >= MaxHillSteps) continue;
                    for (var fanout = 0; fanout < Fanout; fanout++)
                    {
                        proposals.Add(Mutate(chain, checked((uint)(round * 1000 + chain.Group * Fanout + fanout + 1)), minAxis));
                        owners.Add(chainIndex);
                    }
                    chain.Steps += Fanout;
                }
                if (proposals.Count == 0) break;
                var scores = (await scorer.ScoreCatalogAsync(atlas, proposals, true, cancellationToken).ConfigureAwait(false)).Scores;
                var accepted = 0;
                for (var chainIndex = 0; chainIndex < chains.Count; chainIndex++)
                {
                    var bestProposal = -1;
                    for (var index = 0; index < owners.Count; index++) if (owners[index] == chainIndex && (bestProposal < 0 || Better(scores[index], scores[bestProposal]))) bestProposal = index;
                    if (bestProposal >= 0 && Better(scores[bestProposal], chains[chainIndex].Score))
                    {
                        chains[chainIndex].Candidate = proposals[bestProposal];
                        chains[chainIndex].Score = scores[bestProposal];
                        chains[chainIndex].RemainingAge = Age;
                        accepted++;
                    }
                    else if (bestProposal >= 0)
                    {
                        chains[chainIndex].RemainingAge--;
                    }
                }
                roundsWithoutAccept = accepted == 0 ? roundsWithoutAccept + 1 : 0;
            }

            var result = new List<FitCandidate>(chains.Count);
            foreach (var chain in chains) result.Add(CandidateGenerator.FromCatalogResult(atlas.Identifier, (uint)chain.Group, chain.Candidate, chain.Score));
            return result;
        }

        private static CudaCatalogCandidate Mutate(CatalogChain chain, uint candidateId, int minAxis)
        {
            var source = chain.Candidate;
            var candidate = new CudaCatalogCandidate
            {
                CandidateId = candidateId,
                Cx = source.Cx,
                Cy = source.Cy,
                Rx = source.Rx,
                Ry = source.Ry,
                Alpha = source.Alpha,
                AngleDegrees = source.AngleDegrees
            };
            switch (chain.Random.Intn(4))
            {
                case 0:
                    candidate.Cx = Clamp((int)candidate.Cx + (int)(chain.Random.Normal() * 16), 0, 511);
                    candidate.Cy = Clamp((int)candidate.Cy + (int)(chain.Random.Normal() * 16), 0, 511);
                    break;
                case 1: candidate.Rx = Clamp((int)candidate.Rx + (int)(chain.Random.Normal() * 16), minAxis, 512); break;
                case 2: candidate.Ry = Clamp((int)candidate.Ry + (int)(chain.Random.Normal() * 16), minAxis, 512); break;
                default: candidate.AngleDegrees = (float)Wrap(candidate.AngleDegrees + (int)(chain.Random.Normal() * 15), 180); break;
            }
            candidate.Alpha = Clamp(candidate.Alpha + chain.Random.Intn(21) - 10, 1, 255);
            return candidate;
        }

        private static bool Better(CudaScore left, CudaScore right)
        {
            return left.Energy < right.Energy || left.Energy == right.Energy && left.CandidateId < right.CandidateId;
        }

        private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
        private static double Wrap(double value, double maximum) { value %= maximum; return value < 0 ? value + maximum : value; }

        private sealed class CatalogChain
        {
            internal int Group;
            internal CudaCatalogCandidate Candidate;
            internal CudaScore Score;
            internal CatalogRandom Random;
            internal int RemainingAge = Age;
            internal int Steps;

            internal CatalogChain(int group, CudaCatalogCandidate candidate, CudaScore score, CatalogRandom random)
            {
                Group = group;
                Candidate = candidate;
                Score = score;
                Random = random;
            }
        }

        private sealed class CatalogRandom
        {
            private uint state;
            private bool hasSpare;
            private double spare;

            internal CatalogRandom(uint seed) { state = seed; }
            internal double NextFloat() { state = unchecked(state * 1664525u + 1013904223u); return state / 4294967296.0; }
            internal int Intn(int maximum) { return (int)Math.Floor(NextFloat() * maximum); }
            internal double Normal()
            {
                if (hasSpare) { hasSpare = false; return spare; }
                var u = Math.Max(Double.Epsilon, NextFloat());
                var v = NextFloat();
                var radius = Math.Sqrt(-2 * Math.Log(u));
                spare = radius * Math.Sin(2 * Math.PI * v);
                hasSpare = true;
                return radius * Math.Cos(2 * Math.PI * v);
            }
        }
    }
}
