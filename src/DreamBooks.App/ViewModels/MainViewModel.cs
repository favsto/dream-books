using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Ink;
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
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Raised after a different page (or document) became current.</summary>
    public event EventHandler? PageChanged;

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

    public IAsyncRelayCommand OpenPdfCommand { get; }

    public IAsyncRelayCommand<int> GoToPageCommand { get; }

    public IAsyncRelayCommand PreviousPageCommand { get; }

    public IAsyncRelayCommand NextPageCommand { get; }

    public IRelayCommand UndoCommand { get; }

    public IRelayCommand RedoCommand { get; }

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

        PdfRenderer renderer;
        try
        {
            renderer = await OpenRenderer(path, ct);
        }
        catch (PdfOpenException ex)
        {
            ErrorMessage = MessageFor(ex.Error);
            return;
        }

        if (renderer.PageCount == 0)
        {
            await renderer.DisposeAsync();
            ErrorMessage = MessageFor(PdfOpenError.InvalidFormat);
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

    private Task GoToPageAsync(int pageNumber)
    {
        if (Renderer is null || pageNumber < 1 || pageNumber > PageCount || pageNumber == PageNumber)
        {
            return Task.CompletedTask;
        }

        if (ConfirmDiscard())
        {
            ShowPage(pageNumber);
        }

        return Task.CompletedTask;
    }

    private void ShowPage(int pageNumber)
    {
        var size = Renderer!.GetPageSize(pageNumber - 1);
        Undo.Clear();
        Editor = new PageEditor(new CorePage(pageNumber - 1, size.WidthPt, size.HeightPt), Undo);
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
