namespace DreamBooks.App.Resources;

/// <summary>Strongly typed access to Strings.resx (Italian UI text). Keep in sync with the resx.</summary>
internal static class Strings
{
    private static readonly System.Resources.ResourceManager Manager =
        new("DreamBooks.App.Resources.Strings", typeof(Strings).Assembly);

    public static string OpenPdf => Get(nameof(OpenPdf));

    public static string PdfFileFilter => Get(nameof(PdfFileFilter));

    public static string Page => Get(nameof(Page));

    public static string ToolPen => Get(nameof(ToolPen));

    public static string ToolEraser => Get(nameof(ToolEraser));

    public static string PressureCurve => Get(nameof(PressureCurve));

    public static string CurveLinear => Get(nameof(CurveLinear));

    public static string CurveSoft => Get(nameof(CurveSoft));

    public static string CurveFirm => Get(nameof(CurveFirm));

    public static string Smoothing => Get(nameof(Smoothing));

    public static string SmoothingOff => Get(nameof(SmoothingOff));

    public static string SmoothingLight => Get(nameof(SmoothingLight));

    public static string SmoothingStrong => Get(nameof(SmoothingStrong));

    public static string Width => Get(nameof(Width));

    public static string Color => Get(nameof(Color));

    public static string EraserMode => Get(nameof(EraserMode));

    public static string EraseWhole => Get(nameof(EraseWhole));

    public static string ErasePartial => Get(nameof(ErasePartial));

    public static string EraserSize => Get(nameof(EraserSize));

    public static string Undo => Get(nameof(Undo));

    public static string Redo => Get(nameof(Redo));

    public static string Zoom => Get(nameof(Zoom));

    public static string FitWidth => Get(nameof(FitWidth));

    public static string Panel => Get(nameof(Panel));

    public static string ErrorFileNotFound => Get(nameof(ErrorFileNotFound));

    public static string ErrorInvalidFormat => Get(nameof(ErrorInvalidFormat));

    public static string ErrorPassword => Get(nameof(ErrorPassword));

    public static string ErrorUnsupported => Get(nameof(ErrorUnsupported));

    public static string ErrorUnknown => Get(nameof(ErrorUnknown));

    public static string NoDocument => Get(nameof(NoDocument));

    public static string NoticeLabel => Get(nameof(NoticeLabel));

    public static string NoticeUnreadableAnnotations => Get(nameof(NoticeUnreadableAnnotations));

    public static string AppTitle => Get(nameof(AppTitle));

    public static string WindowTitleFormat => Get(nameof(WindowTitleFormat));

    public static string WindowTitleDirtyFormat => Get(nameof(WindowTitleDirtyFormat));

    public static string Save => Get(nameof(Save));

    public static string SaveAs => Get(nameof(SaveAs));

    public static string Saving => Get(nameof(Saving));

    public static string ErrorSaveEncrypted => Get(nameof(ErrorSaveEncrypted));

    public static string ErrorSaveVerification => Get(nameof(ErrorSaveVerification));

    public static string ErrorSaveFailed => Get(nameof(ErrorSaveFailed));

    public static string ErrorSaveReplaceFailed => Get(nameof(ErrorSaveReplaceFailed));

    public static string ErrorReopenAfterSave => Get(nameof(ErrorReopenAfterSave));

    public static string ErrorPageChangeFailed => Get(nameof(ErrorPageChangeFailed));

    public static string AskSaveChanges => Get(nameof(AskSaveChanges));

    public static string AnswerSave => Get(nameof(AnswerSave));

    public static string AnswerDontSave => Get(nameof(AnswerDontSave));

    public static string AnswerCancel => Get(nameof(AnswerCancel));

    public static string AskFileChanged => Get(nameof(AskFileChanged));

    public static string AnswerOverwriteAnyway => Get(nameof(AnswerOverwriteAnyway));

    public static string WarnSignatureInvalidation => Get(nameof(WarnSignatureInvalidation));

    public static string AnswerSaveAnyway => Get(nameof(AnswerSaveAnyway));

    public static string AnswerOk => Get(nameof(AnswerOk));

    public static string ErrorSaveTargetMissing => Get(nameof(ErrorSaveTargetMissing));

    public static string ErrorSavedStateNotUpdated => Get(nameof(ErrorSavedStateNotUpdated));

    public static string ErrorSaveFailedAfterRelease => Get(nameof(ErrorSaveFailedAfterRelease));

