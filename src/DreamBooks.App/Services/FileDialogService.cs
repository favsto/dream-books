using System.Windows;
using DreamBooks.App.Resources;
using Microsoft.Win32;

namespace DreamBooks.App.Services;

public sealed class FileDialogService : IFileDialogService
{
    public string? PickPdf()
    {
        var dialog = new OpenFileDialog
        {
            Filter = Strings.PdfFileFilter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string message) =>
        MessageBox.Show(message, Strings.Panel, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
