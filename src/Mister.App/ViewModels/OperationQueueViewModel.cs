namespace Mister.App.ViewModels;

public enum OperationState { Idle, Running, Cancelling, Failed }

public sealed class OperationQueueViewModel : ViewModelBase
{
    private OperationState state;
    private string archiveName = "No operation running";
    private int entryOrdinal = -1;
    private string entryPath = "—";
    private int entryCount;
    private long completedDecodedBytes;
    private long totalDecodedBytes;
    private int failureCount;
    private TimeSpan elapsed;

    public OperationState State
    {
        get => state;
        set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }

    public string ArchiveName { get => archiveName; private set => SetProperty(ref archiveName, value); }
    public int EntryOrdinal { get => entryOrdinal; private set => SetProperty(ref entryOrdinal, value); }
    public string EntryPath { get => entryPath; private set => SetProperty(ref entryPath, value); }
    public int EntryCount { get => entryCount; private set => SetProperty(ref entryCount, value); }
    public long CompletedDecodedBytes { get => completedDecodedBytes; private set => SetProperty(ref completedDecodedBytes, value); }
    public long TotalDecodedBytes { get => totalDecodedBytes; private set => SetProperty(ref totalDecodedBytes, value); }
    public int FailureCount { get => failureCount; private set => SetProperty(ref failureCount, value); }
    public TimeSpan Elapsed { get => elapsed; private set => SetProperty(ref elapsed, value); }
    public bool IsRunning => State is OperationState.Running or OperationState.Cancelling;

    public void Apply(
        Mister.Core.Extraction.ExtractionProgress progress,
        string currentEntryPath,
        TimeSpan operationElapsed)
    {
        ArchiveName = progress.ArchiveName;
        EntryOrdinal = progress.EntryOrdinal;
        EntryPath = currentEntryPath;
        EntryCount = progress.EntryCount;
        CompletedDecodedBytes = progress.CompletedDecodedBytes;
        TotalDecodedBytes = progress.TotalDecodedBytes;
        FailureCount = progress.FailureCount;
        Elapsed = operationElapsed;
    }
}