    public static string NoticeRestored => Get(nameof(NoticeRestored));

    public static string NoticeRestoredSkipped => Get(nameof(NoticeRestoredSkipped));

    public static string NoticeJournalDisabled => Get(nameof(NoticeJournalDisabled));

    public static string NoticeCopyKept => Get(nameof(NoticeCopyKept));

    public static string ErrorDocumentInUse => Get(nameof(ErrorDocumentInUse));

    public static string ErrorJournalFileMissing => Get(nameof(ErrorJournalFileMissing));

    public static string ErrorJournalUnreadable => Get(nameof(ErrorJournalUnreadable));

    public static string ErrorJournalDeleteFailed => Get(nameof(ErrorJournalDeleteFailed));

    public static string ErrorJournalRestoreFailed => Get(nameof(ErrorJournalRestoreFailed));

    public static string WarnJournalFault => Get(nameof(WarnJournalFault));

    public static string AskJournalMismatch => Get(nameof(AskJournalMismatch));

    public static string AnswerApplyAnyway => Get(nameof(AnswerApplyAnyway));

    public static string AnswerDiscard => Get(nameof(AnswerDiscard));

    public static string AskDiscardOrphanJournal => Get(nameof(AskDiscardOrphanJournal));

    public static string AnswerKeep => Get(nameof(AnswerKeep));

    public static string WarnJournalListenerFailed => Get(nameof(WarnJournalListenerFailed));

    public static string NoticeNotSavable => Get(nameof(NoticeNotSavable));

    public static string JournalWarningLabel => Get(nameof(JournalWarningLabel));

    /// <summary>Page indicator: page number, page count, zoom percent ("12 / 48 · 125%").</summary>
    public static string PageIndicatorFormat => Get(nameof(PageIndicatorFormat));

    /// <summary>Automation name of a page surface: the 1-based page number ("Pagina 12").</summary>
    public static string PageNameFormat => Get(nameof(PageNameFormat));

    public static string RailLabel => Get(nameof(RailLabel));

    public static string RailPreviewLabel => Get(nameof(RailPreviewLabel));

    /// <summary>Caption of the rail's page preview: the 1-based page number ("pag. 12").</summary>
    public static string RailPreviewPageFormat => Get(nameof(RailPreviewPageFormat));

    public static string PageIndicatorLabel => Get(nameof(PageIndicatorLabel));

    public static string GoToPage => Get(nameof(GoToPage));

    /// <summary>The page field opened from the rail's indicator (the panel's field is <see cref="GoToPage"/>).</summary>
    public static string GoToPageIndicatorField => Get(nameof(GoToPageIndicatorField));

    public static string GoToPageHint => Get(nameof(GoToPageHint));

    public static string Back => Get(nameof(Back));

    /// <summary>Page of a mark in the marks list: the 1-based page number ("pag. 12").</summary>
    public static string MarkPageLabelFormat => Get(nameof(MarkPageLabelFormat));

    /// <summary>Empty state of the marks list: what counts as a mark (FR-100, FR-103).</summary>
    public static string MarksEmpty => Get(nameof(MarksEmpty));

    /// <summary>Status shown for a while when previous/next mark has nowhere to go (FR-102).</summary>
    public static string MarksNoOther => Get(nameof(MarksNoOther));

    public static string PreviousMark => Get(nameof(PreviousMark));

    public static string NextMark => Get(nameof(NextMark));

    public static string GoToMark => Get(nameof(GoToMark));

    /// <summary>Title of the marks list panel.</summary>
    public static string MarksList => Get(nameof(MarksList));

    /// <summary>Close button of the left-edge panel (UI-04).</summary>
    public static string ClosePanel => Get(nameof(ClosePanel));

    /// <summary>Menu entry that opens the marks list (FR-103).</summary>
    public static string OpenMarksList => Get(nameof(OpenMarksList));

    /// <summary>Automation name of the marks list itself.</summary>
    public static string MarksListLabel => Get(nameof(MarksListLabel));

    /// <summary>Shown in the marks list while the background scan is still running.</summary>
    public static string MarksScanning => Get(nameof(MarksScanning));

    /// <summary>Automation name of the count in the marks panel's header: {0} the number of marks.</summary>
    public static string MarksCountNameFormat => Get(nameof(MarksCountNameFormat));

