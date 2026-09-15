namespace Mister.Core.T3;

public sealed record T3PackFamily(
    string FamilyName,
    IReadOnlyList<string> ArchivePaths)
{
    public static T3PackFamily Create(IEnumerable<string> archivePaths)
    {
        ArgumentNullException.ThrowIfNull(archivePaths);
        string[] paths = archivePaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                return Path.GetFullPath(path);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException(
                "At least one T3 archive is required.",
                nameof(archivePaths));
        }

        string familyName = T3PakKeyResolver.BaseKeyName(paths[0]);
        if (paths.Any(path => !string.Equals(
            T3PakKeyResolver.BaseKeyName(path),
            familyName,
            StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "All T3 archives must belong to the same base or numbered-overlay family.",
                nameof(archivePaths));
        }

        return new T3PackFamily(
            familyName,
            Array.AsReadOnly(paths));
    }
}
