using Mister.Core.Diagnostics;

namespace Mister.Core.Formats;

public interface IArchiveReader
{
    GameFamily Family { get; }

    ValueTask<ToolResult<ArchiveIndex>> IndexAsync(
        string archivePath,
        DetectionDepth depth,
        CancellationToken cancellationToken);
}
