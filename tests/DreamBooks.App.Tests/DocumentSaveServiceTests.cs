using DreamBooks.App.Ink;
using DreamBooks.App.Resources;
using DreamBooks.App.Services;
using DreamBooks.Core.Documents;
using DreamBooks.Core.Model;
using DreamBooks.Pdf;

namespace DreamBooks.App.Tests;

/// <summary>The saving pipeline with fakes around it (FR-80, FR-89, NFR-10, NFR-12).</summary>
public sealed class DocumentSaveServiceTests : IDisposable
{
    private static readonly InkPoint[] _line =
    [
        new(50, 100, 0.5f, null, null, null, 0),
        new(150, 100, 0.5f, null, null, null, 10),
    ];

    private static readonly byte[] _original = [1, 2, 3, 4];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dreambooks-save-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeDocumentWriter _writer;
    private readonly FakeViewerHandle _viewer;
    private readonly string _path;

    public DocumentSaveServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _writer = new FakeDocumentWriter(_log);
        _viewer = new FakeViewerHandle(_log);
        _path = Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(_path, _original);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Save_writes_replaces_reopens_and_marks_the_session_clean()
    {
        using var session = await OpenDirtySessionAsync();
        var stroke = session.CurrentEditor!.Page.Strokes[0];

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Null(result.Message);
        var call = Assert.Single(_writer.Calls);
        Assert.Equal(_path, call.Source);
        Assert.Equal(_path, call.Destination);
        Assert.Equal([stroke], Assert.Single(call.Changes).Added);
        Assert.Equal(WithMarker(_original), await File.ReadAllBytesAsync(_path));
        Assert.Empty(TempFiles());
        Assert.Equal(_path, _viewer.OpenPath);
        Assert.False(session.IsDirty);
        Assert.Equal(_path, session.Path);
        Assert.Equal(await DocumentFingerprint.ComputeAsync(_path), session.OpenFingerprint);
        Assert.NotNull(result.Saved);
        Assert.False(result.Saved.IsSaveAs);
        Assert.Equal(_path, result.Saved.OldPath);
        Assert.Equal(_path, result.Saved.NewPath);
        Assert.Empty(_dialogs.Messages); // no popup on a successful save
    }

    [Fact]
    public async Task Original_is_untouched_when_the_writer_fails()
    {
        using var session = await OpenDirtySessionAsync();
        _writer.Failure = new IOException(@"disk full writing C:\Users\someone\secret.pdf");

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(Strings.ErrorSaveFailed, result.Message);
        Assert.Equal(_original, await File.ReadAllBytesAsync(_path));
        Assert.Empty(TempFiles());
        Assert.DoesNotContain("release", _log); // the viewer was never touched
        Assert.True(session.IsDirty);
        Assert.Null(result.Saved);
    }

