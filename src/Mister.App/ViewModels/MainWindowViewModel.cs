using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mister.App.Services;
using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Extraction;
using Mister.Core.Formats;
using Mister.Core.Preview;
using Mister.Core.T2;
using Mister.Core.T3;
using Mister.Windows.Capture;

namespace Mister.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IInputDiscoveryService inputDiscovery;
    private readonly IInspectorEvidenceService evidenceService;
    private readonly IArchiveCatalogService archiveCatalog;
    private readonly IInspectionWorkflowService workflow;
    private readonly Dispatcher? uiDispatcher;
    private readonly OperationQueue operationQueue = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim prerequisiteGate = new(1, 1);
    private int prerequisiteOperationRunning;
    private CancellationTokenSource? previewSelection;
    private ArchiveListItemViewModel? selectedArchive;
    private LogicalPackViewModel? selectedPack;
    private bool isPreviewLoading;
    private string safetyStatus = "Offline \u2022 Read-only";
    private string clientPath = "No client selected";
    private string clientHash = "Awaiting client";
    private string familyStatus = "Client family not identified";
    private string evidenceStatus = "Select a client and archive evidence.";
    private string pakKeyStatus = "No pakkey directory selected";
    private string packStatus = "No Pack directory scanned";
    private string outputDirectory = "Select a work directory for T2 capture";
    private string? selectedClientDirectory;
    private string? selectedClientPath;
    private string? pakKeyDirectory;
    private ClientEvidence? evidence;
    private EntryPreview? preview;
    private ImageSource? previewImage;
    private string? mediaPlaybackPath;
    private VcePlaybackViewModel? vcePlayback;
    private PakKeyCoverage? recoveryCoverage;
    private ToolDiagnostic? lastDiagnostic;
    private readonly string playbackRoot = Path.Combine(
        Path.GetTempPath(),
        "Mister",
        "Playback",
        Guid.NewGuid().ToString("N"));

    public MainWindowViewModel()
        : this(
            new InputDiscoveryService(),
            new InspectorEvidenceService(),
            new ArchiveCatalogService(),
            new InspectionWorkflowService())
    {
    }

    internal MainWindowViewModel(
        IInputDiscoveryService inputDiscovery,
        IInspectorEvidenceService evidenceService,
        IArchiveCatalogService archiveCatalog,
        IInspectionWorkflowService workflow)
    {
        this.inputDiscovery = inputDiscovery;
        this.evidenceService = evidenceService;
        this.archiveCatalog = archiveCatalog;
        this.workflow = workflow;
        uiDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        operationQueue.StateChanged += OnOperationStateChanged;
        CaptureT2Command = new AsyncCommand(
            token => RunPrerequisiteOperationAsync(CaptureT2CoreAsync, token),
            () => CanCaptureT2);
        RefreshEvidenceCommand = new AsyncCommand(
            token => RunPrerequisiteOperationAsync(RefreshEvidenceCoreAsync, token),
            () => CanRefreshEvidence);
        PrepareT3RecoveryCommand = new AsyncCommand(
            token => RunPrerequisiteOperationAsync(
                PrepareT3RecoveryCoreAsync,
                token),
            () => CanPrepareT3Recovery);
        RecoverPakKeyCommand = new AsyncCommand(
            token => RunPrerequisiteOperationAsync(RecoverPakKeyAsync, token),
            () => CanRecoverPakKey);
        PreviewCommand = new AsyncCommand(PreviewFromCommandAsync, () => CanPreview);
        CancelOperationCommand = new AsyncCommand(
            _ => operationQueue.CancelAsync(),
            () => Operation.IsRunning);
    }

    public ObservableCollection<ArchiveListItemViewModel> Archives { get; } = [];

    public ObservableCollection<LogicalPackViewModel> PackGroups { get; } = [];

    public ObservableCollection<FolderTreeItemViewModel> FolderTree { get; } = [];

    public ArchiveInspectorViewModel Inspector { get; private set; } =
        new(Array.Empty<ArchiveEntry>());

    public OperationQueueViewModel Operation { get; } = new();

    public AsyncCommand CaptureT2Command { get; }

    public AsyncCommand RefreshEvidenceCommand { get; }

    public AsyncCommand PrepareT3RecoveryCommand { get; }

    public AsyncCommand RecoverPakKeyCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand CancelOperationCommand { get; }

    public bool IsPrerequisiteOperationRunning => Volatile.Read(ref prerequisiteOperationRunning) != 0;

    public ArchiveListItemViewModel? SelectedArchive
    {
        get => selectedArchive;
        private set => SetProperty(ref selectedArchive, value);
    }

    public LogicalPackViewModel? SelectedPack
    {
        get => selectedPack;
        set
        {
            if (SetProperty(ref selectedPack, value))
            {
                RebuildPackWorkspace(value);
                RecoverPakKeyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SafetyStatus
    {
        get => safetyStatus;
        private set => SetProperty(ref safetyStatus, value);
    }

    public string ClientPath
    {
        get => clientPath;
        private set => SetProperty(ref clientPath, value);
    }

    public string ClientHash
    {
        get => clientHash;
        private set => SetProperty(ref clientHash, value);
    }

    public string FamilyStatus
    {
        get => familyStatus;
        private set => SetProperty(ref familyStatus, value);
    }

    public string EvidenceStatus
    {
        get => evidenceStatus;
        private set => SetProperty(ref evidenceStatus, value);
    }

    public string PakKeyStatus
    {
        get => pakKeyStatus;
        private set => SetProperty(ref pakKeyStatus, value);
    }

    public string PackStatus
    {
        get => packStatus;
        private set => SetProperty(ref packStatus, value);
    }

    public string OutputDirectory
    {
        get => outputDirectory;
        private set => SetProperty(ref outputDirectory, value);
    }

    public string? SelectedClientDirectory
    {
        get => selectedClientDirectory;
        private set => SetProperty(ref selectedClientDirectory, value);
    }

    public EntryPreview? Preview
    {
        get => preview;
        private set
        {
            if (!SetProperty(ref preview, value)) return;
            OnPropertyChanged(nameof(HasPreviewImage));
            OnPropertyChanged(nameof(HasPreviewText));
            OnPropertyChanged(nameof(HasPreviewMetadata));
            OnPropertyChanged(nameof(HasMediaPlayback));
            OnPropertyChanged(nameof(HasVcePlayback));
            OnPropertyChanged(nameof(PreviewStatus));
            OnPropertyChanged(nameof(PreviewKindLabel));
            OnPropertyChanged(nameof(PreviewMetadataSummary));
            OnPropertyChanged(nameof(HasPreviewContent));
            OnPropertyChanged(nameof(ShowPreviewEmpty));
        }
    }

    public ImageSource? PreviewImage
    {
        get => previewImage;
        private set
        {
            if (SetProperty(ref previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreviewImage));
                OnPropertyChanged(nameof(HasPreviewContent));
                OnPropertyChanged(nameof(ShowPreviewEmpty));
            }
        }
    }

    public string? MediaPlaybackPath
    {
        get => mediaPlaybackPath;
        private set
        {
            if (!SetProperty(ref mediaPlaybackPath, value)) return;
            OnPropertyChanged(nameof(HasMediaPlayback));
            OnPropertyChanged(nameof(HasPreviewContent));
            OnPropertyChanged(nameof(ShowPreviewEmpty));
        }
    }

    public VcePlaybackViewModel? VcePlayback
    {
        get => vcePlayback;
        private set
        {
            if (!SetProperty(ref vcePlayback, value)) return;
            OnPropertyChanged(nameof(HasVcePlayback));
            OnPropertyChanged(nameof(HasPreviewContent));
            OnPropertyChanged(nameof(ShowPreviewEmpty));
        }
    }

    public PakKeyCoverage? RecoveryCoverage
    {
        get => recoveryCoverage;
        private set => SetProperty(ref recoveryCoverage, value);
    }

    public ToolDiagnostic? LastDiagnostic
    {
        get => lastDiagnostic;
        private set
        {
            if (!SetProperty(ref lastDiagnostic, value)) return;
            OnPropertyChanged(nameof(HasPreviewError));
            OnPropertyChanged(nameof(PreviewStatus));
            OnPropertyChanged(nameof(ShowPreviewEmpty));
        }
    }

    public bool IsPreviewLoading
    {
        get => isPreviewLoading;
        private set
        {
            if (!SetProperty(ref isPreviewLoading, value)) return;
            OnPropertyChanged(nameof(PreviewStatus));
            OnPropertyChanged(nameof(ShowPreviewEmpty));
        }
    }

    public bool HasSelectedFile => Inspector.SelectedEntry is not null;
    public string SelectedFileName => Inspector.SelectedEntry?.FileName ?? "Choose a file";
    public string SelectedFilePath => Inspector.SelectedEntry?.DecodedPath
        ?? "Select a decoded file to preview it automatically.";
    public string SelectedFileSourceArchive => Inspector.SelectedEntry?.SourceArchiveName ?? "—";
    public string SelectedFileSummary => Inspector.SelectedEntry is null
        ? "No file selected"
        : $"{Inspector.SelectedEntry.Extension.TrimStart('.').ToUpperInvariant()} · {Inspector.SelectedEntry.SizeLabel}";
    public bool HasPreviewImage => PreviewImage is not null;
    public bool HasPreviewText => !string.IsNullOrWhiteSpace(Preview?.Text);
    public bool HasPreviewMetadata =>
        Preview?.Kind == PreviewKind.Metadata
        && Preview.Metadata.Count > 0;
    public bool HasMediaPlayback =>
        Preview?.Kind == PreviewKind.Media
        && !string.IsNullOrWhiteSpace(MediaPlaybackPath);
    public bool HasVcePlayback =>
        Preview?.Kind == PreviewKind.Animation
        && VcePlayback is not null;
    public bool HasPreviewError => LastDiagnostic is not null;
    public bool HasPreviewContent =>
        HasPreviewImage
        || HasPreviewText
        || HasPreviewMetadata
        || HasMediaPlayback
        || HasVcePlayback;
    public bool ShowPreviewEmpty => !IsPreviewLoading && !HasPreviewContent && !HasPreviewError;
    public string PreviewKindLabel => Preview?.Kind switch
    {
        PreviewKind.Image => "IMAGE",
        PreviewKind.Text => "TEXT",
        PreviewKind.Hex => "HEX",
        PreviewKind.Media => "MEDIA",
        PreviewKind.Animation => "VCE",
        PreviewKind.Metadata => "MEDIA",
        _ => "PREVIEW"
    };
    public string PreviewMetadataSummary => Preview is null
        ? string.Empty
        : string.Join(
            Environment.NewLine,
            Preview.Metadata.Select(static pair => $"{pair.Key}: {pair.Value}"));
    public string PreviewStatus => IsPreviewLoading
        ? "Decoding preview…"
        : HasPreviewError
            ? LastDiagnostic!.Message
            : Preview is null
                ? "Select a file to preview it automatically."
                : Preview.Kind switch
                {
                    PreviewKind.Image => "Image preview ready",
                    PreviewKind.Text => Preview.IsTruncated
                        ? "Text preview ready · truncated to the safe preview limit"
                        : "Text preview ready",
                    PreviewKind.Hex => Preview.IsTruncated
                        ? "Hex preview ready · first 4 KB shown"
                        : "Hex preview ready",
                    PreviewKind.Media => "Media player ready",
                    PreviewKind.Animation => VcePlayback is null
                        ? "Preparing VCE textures..."
                        : $"VCE player ready - {VcePlayback.Summary}",
                    PreviewKind.Metadata => "Safe media metadata preview",
                    _ => "No safe preview is available for this file type."
                };

    public bool IsBusy => IsPrerequisiteOperationRunning || Operation.IsRunning;
    public bool CanChangePrerequisites => !IsBusy;

    public bool CanExtract => !IsBusy && evidence is not null && Archives.Any(static item => item.IsValid);

    public bool ShowRecoverPakKey => evidence?.Family == GameFamily.Technika3;

    public bool CanRecoverPakKey => !IsBusy
        && ShowRecoverPakKey
        && SelectedPack is not null;

    public bool CanCaptureT2 => !IsBusy && selectedClientPath is not null && HasOutputDirectory && Archives.Any();

    public bool CanUseCaptureBundle => !IsBusy && selectedClientPath is not null && Archives.Any();

    private bool CanRefreshEvidence => !IsBusy && selectedClientPath is not null && Archives.Any();

    public bool CanPrepareT3Recovery =>
        !IsBusy
        && selectedClientPath is not null
        && Archives.Any();

    private bool CanPreview => !IsBusy
        && evidence is not null
        && Inspector.SelectedEntry is not null
        && (Inspector.SelectedEntry.SourceArchive is not null
            || SelectedArchive?.Index is not null);

    private bool HasOutputDirectory => !string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory);

    public async Task SelectClientAsync(string path, CancellationToken cancellationToken = default)
        => await RunPrerequisiteOperationAsync(token => SelectClientCoreAsync(path, token), cancellationToken);

    private async Task SelectClientCoreAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            EvidenceStatus = "The selected client file no longer exists. Pick the client executable again.";
            return;
        }

        selectedClientPath = fullPath;
        SelectedClientDirectory = Path.GetDirectoryName(fullPath);
        ClientPath = fullPath;
        ClientHash = "Hashing local client…";
        FamilyStatus = "Client selected; choose evidence to identify its family.";
        EvidenceStatus = "Discovering adjacent Pack archives.";
        evidence = null;
        ClearArchiveState();

        string[] packs = await Task.Run(
            () => inputDiscovery.DiscoverAdjacentPacks(fullPath).ToArray(),
            cancellationToken);
        AddArchives(packs);
        ClientHash = await FileHasher.Sha256Async(fullPath, cancellationToken);
        PackStatus = packs.Length == 0
            ? "No .pak files were found beside the client or in its Pack folder. Add PAK files or a PAK folder."
            : $"{packs.Length} Pack archive(s) discovered beside the client.";
        EvidenceStatus = packs.Length == 0
            ? "Add a PAK file or Pack folder, then select T2 capture or a T3 pakkey directory."
            : "Choose a T2 capture bundle or a T3 pakkey directory to validate archives.";
        UpdateCommandState();
    }

    public void AddArchives(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (Operation.IsRunning)
        {
            EvidenceStatus = "Cancel or finish extraction before changing archive inputs.";
            return;
        }
        var seen = new HashSet<string>(Archives.Select(static item => item.Path), StringComparer.OrdinalIgnoreCase);
        foreach (string source in paths)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) continue;
            string path = Path.GetFullPath(source);
            if (seen.Add(path)) Archives.Add(new ArchiveListItemViewModel(path));
        }

        RebuildPackGroups();
        PackStatus = Archives.Count == 0
            ? PackStatus
            : $"{PackGroups.Count} logical pack(s) from {Archives.Count} archive layer(s).";
        OnPropertyChanged(nameof(CanExtract));
        UpdateCommandState();
    }

    public async Task AddArchivesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        await RunPrerequisiteOperationAsync(_ =>
        {
            AddArchives(paths);
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task AddPakFolderAsync(string directory, CancellationToken cancellationToken = default)
        => await RunPrerequisiteOperationAsync(token => AddPakFolderCoreAsync(directory, token), cancellationToken);

    private async Task AddPakFolderCoreAsync(string directory, CancellationToken cancellationToken)
    {
        string[] packs = await Task.Run(
            () => inputDiscovery.DiscoverPaksInDirectory(directory).ToArray(),
            cancellationToken);
        AddArchives(packs);
        PackStatus = packs.Length == 0
            ? "That folder has no .pak files. Choose a folder containing archive files."
            : $"Added {packs.Length} archive(s) from the selected folder.";
    }

    public void SetOutputDirectory(string directory)
    {
        if (IsBusy)
        {
            EvidenceStatus = "Finish the active prerequisite operation before changing its work directory.";
            return;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            EvidenceStatus = "The selected work directory no longer exists. Choose another directory.";
            return;
        }

        OutputDirectory = fullPath;
        UpdateCommandState();
    }

    public void ReportInputError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        EvidenceStatus = message;
    }

    public async Task SetPakKeyDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        => await RunPrerequisiteOperationAsync(token => SetPakKeyDirectoryCoreAsync(directory, token), cancellationToken);

    private async Task SetPakKeyDirectoryCoreAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        pakKeyDirectory = Path.GetFullPath(directory);
        PakKeyStatus = Directory.Exists(pakKeyDirectory)
            ? $"Pakkey directory: {pakKeyDirectory}"
            : "The selected pakkey directory no longer exists.";
        await RefreshEvidenceCoreAsync(cancellationToken);
    }

    public async Task UseCaptureBundleAsync(string bundlePath, CancellationToken cancellationToken = default)
        => await RunPrerequisiteOperationAsync(token => UseCaptureBundleCoreAsync(bundlePath, token), cancellationToken);

    private async Task UseCaptureBundleCoreAsync(string bundlePath, CancellationToken cancellationToken)
    {
        if (selectedClientPath is null || Archives.Count == 0)
        {
            EvidenceStatus = "Select a client and at least one validation PAK before loading a capture bundle.";
            return;
        }

        string pak = Archives[0].Path;
        EvidenceStatus = "Verifying the local T2 capture bundle against the selected client and PAK…";
        string clientSha = await FileHasher.Sha256Async(selectedClientPath, cancellationToken);
        string pakSha = await FileHasher.Sha256Async(pak, cancellationToken);
        ToolResult<ClientEvidence> result = await T2CaptureBundleFileStore.LoadVerifiedAsync(
            bundlePath,
            clientSha,
            pakSha,
            pak,
            cancellationToken);
        if (!result.IsSuccess)
        {
            EvidenceStatus = result.Error!.Message;
            return;
        }

        await ApplyEvidenceAsync(result.Value!, cancellationToken);
        EvidenceStatus = "Verified T2 capture bundle is active. Preview and extraction stay bundle-backed.";
    }

    private async Task CaptureT2CoreAsync(CancellationToken cancellationToken)
    {
        if (selectedClientPath is null || !HasOutputDirectory || Archives.Count == 0)
        {
            EvidenceStatus = "Select a client, validation PAK, and work directory before capturing T2 tables.";
            return;
        }

        string parent = Path.Combine(OutputDirectory, ".technika-tools-capture");
        string stage = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            Process? process = FindRunningClient(selectedClientPath);
            if (process is null)
            {
                EvidenceStatus = "The selected T2 client is not running. Start that exact client, then capture once.";
                return;
            }

            using (process)
            {
                EvidenceStatus = "Reading T2 tables from the running client; no process writes are performed.";
                ToolResult<T2CaptureBundle> capture = await new T2RunningClientCaptureProvider().CaptureAsync(
                    process.Id,
                    selectedClientPath,
                    Archives[0].Path,
                    cancellationToken);
                if (!capture.IsSuccess)
                {
                    EvidenceStatus = capture.Error!.Message;
                    return;
                }

                string bundlePath = Path.Combine(
                    OutputDirectory,
                    $"{Path.GetFileNameWithoutExtension(selectedClientPath)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.ttcapture");
                await T2CaptureBundleFileStore.SaveAsync(bundlePath, capture.Value!, cancellationToken);
                await UseCaptureBundleCoreAsync(bundlePath, cancellationToken);
            }
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
    }

    private async Task RefreshEvidenceCoreAsync(CancellationToken cancellationToken)
    {
        if (selectedClientPath is null || Archives.Count == 0) return;
        if (pakKeyDirectory is null || !Directory.Exists(pakKeyDirectory))
        {
            EvidenceStatus = "T3 validation needs a pakkey directory, or load a T2 capture bundle.";
            return;
        }

        EvidenceStatus = "Loading T3 client table and validating local archive evidence…";
        ToolResult<ClientEvidence> result = await evidenceService.LoadT3Async(selectedClientPath, cancellationToken);
        if (!result.IsSuccess)
        {
            EvidenceStatus = result.Error!.Message;
            return;
        }

        await ApplyEvidenceAsync(result.Value!, cancellationToken);
    }

    private async Task ApplyEvidenceAsync(ClientEvidence loadedEvidence, CancellationToken cancellationToken)
    {
        evidence = loadedEvidence;
        RecoveryCoverage = null;
        Preview = null;
        PreviewImage = null;
        ResetPlayback();
        LastDiagnostic = null;
        FamilyStatus = loadedEvidence.Family == GameFamily.Technika2 ? "Technika 2 capture evidence" : "Technika 3 client-table evidence";
        string[] archivePaths = Archives.Select(static archive => archive.Path).ToArray();
        await foreach (CatalogItem item in archiveCatalog.ScanAsync(loadedEvidence, pakKeyDirectory, archivePaths, cancellationToken))
        {
            ArchiveListItemViewModel archive = Archives[item.SelectionOrdinal];
            archive.ApplyDetection(item.Detection);
        }

        RebuildPackWorkspace(SelectedPack);

        PakKeyStatus = loadedEvidence.Family == GameFamily.Technika2
            ? "Capture bundle supplies T2 tables"
            : $"T3 pakkey directory checked: {pakKeyDirectory}";
        EvidenceStatus = Archives.Any(static archive => archive.IsValid)
            ? "Evidence validated. Select an archive to inspect its folder tree."
            : "Evidence did not validate a selected archive. Check the client, PAK family, and pakkeys.";
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanRecoverPakKey));
        OnPropertyChanged(nameof(ShowRecoverPakKey));
        UpdateCommandState();
    }

    private async Task PreviewSelectedAsync(CancellationToken cancellationToken)
    {
        ArchiveTreeNodeViewModel? selectedEntry = Inspector.SelectedEntry;
        ArchiveIndex? archive =
            selectedEntry?.SourceArchive
            ?? SelectedArchive?.Index;
        ArchiveEntry? entry = selectedEntry?.Entry;
        if (evidence is null || archive is null || entry is null) return;
        IsPreviewLoading = true;
        LastDiagnostic = null;
        Preview = null;
        PreviewImage = null;
        ResetPlayback();
        try
        {
            EntryPreview result = await workflow.PreviewAsync(
                archive,
                entry,
                evidence,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Preview = result;
            Inspector.ApplyPreview(result);
            if (result.Kind == PreviewKind.Media)
            {
                ToolResult<PlaybackFile> playback = await workflow
                    .MaterializePlaybackAsync(
                        archive,
                        entry,
                        evidence,
                        playbackRoot,
                        cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!playback.IsSuccess)
                {
                    SurfaceDiagnostic(playback.Error!);
                    return;
                }
                MediaPlaybackPath = playback.Value!.Path;
            }
            else if (result.Kind == PreviewKind.Animation
                     && result.Animation is not null)
            {
                VcePlayback = await BuildVcePlaybackAsync(
                    result.Animation,
                    entry.DecodedPath,
                    cancellationToken);
            }
            try
            {
                PreviewImage = CreatePreviewImage(result.ImageBytes);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                SurfaceDiagnostic(new ToolDiagnostic(
                    DiagnosticCode.SignatureMismatch,
                    $"The validated image bytes could not be rendered: {exception.Message}",
                    entry.DecodedPath));
                return;
            }
            LastDiagnostic = result.Diagnostic;
            EvidenceStatus = LastDiagnostic is null
                ? $"Previewed {entry.DecodedPath} from {Path.GetFileName(archive.ArchivePath)}."
                : $"{LastDiagnostic.Code}: {LastDiagnostic.Message} [{LastDiagnostic.Subject}]";
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    private async Task PreviewFromCommandAsync(CancellationToken cancellationToken)
    {
        previewSelection?.Cancel();
        previewSelection?.Dispose();
        previewSelection = null;
        await PreviewSelectedAsync(cancellationToken);
    }

    private async Task<VcePlaybackViewModel> BuildVcePlaybackAsync(
        VceAnimation animation,
        string animationPath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveEntrySource> entries =
            SelectedPack?.EffectiveEntries
            ?? Array.Empty<ArchiveEntrySource>();
        var sourceImages = new Dictionary<string, BitmapSource?>(
            StringComparer.OrdinalIgnoreCase);
        var layers = new List<VcePlaybackLayerViewModel>(
            animation.Layers.Count);
        int missingTextures = 0;
        foreach (VceLayer layer in animation.Layers)
        {
            var textures = new List<VcePlaybackTextureViewModel>(
                layer.Textures.Count);
            foreach (VceTexture texture in layer.Textures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string cacheKey = NormalizeTextureName(texture.Name);
                if (!sourceImages.TryGetValue(
                        cacheKey,
                        out BitmapSource? sourceImage))
                {
                    ArchiveEntrySource? source = FindVceTexture(
                        entries,
                        animationPath,
                        texture.Name);
                    if (source is not null && evidence is not null)
                    {
                        EntryPreview texturePreview = await workflow
                            .PreviewAsync(
                                source.SourceArchive,
                                source.Entry,
                                evidence,
                                cancellationToken);
                        try
                        {
                            sourceImage = CreatePreviewImage(
                                texturePreview.ImageBytes);
                        }
                        catch (Exception exception) when (
                            exception is ArgumentException
                                or InvalidOperationException
                                or NotSupportedException)
                        {
                            sourceImage = null;
                        }
                    }
                    sourceImages[cacheKey] = sourceImage;
                }

                BitmapSource? cropped = CropVceTexture(
                    sourceImage,
                    texture);
                if (cropped is null) missingTextures++;
                textures.Add(new VcePlaybackTextureViewModel(
                    texture,
                    cropped));
            }
            layers.Add(new VcePlaybackLayerViewModel(layer, textures));
        }

        return new VcePlaybackViewModel(
            animation,
            layers,
            missingTextures);
    }

    private static ArchiveEntrySource? FindVceTexture(
        IReadOnlyList<ArchiveEntrySource> entries,
        string animationPath,
        string textureName)
    {
        string normalizedTexture = NormalizeTextureName(textureName);
        if (string.IsNullOrWhiteSpace(normalizedTexture)) return null;
        string normalizedAnimation = NormalizeTextureName(animationPath);
        string directory =
            Path.GetDirectoryName(normalizedAnimation)
            ?? string.Empty;
        string besideAnimation = NormalizeTextureName(
            Path.Combine(directory, normalizedTexture));
        ArchiveEntrySource? exact = entries.FirstOrDefault(source =>
            string.Equals(
                NormalizeTextureName(source.Entry.DecodedPath),
                besideAnimation,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        string fileName = Path.GetFileName(normalizedTexture);
        exact = entries.FirstOrDefault(source =>
            string.Equals(
                Path.GetFileName(source.Entry.DecodedPath),
                fileName,
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        return entries.FirstOrDefault(source =>
            IsVceTextureExtension(
                Path.GetExtension(source.Entry.DecodedPath))
            && string.Equals(
                Path.GetFileNameWithoutExtension(
                    source.Entry.DecodedPath),
                stem,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTextureName(string path) =>
        path.Replace('/', '\\').TrimStart('\\');

    private static bool IsVceTextureExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static BitmapSource? CropVceTexture(
        BitmapSource? source,
        VceTexture texture)
    {
        if (source is null) return null;
        if (texture.CropWidth <= 0 || texture.CropHeight <= 0)
        {
            return source;
        }

        int x = Math.Clamp(texture.CropX, 0, source.PixelWidth);
        int y = Math.Clamp(texture.CropY, 0, source.PixelHeight);
        int width = Math.Min(
            texture.CropWidth,
            source.PixelWidth - x);
        int height = Math.Min(
            texture.CropHeight,
            source.PixelHeight - y);
        if (width <= 0 || height <= 0) return source;
        var cropped = new CroppedBitmap(
            source,
            new System.Windows.Int32Rect(x, y, width, height));
        cropped.Freeze();
        return cropped;
    }

    private void ResetPlayback()
    {
        string? previousPath = MediaPlaybackPath;
        MediaPlaybackPath = null;
        VcePlayback = null;
        if (!string.IsNullOrWhiteSpace(previousPath)
            && IsPlaybackFile(previousPath))
        {
            PlaybackMaterializer.TryDelete(previousPath);
        }
    }

    private bool IsPlaybackFile(string path)
    {
        string relative = Path.GetRelativePath(playbackRoot, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private void TryDeletePlaybackRoot()
    {
        try
        {
            if (Directory.Exists(playbackRoot))
            {
                Directory.Delete(playbackRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task PrepareT3RecoveryCoreAsync(
        CancellationToken cancellationToken)
    {
        if (selectedClientPath is null || Archives.Count == 0) return;
        EvidenceStatus =
            "Reading T3 client-table evidence from the selected Client_D.exe…";
        ToolResult<ClientEvidence> result = await evidenceService.LoadT3Async(
            selectedClientPath,
            cancellationToken);
        if (!result.IsSuccess)
        {
            SurfaceDiagnostic(result.Error!);
            return;
        }

        evidence = result.Value!;
        RecoveryCoverage = null;
        FamilyStatus = "Technika 3 client-table evidence";
        PakKeyStatus =
            "Client_D evidence loaded; no existing pakkey is required for recovery.";
        EvidenceStatus =
            "Select a logical pack, then recover its pakkey from the client and archive structure.";
        OnPropertyChanged(nameof(ShowRecoverPakKey));
        OnPropertyChanged(nameof(CanRecoverPakKey));
        UpdateCommandState();
    }

    private async Task RecoverPakKeyAsync(CancellationToken cancellationToken)
    {
        if (evidence?.Family != GameFamily.Technika3) return;
        RecoveryCoverage = null;
        ToolResult<PakKeyCoverage> result = await workflow.RecoverPakKeyAsync(
            SelectedPack!.Members
                .Select(static archive => archive.Path)
                .ToArray(),
            evidence,
            cancellationToken);
        if (!result.IsSuccess)
        {
            SurfaceDiagnostic(result.Error!);
            return;
        }

        RecoveryCoverage = result.Value!;
        PakKeyStatus = RecoveryCoverage.HasCompleteEffectiveCoverage
            ? "Recovery has complete effective coverage (125/125). Review before export."
            : $"Recovery is usable: {RecoveryCoverage.IsUsable}; recovered {RecoveryCoverage.Recovered.Count}/125 effective positions. Review before export.";
    }

    public async Task ExportRecoveredPakKeyAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (evidence?.Family != GameFamily.Technika3 || RecoveryCoverage is null) return;
        ToolResult<PakKeyExport> result = await workflow.ExportPakKeyAsync(
            RecoveryCoverage, evidence, outputDirectory, cancellationToken);
        if (!result.IsSuccess)
        {
            SurfaceDiagnostic(result.Error!);
            return;
        }

        PakKeyStatus = $"Recovered pakkey exported to {result.Value!.PakKeyPath}";
    }

    public async Task RecoverAllT3PakKeysAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        await RunPrerequisiteOperationAsync(
            token => RecoverAllT3PakKeysCoreAsync(outputDirectory, token),
            cancellationToken);
    }

    private async Task RecoverAllT3PakKeysCoreAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (evidence?.Family != GameFamily.Technika3)
        {
            await PrepareT3RecoveryCoreAsync(cancellationToken);
        }

        if (evidence?.Family != GameFamily.Technika3
            || PackGroups.Count == 0)
        {
            return;
        }

        string outputRoot = Path.GetFullPath(outputDirectory);
        int recovered = 0;
        int failed = 0;
        ToolDiagnostic? firstFailure = null;
        foreach (LogicalPackViewModel pack in PackGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PakKeyStatus =
                $"Recovering {pack.Name} · {recovered + failed + 1}/{PackGroups.Count}";
            ToolResult<PakKeyCoverage> recovery =
                await workflow.RecoverPakKeyAsync(
                    pack.Members
                        .Select(static archive => archive.Path)
                        .ToArray(),
                    evidence,
                    cancellationToken);
            if (!recovery.IsSuccess)
            {
                firstFailure ??= recovery.Error;
                failed++;
                continue;
            }

            ToolResult<PakKeyExport> export =
                await workflow.ExportPakKeyAsync(
                    recovery.Value!,
                    evidence,
                    outputRoot,
                    cancellationToken);
            if (!export.IsSuccess)
            {
                firstFailure ??= export.Error;
                failed++;
                continue;
            }

            RecoveryCoverage = recovery.Value;
            recovered++;
        }

        if (recovered > 0)
        {
            pakKeyDirectory = outputRoot;
            await ApplyEvidenceAsync(evidence, cancellationToken);
        }

        LastDiagnostic = firstFailure;
        PakKeyStatus =
            $"Client_D recovery exported {recovered}/{PackGroups.Count} pakkeys"
            + (failed == 0 ? "." : $"; {failed} failed.");
        EvidenceStatus = recovered > 0
            ? $"Recovered pakkeys are available in {outputRoot}"
            : firstFailure?.Message
              ?? "No pakkeys were recovered.";
    }

    public async Task ExtractAsync(
        string outputDirectory,
        bool buildMergedView,
        IReadOnlyCollection<string>? selectedPackSeries = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanExtract || evidence is null) return;
        HashSet<string>? selected = selectedPackSeries is null
            ? null
            : new HashSet<string>(
                selectedPackSeries,
                StringComparer.OrdinalIgnoreCase);
        ArchiveIndex[] indexes = PackGroups
            .Where(pack =>
                pack.IsValid
                && (selected is null || selected.Contains(pack.SeriesName)))
            .SelectMany(static pack => pack.Members)
            .Where(static archive => archive.IsValid)
            .Select(static archive => archive.Index!)
            .DistinctBy(
                static index => index.ArchivePath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (indexes.Length == 0) return;
        var started = Stopwatch.StartNew();
        var progress = new InlineProgress<ExtractionProgress>(
            value => RunOnUiThread(() =>
                Operation.Apply(
                    value,
                    indexes.FirstOrDefault(index => string.Equals(
                        Path.GetFileName(index.ArchivePath),
                        value.ArchiveName,
                        StringComparison.OrdinalIgnoreCase))
                        ?.Entries.FirstOrDefault(entry => entry.Ordinal == value.EntryOrdinal)
                        ?.DecodedPath ?? "—",
                    started.Elapsed)));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, shutdown.Token);
        try
        {
            await operationQueue.StartAsync(async token =>
            {
                using CancellationTokenSource operationToken =
                    CancellationTokenSource.CreateLinkedTokenSource(token, linked.Token);
                ExtractionSummary summary = await workflow.ExtractAsync(
                    indexes,
                    evidence,
                    outputDirectory,
                    buildMergedView,
                    progress,
                    operationToken.Token);
                LastDiagnostic = summary.Diagnostics.FirstOrDefault();
                if (LastDiagnostic is not null) SurfaceDiagnostic(LastDiagnostic);
                else EvidenceStatus = summary.Status == ExtractionStatus.Cancelled
                    ? "Extraction cancelled after cleanup; unpublished .part files were removed."
                    : $"Extraction {summary.Status}: {summary.ExtractedEntries} verified, {summary.FailedEntries} failed.";
            });
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException)
        {
            SurfaceDiagnostic(new ToolDiagnostic(
                exception is ArgumentException
                    ? DiagnosticCode.UnsafeOutputPath
                    : DiagnosticCode.OutputPermissionFailure,
                $"Extraction could not start safely: {exception.Message}",
                outputDirectory));
        }
    }

    public async Task ShutdownAsync()
    {
        previewSelection?.Cancel();
        shutdown.Cancel();
        await operationQueue.CancelAsync();
        await prerequisiteGate.WaitAsync();
        prerequisiteGate.Release();
        TryDeletePlaybackRoot();
    }

    private async Task RunPrerequisiteOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (Operation.IsRunning || !await prerequisiteGate.WaitAsync(0, cancellationToken)) return;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, shutdown.Token);
        Volatile.Write(ref prerequisiteOperationRunning, 1);
        OnPropertyChanged(nameof(IsPrerequisiteOperationRunning));
        UpdateCommandState();
        try
        {
            await operation(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref prerequisiteOperationRunning, 0);
            prerequisiteGate.Release();
            OnPropertyChanged(nameof(IsPrerequisiteOperationRunning));
            UpdateCommandState();
        }
    }

    private void ClearArchiveState()
    {
        previewSelection?.Cancel();
        evidence = null;
        RecoveryCoverage = null;
        Preview = null;
        PreviewImage = null;
        ResetPlayback();
        LastDiagnostic = null;
        SelectedPack = null;
        SelectedArchive = null;
        Archives.Clear();
        PackGroups.Clear();
        FolderTree.Clear();
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanRecoverPakKey));
        OnPropertyChanged(nameof(ShowRecoverPakKey));
    }

    private void RebuildPackGroups()
    {
        string? previousSeries = SelectedPack?.SeriesName;
        PackGroups.Clear();
        foreach (IGrouping<string, ArchiveListItemViewModel> group in Archives
                     .GroupBy(
                         static archive => archive.SeriesName,
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(
                         static group => group.Key,
                         StringComparer.OrdinalIgnoreCase))
        {
            PackGroups.Add(new LogicalPackViewModel(group.Key, group));
        }

        SelectedPack = PackGroups.FirstOrDefault(pack =>
                           string.Equals(
                               pack.SeriesName,
                               previousSeries,
                               StringComparison.OrdinalIgnoreCase))
                       ?? PackGroups.FirstOrDefault();
        OnPropertyChanged(nameof(PackGroups));
    }

    private void RebuildPackWorkspace(LogicalPackViewModel? pack)
    {
        previewSelection?.Cancel();
        Preview = null;
        PreviewImage = null;
        ResetPlayback();
        LastDiagnostic = null;
        FolderTree.Clear();
        SelectedArchive = pack?.PrimaryArchive;
        IReadOnlyList<ArchiveEntrySource> entries =
            pack?.EffectiveEntries
            ?? Array.Empty<ArchiveEntrySource>();
        Inspector = new ArchiveInspectorViewModel(entries);
        Inspector.PropertyChanged += OnInspectorPropertyChanged;
        OnPropertyChanged(nameof(Inspector));
        if (entries.Count == 0) return;
        foreach (IGrouping<string, ArchiveEntrySource> group in entries
                     .GroupBy(static source => FirstSegment(source.Entry.DecodedPath)))
        {
            FolderTree.Add(new FolderTreeItemViewModel(
                group.Key,
                group.Count(),
                group.Select(static source => source.Entry.DecodedPath).ToArray()));
        }
    }

    private void OnInspectorPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ArchiveInspectorViewModel.SelectedEntry)) return;
        PreviewCommand.RaiseCanExecuteChanged();
        Preview = null;
        PreviewImage = null;
        ResetPlayback();
        LastDiagnostic = null;
        NotifySelectedFileChanged();
        previewSelection?.Cancel();
        previewSelection?.Dispose();
        previewSelection = null;
        ArchiveTreeNodeViewModel? selected = Inspector.SelectedEntry;
        if (selected is null || !CanPreview) return;
        previewSelection = CancellationTokenSource.CreateLinkedTokenSource(
            shutdown.Token);
        _ = PreviewAfterSelectionAsync(selected, previewSelection.Token);
    }

    private void NotifySelectedFileChanged()
    {
        OnPropertyChanged(nameof(HasSelectedFile));
        OnPropertyChanged(nameof(SelectedFileName));
        OnPropertyChanged(nameof(SelectedFilePath));
        OnPropertyChanged(nameof(SelectedFileSourceArchive));
        OnPropertyChanged(nameof(SelectedFileSummary));
        OnPropertyChanged(nameof(ShowPreviewEmpty));
    }

    private async Task PreviewAfterSelectionAsync(
        ArchiveTreeNodeViewModel selected,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(120, cancellationToken);
            if (!ReferenceEquals(Inspector.SelectedEntry, selected)) return;
            await PreviewSelectedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SurfaceDiagnostic(new ToolDiagnostic(
                DiagnosticCode.PreviewFailure,
                exception.Message,
                selected.DecodedPath));
        }
    }

    private void UpdateCommandState()
    {
        CaptureT2Command.RaiseCanExecuteChanged();
        RefreshEvidenceCommand.RaiseCanExecuteChanged();
        PrepareT3RecoveryCommand.RaiseCanExecuteChanged();
        RecoverPakKeyCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        CancelOperationCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUseCaptureBundle));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanChangePrerequisites));
        OnPropertyChanged(nameof(CanPrepareT3Recovery));
    }

    private void OnOperationStateChanged(OperationState state)
    {
        RunOnUiThread(() =>
        {
            Operation.State = state;
            UpdateCommandState();
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (uiDispatcher is null || uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = uiDispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            action);
    }

    private void SurfaceDiagnostic(ToolDiagnostic diagnostic)
    {
        LastDiagnostic = diagnostic;
        EvidenceStatus = $"{diagnostic.Code}: {diagnostic.Message} [{diagnostic.Subject}]";
    }

    private static Process? FindRunningClient(string clientPath)
    {
        string fullPath = Path.GetFullPath(clientPath);
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullPath)))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(process.MainModule!.FileName!), fullPath, StringComparison.OrdinalIgnoreCase)) return process;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return null;
    }

    private static string FirstSegment(string path)
    {
        int separator = path.IndexOfAny(['/', '\\']);
        return separator < 0 ? path : path[..separator];
    }

    private static BitmapSource? CreatePreviewImage(byte[]? bytes)
    {
        if (bytes is null) return null;
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed class ArchiveListItemViewModel(string path) : ViewModelBase
{
    private DetectionResult? detection;

    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    public string SeriesName
    {
        get
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(Path);
            return HasNumberedPatchSuffix(stem)
                ? stem[..^2]
                : stem;
        }
    }

    public string SeriesPart
    {
        get
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(Path);
            return HasNumberedPatchSuffix(stem)
                ? $"UPDATE {stem[^2..]}"
                : "BASE";
        }
    }

    public string Status => detection is null ? "Awaiting evidence" : detection.Family == GameFamily.Unknown ? "Not validated" : detection.Index!.Health.ToString();

    public string Family => detection?.Family.ToString() ?? "Unknown";

    public ArchiveIndex? Index => detection?.Index;

    public bool IsValid => detection?.Index?.Health is ArchiveHealth.Valid or ArchiveHealth.Warning;

    public int FileCount => detection?.Index?.Entries.Count ?? 0;

    public long DecodedBytes => detection?.Index?.Entries.Sum(static item => item.DecodedSize) ?? 0;

    public string EofHealth => detection?.Index is null ? "Awaiting index" : detection.Index.FinalOffset == detection.Index.ArchiveSize ? "Exact EOF" : "EOF mismatch";

    public string KeyCoverage => detection?.Index is null
        ? "Awaiting evidence"
        : detection.Family == GameFamily.Technika3
            ? $"{detection.Index.Entries.Select(static entry => entry.KeyIndex).Distinct().Count()}/125 effective key positions exercised"
            : "T2 capture-bundle key evidence";

    public void ApplyDetection(DetectionResult value)
    {
        detection = value;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(DecodedBytes));
        OnPropertyChanged(nameof(EofHealth));
        OnPropertyChanged(nameof(KeyCoverage));
    }

    private static bool HasNumberedPatchSuffix(string stem) =>
        stem.Length > 2
        && stem[^2] == '0'
        && char.IsAsciiDigit(stem[^1]);
}

