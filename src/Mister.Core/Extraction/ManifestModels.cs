using Mister.Core.Diagnostics;
using Mister.Core.Formats;

namespace Mister.Core.Extraction;

public sealed record ManifestSource(
    string Path,
    long Size,
    string Sha256);

public sealed record ManifestEntry(
    int Ordinal,
    string ArchivePath,
    string OutputPath,
    long StoredSize,
    long DecodedSize,
    string Sha256,
    PayloadSignature Signature,
    IReadOnlyList<ToolDiagnostic> Diagnostics,
    OutputPathCollision? Collision);

public sealed record ExtractionManifest(
    GameFamily Family,
    ManifestSource Source,
    IReadOnlyList<ManifestEntry> Entries,
    IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    public int SchemaVersion => 1;
}