    /// <summary>A row's automation name: {0} page number, {1} side (no line: the fixed default "line" is not a printed line).</summary>
    public static string MarkRowNameFormat => Get(nameof(MarkRowNameFormat));

    public static string MarkSideLeft => Get(nameof(MarkSideLeft));

    public static string MarkSideRight => Get(nameof(MarkSideRight));

    /// <summary>Section label of the text search in the navigation panel (FR-13).</summary>
    public static string Search => Get(nameof(Search));

    /// <summary>Automation name of the search field.</summary>
    public static string SearchField => Get(nameof(SearchField));

    /// <summary>Tooltip and help text of the search field: the shortcuts.</summary>
    public static string SearchHint => Get(nameof(SearchHint));

    public static string PreviousResult => Get(nameof(PreviousResult));

    public static string NextResult => Get(nameof(NextResult));

    public static string ClearSearch => Get(nameof(ClearSearch));

    /// <summary>Automation name of the result count.</summary>
    public static string SearchCountLabel => Get(nameof(SearchCountLabel));

    /// <summary>The count while the scan runs and nothing was found yet.</summary>
    public static string SearchInProgress => Get(nameof(SearchInProgress));

    public static string SearchNoResults => Get(nameof(SearchNoResults));

    public static string SearchOneResult => Get(nameof(SearchOneResult));

    /// <summary>{0}: the number of results (with "+" when capped).</summary>
    public static string SearchResultsFormat => Get(nameof(SearchResultsFormat));

    /// <summary>{0}: the current result's place, {1}: the number of results ("3 di 17").</summary>
    public static string SearchPositionFormat => Get(nameof(SearchPositionFormat));

    /// <summary>{0}: the number of results found before the cap stopped the search ("5000+").</summary>
    public static string SearchTruncatedFormat => Get(nameof(SearchTruncatedFormat));

    /// <summary>Shown once a search found no page with text (a scanned PDF without a text layer).</summary>
    public static string SearchNoTextLayer => Get(nameof(SearchNoTextLayer));

    /// <summary>The count when no page could be searched at all (a damaged file).</summary>
    public static string SearchFailed => Get(nameof(SearchFailed));

    /// <summary>Status after next result wrapped from the last result to the first.</summary>
    public static string SearchWrappedToStart => Get(nameof(SearchWrappedToStart));

    /// <summary>Status after previous result wrapped from the first result to the last.</summary>
    public static string SearchWrappedToEnd => Get(nameof(SearchWrappedToEnd));

    /// <summary>Note beside the count after a search in which some pages could not be read: {0} the number of pages.</summary>
    public static string SearchFailedPagesFormat => Get(nameof(SearchFailedPagesFormat));

    /// <summary>The same note for a single page.</summary>
    public static string SearchFailedPageOne => Get(nameof(SearchFailedPageOne));

    /// <summary>The Pages tab of the left-edge panel (UI-04): its header.</summary>
    public static string PagesTab => Get(nameof(PagesTab));

    /// <summary>Automation name of the "Segni" tab.</summary>
    public static string MarksTabName => Get(nameof(MarksTabName));

    /// <summary>Automation name of the "Pagine" tab.</summary>
    public static string PagesTabName => Get(nameof(PagesTabName));

    /// <summary>Automation name of the page thumbnail list.</summary>
    public static string PagesListLabel => Get(nameof(PagesListLabel));

    /// <summary>Automation name and tooltip of the menu entry that opens the Pages tab.</summary>
    public static string OpenPagesList => Get(nameof(OpenPagesList));

    /// <summary>Caption of the menu entry that opens the Pages tab.</summary>
    public static string PagesListButton => Get(nameof(PagesListButton));

    /// <summary>A page row's automation name: {0} the page number.</summary>
    public static string PageRowNameFormat => Get(nameof(PageRowNameFormat));

    /// <summary>Added to the row name of the current page.</summary>
    public static string PageRowCurrent => Get(nameof(PageRowCurrent));

    /// <summary>Added to the row name of a page with marks.</summary>
    public static string PageRowHasMarks => Get(nameof(PageRowHasMarks));

    /// <summary>Added to the row name of a page with handwritten notes.</summary>
    public static string PageRowHasNotes => Get(nameof(PageRowHasNotes));

    private static string Get(string name) =>
        Manager.GetString(name, System.Globalization.CultureInfo.CurrentUICulture) ?? name;
}