public sealed class LogicalPackViewModel : ViewModelBase
{
    public LogicalPackViewModel(
        string seriesName,
        IEnumerable<ArchiveListItemViewModel> members)
    {
        SeriesName = seriesName;
        Members = members
            .OrderBy(static member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static member => member.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (ArchiveListItemViewModel member in Members)
        {
            member.PropertyChanged += (_, _) => NotifyStateChanged();
        }
    }

    public string SeriesName { get; }
    public string Name => $"{SeriesName}.pak";
    public override string ToString() => Name;
    public IReadOnlyList<ArchiveListItemViewModel> Members { get; }
    public int LayerCount => Members.Count;
    public int UpdateCount => Math.Max(0, LayerCount - 1);
    public string LayerSummary => UpdateCount == 0
        ? "Single archive"
        : $"Base + {UpdateCount} update{(UpdateCount == 1 ? string.Empty : "s")}";
    public string MemberNames => string.Join(", ", Members.Select(static member => member.Name));
    public ArchiveListItemViewModel? PrimaryArchive =>
        Members.FirstOrDefault(static member => member.IsValid)
        ?? Members.FirstOrDefault();
    public bool IsValid => Members.Any(static member => member.IsValid);
    public int FileCount => EffectiveEntries.Count;
    public long DecodedBytes => EffectiveEntries.Aggregate(
        0L,
        static (total, source) =>
            source.Entry.DecodedSize <= 0
                ? total
                : total > long.MaxValue - source.Entry.DecodedSize
                    ? long.MaxValue
                    : total + source.Entry.DecodedSize);
    public string Family
    {
        get
        {
            string[] families = Members
                .Where(static member => member.IsValid)
                .Select(static member => member.Family)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return families.Length == 1 ? families[0] : "Unverified";
        }
    }
    public string Status => Members.All(static member => member.IsValid)
        ? "Ready"
        : Members.Any(static member => member.IsValid)
            ? "Partially ready"
            : "Awaiting evidence";

    public IReadOnlyList<ArchiveEntrySource> EffectiveEntries
    {
        get
        {
            var effective = new Dictionary<string, ArchiveEntrySource>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ArchiveListItemViewModel member in Members)
            {
                if (member.Index is not ArchiveIndex index) continue;
                foreach (ArchiveEntry entry in index.Entries)
                {
                    effective[entry.DecodedPath] = new ArchiveEntrySource(
                        index,
                        entry,
                        member.Name);
                }
            }

            return effective.Values
                .OrderBy(static source => source.Entry.DecodedPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static source => source.Entry.DecodedPath, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(PrimaryArchive));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(DecodedBytes));
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(EffectiveEntries));
    }
}

public sealed class FolderTreeItemViewModel(string name, int count, IReadOnlyList<string> entries)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public IReadOnlyList<string> Entries { get; } = entries;
}
