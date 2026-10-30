using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Geometry;
using DreamBooks.Core.Ink;
using DreamBooks.Core.Model;
using DreamBooks.Core.Undo;
using DreamBooks.Pdf;
using CorePage = DreamBooks.Core.Model.Page;

namespace DreamBooks.App.ViewModels;

/// <summary>Document, page and undo state of the prototype window.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IFileDialogService _dialogs;
    private PageEditor? _editor;
    private PdfRenderer? _renderer;
    private PageSize _pageSize;
    private int _pageNumber;
    private int _pageCount;
    private string? _errorMessage;
    private EraserSession? _activeErase;
    private bool _completingErase;

    public MainViewModel(IFileDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
        Tools = new ToolsViewModel();
        ZoomVm = new ZoomViewModel();
        Undo = new UndoManager();
        OpenRenderer = PdfRenderer.OpenAsync;

        OpenPdfCommand = new AsyncRelayCommand(OpenPdfAsync);
        GoToPageCommand = new AsyncRelayCommand<int>(GoToPageAsync);
        PreviousPageCommand = new AsyncRelayCommand(() => GoToPageAsync(PageNumber - 1));
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(PageNumber + 1));
        UndoCommand = new RelayCommand(() => Undo.Undo(), () => Undo.CanUndo);
        RedoCommand = new RelayCommand(() => Undo.Redo(), () => Undo.CanRedo);
        Undo.StateChanged += (_, _) =>
        {
            if (!_completingErase)
            {
                // Undo or redo while the eraser is down: the gesture's snapshot is stale, so drop it
                // rather than overwrite the page with it on EndErase.
                CancelErase();
            }

            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Raised after a different page (or document) became current.</summary>
    public event EventHandler? PageChanged;

    /// <summary>
    /// Raised when the strokes to draw change because of the eraser: a gesture started, its working copy
    /// changed, or it ended (then the view goes back to the page strokes).
    /// </summary>
    public event EventHandler? ErasePreviewChanged;

    public ToolsViewModel Tools { get; }

    public ZoomViewModel ZoomVm { get; }

    public UndoManager Undo { get; }

    public PageEditor? Editor
    {
        get => _editor;
        private set => SetProperty(ref _editor, value);
    }

    public PdfRenderer? Renderer
    {
        get => _renderer;
        private set => SetProperty(ref _renderer, value);
    }

    public PageSize PageSize
    {
        get => _pageSize;
        private set => SetProperty(ref _pageSize, value);
    }

    /// <summary>Current page, 1-based; 0 when no document is open.</summary>
    public int PageNumber
    {
        get => _pageNumber;
        private set => SetProperty(ref _pageNumber, value);
    }

    public int PageCount
    {
        get => _pageCount;
        private set => SetProperty(ref _pageCount, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>The eraser gesture in progress, or null; its <see cref="EraserSession.Strokes"/> is the live preview.</summary>
    public EraserSession? ActiveErase
    {
        get => _activeErase;
        private set => SetProperty(ref _activeErase, value);
    }

    public IAsyncRelayCommand OpenPdfCommand { get; }

    public IAsyncRelayCommand<int> GoToPageCommand { get; }

    public IAsyncRelayCommand PreviousPageCommand { get; }

    public IAsyncRelayCommand NextPageCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

    /// <summary>
    /// Starts an eraser gesture with the selected mode (FR-31). Zoom stays locked until it ends. No-op
    /// without a page or while a gesture is already live.
    /// </summary>
    public void BeginErase()
    {
        if (ActiveErase is not null || Editor is not { } editor)
        {
            return;
        }

        ActiveErase = editor.BeginErase(Tools.EraserMode);
        ZoomVm.IsLocked = true;
        ErasePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the eraser to a page point; the circle has the selected diameter (FR-32).</summary>
    public void EraseAt(PointD centerPt)
    {
        if (ActiveErase is { } session && session.MoveTo(centerPt, Units.MmToPt(Tools.EraserSizeMm) / 2))
        {
            ErasePreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ends the gesture: one undo step if anything was erased, none otherwise (FR-33, FR-63).</summary>
    public void EndErase()
    {
        if (ActiveErase is not { } session)
        {
            return;
        }

        // Complete first, then clear: the page already holds the working copy when the view switches back
        // to it, so nothing erased reappears for a frame.
        _completingErase = true;
        try
        {
            session.Complete();
        }
        finally
        {
            _completingErase = false;
            FinishErase();
        }
    }

    /// <summary>Drops the gesture without touching the page (lost capture, page change, undo mid-gesture).</summary>
    public void CancelErase()
    {
        if (ActiveErase is not { } session)
        {
            return;
        }

        session.Cancel();
        FinishErase();
    }

    private void FinishErase()
    {
        ActiveErase = null;
        ZoomVm.IsLocked = false;
        ErasePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Test seam: opens a renderer for a path.</summary>
    internal Func<string, CancellationToken, Task<PdfRenderer>> OpenRenderer { get; set; }

    private bool ConfirmDiscard() =>
        Editor is not { Page.Strokes.Count: > 0 } || _dialogs.Confirm(Strings.DiscardStrokesConfirm);

    private async Task OpenPdfAsync(CancellationToken ct)
    {
        var path = _dialogs.PickPdf();
        if (path is null || !ConfirmDiscard())
        {
            return;
        }

        PdfRenderer? renderer = null;
        try
        {
            renderer = await OpenRenderer(path, ct);
            if (renderer.PageCount == 0)
            {
                throw new PdfOpenException(PdfOpenError.InvalidFormat);
            }

            // Fails here, before anything is swapped, if the first page cannot be shown (NFR-11).
            _ = PreparePage(renderer, 1);
        }
        catch (OperationCanceledException)
        {
            await DisposeQuietlyAsync(renderer);
            throw;
        }
        catch (PdfOpenException ex)
        {
            await DisposeQuietlyAsync(renderer);
            ErrorMessage = MessageFor(ex.Error);
            return;
        }
        catch (Exception)
        {
            // Never include the exception text: it may carry file paths or document content (NFR-12).
            await DisposeQuietlyAsync(renderer);
            ErrorMessage = Strings.ErrorUnknown;
            return;
        }

        var previous = Renderer;
        Renderer = renderer;
        PageCount = renderer.PageCount;
        ErrorMessage = null;
        ShowPage(1);
        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
    }

    private static async Task DisposeQuietlyAsync(PdfRenderer? renderer)
    {
        if (renderer is null)
        {
            return;
        }

        try
        {
            await renderer.DisposeAsync();
        }
        catch (Exception)
        {
            // Best effort: the original failure is what the user needs to hear about.
        }
    }

    private Task GoToPageAsync(int pageNumber)
    {
        if (Renderer is null || pageNumber < 1 || pageNumber > PageCount || pageNumber == PageNumber)
        {
            return Task.CompletedTask;
        }

        if (ConfirmDiscard())
        {
            try
            {
                ShowPage(pageNumber);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException)
            {
                // ShowPage validates the page before touching any state, so the current page stays intact.
                ErrorMessage = Strings.ErrorUnknown;
            }
        }

        return Task.CompletedTask;
    }

    private static (PageSize Size, CorePage Page) PreparePage(PdfRenderer renderer, int pageNumber)
    {
        var size = renderer.GetPageSize(pageNumber - 1);
        return (size, new CorePage(pageNumber - 1, size.WidthPt, size.HeightPt));
    }

    private void ShowPage(int pageNumber)
    {
        var (size, page) = PreparePage(Renderer!, pageNumber);
        CancelErase();
        Undo.Clear();
        Editor = new PageEditor(page, Undo);
        PageSize = size;
        PageNumber = pageNumber;
        PageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string MessageFor(PdfOpenError error) => error switch
    {
        PdfOpenError.FileNotFound => Strings.ErrorFileNotFound,
        PdfOpenError.InvalidFormat => Strings.ErrorInvalidFormat,
        PdfOpenError.PasswordRequired => Strings.ErrorPassword,
        PdfOpenError.UnsupportedSecurity => Strings.ErrorUnsupported,
        _ => Strings.ErrorUnknown,
    };
}
