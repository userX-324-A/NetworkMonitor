using NetworkMonitorUI.ViewModels;
using NetworkMonitorUI.Services; // Add this using
using System;
using System.ComponentModel;
using System.Threading.Tasks; // Keep this for await
using System.Windows;

namespace NetworkMonitorUI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    // Inject ViewModel via constructor
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel; // Set DataContext to the injected ViewModel

        // Remove Loaded event handler if it only initialized the ViewModel
        // this.Loaded -= MainWindow_Loaded;
        // If it did other things, keep it, but remove ViewModel logic

        this.Closing += Window_Closing; // Keep closing event
    }

    // Remove the old Loaded event handler if no longer needed
    /*
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // ViewModel initialization and data loading is now handled 
        // by the ViewModel's constructor and InitializeAsync method.
        // If other UI setup was done here, keep that part.
    }
    */

    // Keep the Closing event handler to dispose the ViewModel
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Dispose the ViewModel if it implements IDisposable
        if (_viewModel is IDisposable disposableViewModel)
        {
            disposableViewModel.Dispose();
        }
    }
}