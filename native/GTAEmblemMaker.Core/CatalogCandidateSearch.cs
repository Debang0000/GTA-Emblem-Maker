using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        internal static async Task<CatalogSelection> SelectAsync(CudaScorerClient scorer, CatalogSearch config, int layer, int minAxis, CancellationToken cancellationToken)
        {
            if (scorer == null) throw new ArgumentNullException("scorer");
            if (config == null) throw new ArgumentNullException("config");
            var atlases = new List<CatalogMaskAtlasEntry>(config.Identities.Count);
            for (var index = 0; index < config.Identities.Count; index++) atlases.Add(CatalogMaskAtlas.Find(config.Identities[index]));
            var atlasMilliseconds = await scorer.SetCatalogAtlasesAsync(atlases, cancellationToken).ConfigureAwait(false);
            var result = await scorer.SelectResidentCatalogAsync(config.Identities.Count, config.CandidatesPerGroup, layer, minAxis, CandidateGenerator.SeedForLayer(layer), true, cancellationToken).ConfigureAwait(false);
            var finalists = new List<FitCandidate>(result.Chains.Count);
            for (var index = 0; index < result.Chains.Count; index++)
            {
                var chain = result.Chains[index];
                finalists.Add(CandidateGenerator.FromCatalogResult(config.Identities[chain.AtlasIndex], chain.GroupId, chain.Candidate, chain.Score));
            }
            var bestByIdentity = new List<FitCandidate>(config.Identities.Count);
            for (var identityIndex = 0; identityIndex < config.Identities.Count; identityIndex++)
            {
                var identity = config.Identities[identityIndex];
                bestByIdentity.Add(finalists.Where(candidate => candidate.Shape == identity).OrderBy(candidate => candidate.Energy).ThenBy(candidate => candidate.CandidateId).First());
            }
            var best = finalists.OrderBy(candidate => candidate.Energy).ThenBy(candidate => candidate.PoolShapeFamily, StringComparer.Ordinal).ThenBy(candidate => candidate.CandidateId).First();
            return new CatalogSelection(best, finalists, bestByIdentity, atlasMilliseconds + result.ServerMilliseconds);
        }

        internal static uint ProposalCandidateId(int round, int group, int fanoutIndex)
        {
            if (round < 1) throw new ArgumentOutOfRangeException("round");
            if (group < 0 || group >= 16) throw new ArgumentOutOfRangeException("group");
            if (fanoutIndex < 0 || fanoutIndex >= 8) throw new ArgumentOutOfRangeException("fanoutIndex");
            return checked((uint)((round * 1000 + group * 8 + fanoutIndex) * 1000 + group + 1));
        }
    }
}
