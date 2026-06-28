using System.Windows;
using System.Windows.Input;
using MultividStreamer.App.Services;

namespace MultividStreamer.App.Selection;

public partial class AddSourcesDialog : Window
{
    private readonly FileSystemBrowserService browserService = new();
    private int loadVersion;
    private string? currentPath;

    public IReadOnlyList<string> SelectedPaths { get; private set; } = Array.Empty<string>();

    public AddSourcesDialog()
    {
        InitializeComponent();
        _ = LoadEntriesAsync();
    }

    private async Task LoadEntriesAsync()
    {
        string? requestedPath = currentPath;
        int requestedVersion = ++loadVersion;

        SetLoadingState(requestedPath);

        IReadOnlyList<FileSystemSelectionEntry> entries;
        try
        {
            entries = await Task.Run(() => browserService.GetEntries(requestedPath));
        }
        catch (Exception exception)
        {
            if (requestedVersion != loadVersion)
            {
                return;
            }

            EntriesList.ItemsSource = Array.Empty<FileSystemSelectionEntry>();
            EntriesList.IsEnabled = true;
            StatusText.Text = $"Impossible de lire ce dossier: {exception.Message}";
            return;
        }

        if (requestedVersion != loadVersion)
        {
            return;
        }

        EntriesList.ItemsSource = entries;
        EntriesList.IsEnabled = true;

        CurrentPathText.Text = string.IsNullOrWhiteSpace(requestedPath) ? "Ce PC" : requestedPath;
        UpButton.IsEnabled = !string.IsNullOrWhiteSpace(requestedPath);
        AddCurrentFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(requestedPath)
            && !LibrarySourceFactory.IsBlockedDirectorySource(requestedPath);
        StatusText.Text = $"{entries.Count} element(s)";
    }

    private void SetLoadingState(string? requestedPath)
    {
        CurrentPathText.Text = string.IsNullOrWhiteSpace(requestedPath) ? "Ce PC" : requestedPath;
        EntriesList.IsEnabled = false;
        UpButton.IsEnabled = false;
        AddCurrentFolderButton.IsEnabled = false;
        StatusText.Text = "Chargement...";
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        currentPath = browserService.GetParentPath(currentPath);
        await LoadEntriesAsync();
    }

    private void AddCurrentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return;
        }

        SelectedPaths = new[] { currentPath };
        DialogResult = true;
    }

    private void AddSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EntriesList.IsEnabled)
        {
            return;
        }

        List<string> selectedPaths = EntriesList.SelectedItems
            .OfType<FileSystemSelectionEntry>()
            .Select(entry => entry.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedPaths.Count == 0)
        {
            MessageBox.Show(this, "Selectionnez au moins un fichier ou dossier.", "Multivid Streamer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPaths = selectedPaths;
        DialogResult = true;
    }

    private async void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntriesList.SelectedItem is not FileSystemSelectionEntry entry || !entry.IsDirectory)
        {
            return;
        }

        currentPath = entry.FullPath;
        await LoadEntriesAsync();
    }
}
