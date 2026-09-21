using MahjongHater.Core.Policy;
using MahjongHater.Core.State;

namespace MahjongHater.Core.Precomputed;

// Optional corpus collection on the analysis worker. No hidden information or
// player names are recorded. Recording failures do not suppress a recommendation.
public sealed class SnapshotRecordingPolicy(IPolicy policy, string path, Action<Exception>? onError = null) : IPolicy
{
    private readonly object gate = new();
    private readonly HashSet<StateIdentity> recorded = [];
    private bool failed;

    public ActionChoice Choose(StateSnapshot state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (DiscardActionSpace.Supports(state))
        {
            var identity = IncrementalStateKey.Create(state);
            lock (this.gate)
            {
                ct.ThrowIfCancellationRequested();
                if (!this.failed && !this.recorded.Contains(identity))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                        File.AppendAllText(path, SnapshotJson.Serialize(state) + Environment.NewLine);
                        // Bound in-memory deduplication; the trainer also deduplicates.
                        if (this.recorded.Count >= 4096)
                            this.recorded.Clear();
                        this.recorded.Add(identity);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        this.failed = true;
                        onError?.Invoke(ex);
                    }
                }
            }
        }
        return policy.Choose(state, ct);
    }
}
