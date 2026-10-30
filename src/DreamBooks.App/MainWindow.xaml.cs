using System.Windows;
using DreamBooks.App.Services;
using DreamBooks.App.ViewModels;

namespace DreamBooks.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new(new FileDialogService());

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OnFitWidthClick(object sender, RoutedEventArgs e) =>
        _viewModel.ZoomVm.FitWidth(PageViewer.FitWidthViewport, _viewModel.PageSize.WidthPt);
}
