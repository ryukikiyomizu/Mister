namespace Mister.Core.Extraction;

public static class PackFamilyOrder
{
    public static int Compare(string leftArchive, string rightArchive) =>
        Compare(
            leftArchive,
            leftSelectionOrdinal: 0,
            rightArchive,
            rightSelectionOrdinal: 0);

    public static int Compare(
        string leftArchive,
        int leftSelectionOrdinal,
        string rightArchive,
        int rightSelectionOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightArchive);

        ArchiveFamily left = Split(leftArchive);
        ArchiveFamily right = Split(rightArchive);
        int comparison = StringComparer.OrdinalIgnoreCase.Compare(
            left.Family,
            right.Family);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.HasNumericSuffix.CompareTo(
            right.HasNumericSuffix);
        if (comparison != 0)
        {
            return comparison;
        }

        if (left.HasNumericSuffix)
        {
            comparison = CompareNaturalNumber(
                left.NumericSuffix,
                right.NumericSuffix);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftSelectionOrdinal.CompareTo(rightSelectionOrdinal);
    }

    public static int Compare(
        CompletedByPackManifestRow left,
        CompletedByPackManifestRow right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Compare(
            left.ArchiveName,
            left.ArchiveSelectionOrdinal,
            right.ArchiveName,
            right.ArchiveSelectionOrdinal);
    }

    private static ArchiveFamily Split(string archive)
    {
        string stem = Path.GetFileNameWithoutExtension(archive);
        int suffixStart = stem.Length;
        while (suffixStart > 0 && char.IsAsciiDigit(stem[suffixStart - 1]))
        {
            suffixStart--;
        }

        return suffixStart == stem.Length
            ? new ArchiveFamily(stem, string.Empty, HasNumericSuffix: false)
            : new ArchiveFamily(
                stem[..suffixStart],
                stem[suffixStart..],
                HasNumericSuffix: true);
    }

    private static int CompareNaturalNumber(string left, string right)
    {
        string normalizedLeft = TrimLeadingZeroes(left);
        string normalizedRight = TrimLeadingZeroes(right);
        int comparison = normalizedLeft.Length.CompareTo(
            normalizedRight.Length);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(
                normalizedLeft,
                normalizedRight);
    }

    private static string TrimLeadingZeroes(string value)
    {
        int firstNonZero = 0;
        while (firstNonZero < value.Length - 1
            && value[firstNonZero] == '0')
        {
            firstNonZero++;
        }

        return value[firstNonZero..];
    }

    private sealed record ArchiveFamily(
        string Family,
        string NumericSuffix,
        bool HasNumericSuffix);
}