    [Fact]
    public async Task Verification_failure_shows_the_verification_message()
    {
        using var session = await OpenDirtySessionAsync();
        _writer.Failure = new SaveVerificationException("strokes on page 1");

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(Strings.ErrorSaveVerification, result.Message);
        Assert.Equal(_original, await File.ReadAllBytesAsync(_path));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Cancelled_write_returns_Cancelled_quietly()
    {
        using var session = await OpenDirtySessionAsync();
        _writer.Failure = new OperationCanceledException();

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Message);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Replace_failure_reports_the_temp_location_and_keeps_the_session_dirty()
    {
        using var session = await OpenDirtySessionAsync();
        var service = Service((temp, target) =>
            throw new ReplaceFailedException(temp, target, new IOException(@"locked by C:\Other\private.pdf")));

        var result = await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(string.Format(Strings.ErrorSaveReplaceFailed, _dir), result.Message);
        Assert.DoesNotContain("private", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(_writer.LastTempPath)); // the verified copy is never deleted here
        Assert.Equal(_original, await File.ReadAllBytesAsync(_path));
        Assert.True(session.IsDirty);
        Assert.Equal(["write", "release", "replace", "reopen:" + _path], _log);
        Assert.False(_viewer.IsReleased);
    }

    [Fact]
    public async Task Replace_failure_without_a_temp_copy_does_not_point_to_one()
    {
        using var session = await OpenDirtySessionAsync();
        var service = Service((temp, target) =>
        {
            File.Delete(temp);
            throw new ReplaceFailedException(temp, target, new IOException("gone"));
        });

        var result = await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(Strings.ErrorSaveFailed, result.Message);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Failure_after_release_reopens_the_viewer_renderer()
    {
        using var session = await OpenDirtySessionAsync();
        var service = Service((_, _) => throw new UnauthorizedAccessException(@"C:\Users\someone\a.pdf"));

        var result = await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(Strings.ErrorSaveFailedAfterRelease, result.Message);
        Assert.Equal(["write", "release", "replace", "reopen:" + _path], _log);
        Assert.False(_viewer.IsReleased);
        Assert.Empty(TempFiles()); // the original is still there, so the stray copy is removed
        Assert.Equal(_original, await File.ReadAllBytesAsync(_path));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Reopen_failure_after_replace_does_not_report_a_failed_save()
    {
        using var session = await OpenDirtySessionAsync();
        _viewer.ReopenFailure = new IOException(@"C:\Users\someone\a.pdf is busy");

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(Strings.ErrorReopenAfterSave, result.Message);
        Assert.False(session.IsDirty);
        Assert.Equal(WithMarker(_original), await File.ReadAllBytesAsync(_path));
        Assert.NotNull(result.Saved);
    }

    [Fact]
    public async Task Encrypted_document_is_refused_with_the_italian_message_and_nothing_is_written()
    {
        using var session = await OpenDirtySessionAsync();
        _viewer.Info = new PdfDocumentInfo(IsEncrypted: true, HasRestrictions: false, SignatureCount: 1);

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);
        var resultAs = await Service().SaveAsync(session, _viewer, saveAs: true, CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal(Strings.ErrorSaveEncrypted, result.Message);
        Assert.Equal(SaveOutcome.Refused, resultAs.Outcome);
        Assert.Empty(_writer.Calls);
        Assert.Empty(_log);
        Assert.Equal(0, _dialogs.SignatureAsks);
        Assert.Empty(_dialogs.SuggestedNames);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Writer_refusal_is_reported_as_the_encrypted_message()
    {
        using var session = await OpenDirtySessionAsync();
        _writer.Failure = new PdfSaveRefusedException(new PdfDocumentInfo(false, true, 0));

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Refused, result.Outcome);
        Assert.Equal(Strings.ErrorSaveEncrypted, result.Message);
        Assert.Equal(_original, await File.ReadAllBytesAsync(_path));
    }

    [Fact]
    public async Task Signed_document_asks_confirmation_and_declining_cancels()
    {
        using var session = await OpenDirtySessionAsync();
        _viewer.Info = new PdfDocumentInfo(IsEncrypted: false, HasRestrictions: false, SignatureCount: 2);
        _dialogs.ConfirmSignature = false;

        var declined = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Cancelled, declined.Outcome);
        Assert.Null(declined.Message);
        Assert.Equal(1, _dialogs.SignatureAsks);
        Assert.Empty(_writer.Calls);
        Assert.True(session.IsDirty);

        _dialogs.ConfirmSignature = true;
        var accepted = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, accepted.Outcome);
        Assert.Equal(2, _dialogs.SignatureAsks);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task Changed_fingerprint_asks_and_Cancel_does_nothing()
    {
        using var session = await OpenDirtySessionAsync();
        byte[] changed = [9, 9, 9, 9, 9];
        await File.WriteAllBytesAsync(_path, changed); // another program wrote the file
        _dialogs.FileChangedAnswer = FileChangedAnswer.Cancel;

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, _dialogs.FileChangedAsks);
        Assert.Empty(_writer.Calls);
        Assert.Empty(_log);
        Assert.Equal(changed, await File.ReadAllBytesAsync(_path));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Changed_fingerprint_OverwriteAnyway_saves_over_the_file()
    {
        using var session = await OpenDirtySessionAsync();
        byte[] changed = [9, 9, 9, 9, 9];
        await File.WriteAllBytesAsync(_path, changed);
        _dialogs.FileChangedAnswer = FileChangedAnswer.OverwriteAnyway;

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(WithMarker(changed), await File.ReadAllBytesAsync(_path));
        Assert.Equal(await DocumentFingerprint.ComputeAsync(_path), session.OpenFingerprint);
    }

    [Fact]
    public async Task Changed_fingerprint_SaveAs_switches_to_a_picked_path()
    {
        using var session = await OpenDirtySessionAsync();
        byte[] changed = [9, 9, 9, 9, 9];
        await File.WriteAllBytesAsync(_path, changed);
        var picked = Path.Combine(_dir, "b.pdf");
        _dialogs.FileChangedAnswer = FileChangedAnswer.SaveAs;
        _dialogs.NextSavePath = picked;

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(["a.pdf"], _dialogs.SuggestedNames);
        var call = Assert.Single(_writer.Calls);
        Assert.Equal(_path, call.Source);
        Assert.Equal(picked, call.Destination);
        Assert.Equal(changed, await File.ReadAllBytesAsync(_path));
        Assert.Equal(picked, session.Path);
        Assert.True(result.Saved!.IsSaveAs);
        Assert.Equal(["write", "replace", "reopen:" + picked], _log); // the old file never needed releasing
    }

    [Fact]
    public async Task Save_as_moves_the_session_to_the_new_path_and_keeps_the_old_file_untouched()
    {
        using var session = await OpenDirtySessionAsync();
        var picked = Path.Combine(_dir, "b.pdf");
        _dialogs.NextSavePath = picked;

        var result = await Service().SaveAsync(session, _viewer, saveAs: true, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(0, _dialogs.FileChangedAsks);
        Assert.Equal(_original, await File.ReadAllBytesAsync(_path));
        Assert.Equal(WithMarker(_original), await File.ReadAllBytesAsync(picked));
        Assert.Equal(picked, session.Path);
        Assert.Equal(await DocumentFingerprint.ComputeAsync(picked), session.OpenFingerprint);
        Assert.False(session.IsDirty);
        Assert.Equal(picked, _viewer.OpenPath);
        Assert.Empty(TempFiles());
        Assert.Equal(_path, result.Saved!.OldPath);
        Assert.Equal(picked, result.Saved.NewPath);
        Assert.True(result.Saved.IsSaveAs);
    }

    [Fact]
    public async Task Save_as_onto_the_open_file_releases_it_like_a_save()
    {
        using var session = await OpenDirtySessionAsync();
        _dialogs.NextSavePath = _path.ToUpperInvariant();

        var result = await Service().SaveAsync(session, _viewer, saveAs: true, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal("release", _log[1]);
        Assert.Equal(_path, session.Path);
        Assert.False(result.Saved!.IsSaveAs);
    }

    [Fact]
    public async Task Save_as_dialog_cancelled_returns_Cancelled_without_writing()
    {
        using var session = await OpenDirtySessionAsync();
        _dialogs.NextSavePath = null;

        var result = await Service().SaveAsync(session, _viewer, saveAs: true, CancellationToken.None);

        Assert.Equal(SaveOutcome.Cancelled, result.Outcome);
        Assert.Single(_dialogs.SuggestedNames);
        Assert.Empty(_writer.Calls);
        Assert.Empty(_log);
        Assert.Equal(_path, session.Path);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Renderer_is_released_before_replace_and_reopened_after()
    {
        using var session = await OpenDirtySessionAsync();

        await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(["write", "release", "replace", "reopen:" + _path], _log);
    }

    [Fact]
    public async Task Stroke_drawn_during_the_write_stays_dirty()
    {
        using var session = await OpenDirtySessionAsync();
        _writer.Gate = new TaskCompletionSource();

        var saving = Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);
        await _writer.Started.Task;
        Assert.True(session.CurrentEditor!.CommitStroke(_line, InkTool.Pen, Palette.Colors[1], 1));
        var late = session.CurrentEditor.Page.Strokes[^1];
        _writer.Gate.SetResult();
        var result = await saving;

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Single(Assert.Single(_writer.Calls).Changes[0].Added); // the snapshot was taken before
        Assert.True(session.IsDirty);
        Assert.Equal([late], Assert.Single(session.CaptureSaveSnapshot().Changes).Added);
    }

    [Fact]
    public async Task MarkSaved_runs_after_the_viewer_reopened()
    {
        using var session = await OpenDirtySessionAsync();
        session.DirtyChanged += (_, _) => _log.Add("marked");

        await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(["write", "release", "replace", "reopen:" + _path, "marked"], _log);
    }

    [Fact]
    public async Task Save_as_onto_a_locked_existing_target_leaves_the_viewer_and_both_files_alone()
    {
        using var session = await OpenDirtySessionAsync();
        var picked = Path.Combine(_dir, "b.pdf");
        byte[] other = [7, 7, 7];
        await File.WriteAllBytesAsync(picked, other);
        _dialogs.NextSavePath = picked;
        var service = Service((temp, target) =>
            throw new ReplaceFailedException(temp, target, new IOException("locked")));

        var result = await service.SaveAsync(session, _viewer, saveAs: true, CancellationToken.None);

        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(string.Format(Strings.ErrorSaveReplaceFailed, _dir), result.Message);
        Assert.Equal(["write", "replace"], _log); // never released, never reopened
        Assert.False(_viewer.IsReleased);
        Assert.Equal(other, await File.ReadAllBytesAsync(picked));
        Assert.Equal(_original, await File.ReadAllBytesAsync(_path));
        Assert.Equal(_path, session.Path);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Save_as_onto_the_open_file_checks_the_fingerprint_like_a_save()
    {
        using var session = await OpenDirtySessionAsync();
        byte[] changed = [9, 9, 9, 9, 9];
        await File.WriteAllBytesAsync(_path, changed);
        _dialogs.NextSavePath = _path;
        _dialogs.FileChangedAnswer = FileChangedAnswer.Cancel;

        var result = await Service().SaveAsync(session, _viewer, saveAs: true, CancellationToken.None);

        Assert.Equal(SaveOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, _dialogs.FileChangedAsks);
        Assert.Empty(_writer.Calls);
        Assert.Equal(changed, await File.ReadAllBytesAsync(_path));
    }

    [Fact]
    public async Task Failure_of_MarkSaved_after_the_replace_still_reports_the_file_as_saved()
    {
        using var session = await OpenDirtySessionAsync();
        session.DirtyChanged += (_, _) => throw new InvalidOperationException(@"C:\secret");

        var result = await Service().SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(Strings.ErrorSavedStateNotUpdated, result.Message);
        Assert.Null(result.Saved); // the journal must not treat this as a clean save
        Assert.Equal(WithMarker(_original), await File.ReadAllBytesAsync(_path));
    }

    [Fact]
    public async Task Missing_target_after_a_failed_replace_adopts_the_verified_copy()
    {
        using var session = await OpenDirtySessionAsync();
        var service = Service(MissingTargetOnce());

        var result = await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);

        var copy = _writer.LastTempPath!;
        Assert.Equal(SaveOutcome.Failed, result.Outcome);
        Assert.Equal(string.Format(Strings.ErrorSaveTargetMissing, _dir), result.Message);
        Assert.False(File.Exists(_path));
        Assert.True(File.Exists(copy));
        Assert.Equal(["write", "release", "replace", "reopen:" + copy], _log);
        Assert.Equal(copy, _viewer.OpenPath);
        Assert.True(session.IsDirty);
        Assert.Equal(_path, session.Path);
    }

    [Fact]
    public async Task After_a_missing_target_Save_as_writes_from_the_verified_copy()
    {
        using var session = await OpenDirtySessionAsync();
        var service = Service(MissingTargetOnce());
        await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);
        var copy = _writer.LastTempPath!;
        var picked = Path.Combine(_dir, "b.pdf");
        _dialogs.NextSavePath = picked;

        var result = await service.SaveAsync(session, _viewer, saveAs: true, CancellationToken.None);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(copy, _writer.Calls[1].Source);
        Assert.Equal(picked, _writer.Calls[1].Destination);
        Assert.Single(_writer.Calls[1].Changes); // the baseline was not advanced by the failed save
        Assert.Equal(WithMarker(WithMarker(_original)), await File.ReadAllBytesAsync(picked));
        Assert.Equal(picked, session.Path);
        Assert.False(session.IsDirty);
        Assert.Empty(TempFiles()); // the adopted copy is removed once its content is saved
    }

    [Fact]
    public async Task After_a_missing_target_Save_recreates_the_original_path()
    {
        using var session = await OpenDirtySessionAsync();
        using var other = await OpenDirtySessionAsync();
        var service = Service(MissingTargetOnce());
        Assert.False(service.HasAdoptedCopy(session));
        await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);
        var copy = _writer.LastTempPath!;
        Assert.True(service.HasAdoptedCopy(session)); // the only complete copy: its journal must survive
        Assert.Equal(Path.GetDirectoryName(copy), service.AdoptedCopyFolder(session));
        Assert.False(service.HasAdoptedCopy(other));

        var result = await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);
        Assert.False(service.HasAdoptedCopy(session)); // saved over the original again

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(0, _dialogs.FileChangedAsks); // the missing original is not "changed": it was replaced by the copy
        Assert.Equal(copy, _writer.Calls[1].Source);
        Assert.Equal(_path, _writer.Calls[1].Destination);
        Assert.Equal(WithMarker(WithMarker(_original)), await File.ReadAllBytesAsync(_path));
        Assert.Equal(_path, session.Path);
        Assert.Equal(_path, _viewer.OpenPath);
        Assert.False(session.IsDirty);
        Assert.Empty(TempFiles());

        // Back to normal: the next save reads the original path again.
        Assert.True(session.CurrentEditor!.CommitStroke(_line, InkTool.Pen, Palette.Colors[1], 1));
        await service.SaveAsync(session, _viewer, saveAs: false, CancellationToken.None);
        Assert.Equal(_path, _writer.Calls[2].Source);
    }

    /// <summary>The first replace deletes the target and fails like Windows error 1176; later ones work.</summary>
    private static Action<string, string>? MissingTargetOnce()
    {
        var failed = false;
        return (temp, target) =>
        {
            if (!failed)
            {
                failed = true;
                File.Delete(target);
                throw new ReplaceFailedException(temp, target, new IOException("1176"));
            }

            FileReplacer.Replace(temp, target);
        };
    }

    private DocumentSaveService Service(Action<string, string>? replace = null) =>
        new(_writer, _dialogs, (temp, target) =>
        {
            _log.Add("replace");
            if (replace is not null)
            {
                replace(temp, target);
                return Task.CompletedTask;
            }

            return DocumentSaveService.ReplaceOnBackgroundThreadAsync(temp, target);
        });

    private async Task<DocumentSession> OpenDirtySessionAsync()
    {
        var session = new DocumentSession(new FakePageSource((612, 792)), _path, await DocumentFingerprint.ComputeAsync(_path));
        await session.GoToPageAsync(0);
        Assert.True(session.CurrentEditor!.CommitStroke(_line, InkTool.Pen, Palette.Colors[0], 1));
        Assert.True(session.IsDirty);
        return session;
    }

    private string[] TempFiles() => Directory.GetFiles(_dir, "*.drbk-tmp");

    private static byte[] WithMarker(byte[] content) => [.. content, .. FakeDocumentWriter.Marker];
}
