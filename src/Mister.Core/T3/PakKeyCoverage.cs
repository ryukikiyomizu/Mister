using System.Collections.Frozen;

namespace Mister.Core.T3;

public sealed record PakKeyCoverage(
    string FamilyName,
    IReadOnlyDictionary<int, byte> Recovered,
    IReadOnlySet<int> ConsultedIndexes,
    IReadOnlySet<int> UnresolvedEffectiveIndexes,
    IReadOnlySet<int> UnconsultedIndexes,
    IReadOnlyDictionary<int, IReadOnlySet<byte>> Conflicts,
    IReadOnlyList<PakKeyEvidence> Evidence)
{
    public bool IsUsable =>
        Conflicts.Count == 0 && ConsultedIndexes.All(Recovered.ContainsKey);

    public bool HasCompleteEffectiveCoverage =>
        Enumerable.Range(0, T3Constants.EffectivePakKeySize)
            .All(Recovered.ContainsKey);

    public bool HasConflicts => Conflicts.Count > 0;

    public IReadOnlyList<PakKeyMismatch> CompareSuppliedKey(
        ReadOnlySpan<byte> suppliedKey)
    {
        if (suppliedKey.Length != T3Constants.PakKeySize)
        {
            throw new ArgumentException(
                $"The supplied T3 pakkey must be exactly {T3Constants.PakKeySize} bytes.",
                nameof(suppliedKey));
        }

        var mismatches = new List<PakKeyMismatch>();
        foreach ((int index, byte recovered) in Recovered.OrderBy(
            item => item.Key))
        {
            byte supplied = suppliedKey[index];
            if (supplied != recovered)
            {
                mismatches.Add(
                    new PakKeyMismatch(index, recovered, supplied));
            }
        }

        return mismatches;
    }
}

public sealed record PakKeyEvidence(
    string ArchivePath,
    string ArchiveSha256,
    int Ordinal,
    int KeyIndex,
    byte Candidate);

public sealed record PakKeyMismatch(
    int KeyIndex,
    byte Recovered,
    byte Supplied);

public static class PakKeyCoverageBuilder
{
    public static PakKeyCoverage FromEvidence(
        string family,
        IEnumerable<int> consulted,
        IReadOnlyDictionary<int, IReadOnlySet<byte>> candidates,
        IEnumerable<PakKeyEvidence>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(consulted);
        ArgumentNullException.ThrowIfNull(candidates);

        FrozenSet<int> consultedIndexes = consulted.ToFrozenSet();
        if (consultedIndexes.Any(index =>
            index < 0 || index >= T3Constants.EffectivePakKeySize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(consulted),
                $"Consulted T3 pakkey indexes must be between 0 and {T3Constants.EffectivePakKeySize - 1}.");
        }

        if (candidates.Keys.Any(index =>
            !consultedIndexes.Contains(index)))
        {
            throw new ArgumentException(
                "Candidate indexes must also be present in the consulted set.",
                nameof(candidates));
        }

        var recovered = new Dictionary<int, byte>();
        var conflicts = new Dictionary<int, IReadOnlySet<byte>>();
        foreach ((int index, IReadOnlySet<byte> values) in candidates)
        {
            ArgumentNullException.ThrowIfNull(values);
            byte[] distinct = values.Distinct().Order().ToArray();
            if (distinct.Length == 1)
            {
                recovered.Add(index, distinct[0]);
            }
            else if (distinct.Length > 1)
            {
                conflicts.Add(index, distinct.ToFrozenSet());
            }
        }

        FrozenSet<int> unresolved = Enumerable
            .Range(0, T3Constants.EffectivePakKeySize)
            .Where(index => !recovered.ContainsKey(index))
            .ToFrozenSet();
        FrozenSet<int> unconsulted = Enumerable
            .Range(
                T3Constants.EffectivePakKeySize,
                T3Constants.PakKeySize - T3Constants.EffectivePakKeySize)
            .ToFrozenSet();
        PakKeyEvidence[] evidenceItems = evidence?.ToArray() ?? [];

        return new PakKeyCoverage(
            family,
            recovered.ToFrozenDictionary(),
            consultedIndexes,
            unresolved,
            unconsulted,
            conflicts.ToFrozenDictionary(),
            Array.AsReadOnly(evidenceItems));
    }
}
