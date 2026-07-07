using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MultividStreamer.App.Models;
using MultividStreamer.App.Selection;
using MultividStreamer.App.Services;
using MultividStreamer.App.Services.Api;

namespace MultividStreamer.App;

public partial class MainWindow : Window
{
    private readonly LibrarySourceStore sourceStore = new();
    private readonly CatalogStore catalogStore = new();
    private readonly LibraryCatalogScanner catalogScanner = new();
    private readonly TranscodeSettingsStore transcodeSettingsStore = new();
    private readonly ApiSettings apiSettings;
    private readonly TrustedDeviceStore trustedDeviceStore = new();
    private readonly LocalApiHost localApiHost;
    private readonly ObservableCollection<LibrarySource> sources;
    private readonly ObservableCollection<TrustedDevice> trustedDevices;

    // While the API runs, a pairing code is always active and rotates on this timer
    // (same period as the code's validity), so the headset can pair at any moment
    // without someone clicking "Generer code" on the PC first.
    private readonly DispatcherTimer pairingRotationTimer = new()
    {
        Interval = TimeSpan.FromMinutes(LocalApiHost.PairingCodeLifetimeMinutes)
    };

    private bool startupPromptShown;

    public MainWindow()
    {
        InitializeComponent();

        // Register the transcode formats first: this makes those extensions count as
        // videos for cataloguing AND flags them for ffmpeg, before the catalog scan or
        // API host run. Seeds transcode-formats.json on first launch. Also kick off
        // encoder detection (NVENC/QSV/AMF/CPU) so the same build runs optimally here.
        TranscodeSettings transcodeSettings = transcodeSettingsStore.Load();
        SupportedMediaTypes.SetTranscodeExtensions(TranscodeSettingsStore.NormalizeExtensions(transcodeSettings.TranscodeExtensions));
        TranscodeEncoder.Initialize(transcodeSettings.Encoder);

        apiSettings = new ApiSettingsStore().LoadOrCreate();
        localApiHost = new LocalApiHost(catalogStore, sourceStore, apiSettings, trustedDeviceStore);
        // LAN exposure is always on now (the streamer exists to serve the headset over
        // the local network); the old opt-in "Exposer sur le LAN" checkbox was removed.
        localApiHost.AllowLan = true;
        localApiHost.TrustedDevicesChanged += LocalApiHost_TrustedDevicesChanged;
        localApiHost.StreamDiagnosticsUpdated += LocalApiHost_StreamDiagnosticsUpdated;
        localApiHost.RequestLogged += LocalApiHost_RequestLogged;
        sources = new ObservableCollection<LibrarySource>(sourceStore.Load());
        trustedDevices = new ObservableCollection<TrustedDevice>(trustedDeviceStore.Load());
        SourcesList.ItemsSource = sources;
        TrustedDevicesList.ItemsSource = trustedDevices;
        StorePathText.Text = sourceStore.StorePath;
        ApiTokenText.Text = localApiHost.Token;
        StreamDiagnosticsText.Text = "Aucune lecture";
        pairingRotationTimer.Tick += (_, _) => RotatePairingCode();
        ContentRendered += MainWindow_ContentRendered;
        UpdateTrustedDeviceStatus();
        UpdateApiStatus();
        UpdateStatus();
    }

    // First thing after the window shows: offer to start serving right away. Forgetting
    // to click "Demarrer API" is the #1 reason the headset "can't find" the streamer.
    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (startupPromptShown)
        {
            return;
        }

        startupPromptShown = true;

        MessageBoxResult result = MessageBox.Show(
            this,
            "Start Network Broadcast?",
            "Multivid Streamer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            StartApi();
        }
    }

    private void StartApi()
    {
        localApiHost.Start();
        RotatePairingCode();
        UpdateApiStatus();
    }

    private void StopApi()
    {
        pairingRotationTimer.Stop();
        localApiHost.CancelPairing();
        localApiHost.Stop();
        UpdateApiStatus();
    }

    // Issues a fresh pairing code and restarts the rotation window. Called when the
    // API starts, every PairingCodeLifetimeMinutes, when a headset consumes the code
    // by pairing, and on the manual "Generer code" button.
    private void RotatePairingCode()
    {
        if (!localApiHost.IsRunning)
        {
            pairingRotationTimer.Stop();
            return;
        }

        PairingCodeText.Text = localApiHost.StartPairing();
        UpdatePairingStatus();
        pairingRotationTimer.Stop();
        pairingRotationTimer.Start();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        AddSourcesDialog dialog = new()
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        List<LibrarySource> newSources = new();
        foreach (string path in dialog.SelectedPaths)
        {
            LibrarySource? source = LibrarySourceFactory.TryCreate(path);
            if (source == null || sources.Any(existing => string.Equals(existing.Path, source.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            sources.Add(source);
            newSources.Add(source);
        }

        SaveSources();

        if (newSources.Count == 0)
        {
            UpdateStatus("Aucune nouvelle source ajoutee.");
            return;
        }

        // Scan ONLY the newly-added sources and merge them into the catalogue, so their
        // contents show up on the headset right away instead of looking empty until a
        // manual Rescan. (Rescan stays for a full refresh of everything.)
        SetBusy(true, $"Scan de {newSources.Count} nouvelle(s) source(s)...");

        ScanResult result;
        try
        {
            List<LibrarySource> snapshot = newSources.ToList();
            result = await Task.Run(() => catalogScanner.Scan(snapshot));
        }
        catch (Exception exception)
        {
            SetBusy(false);
            MessageBox.Show(this, $"Le scan a echoue: {exception.Message}", "Multivid Streamer", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus($"{newSources.Count} source(s) ajoutee(s) - scan echoue, faites Rescan.");
            return;
        }

        // Merge: keep existing catalogue items from other sources, then the freshly
        // scanned items, de-duplicated by stable id (handles re-add / overlapping
        // sources) while preserving order.
        HashSet<string> newSourceIds = new(newSources.Select(source => source.Id), StringComparer.Ordinal);
        HashSet<string> seenItemIds = new(StringComparer.Ordinal);
        List<CatalogItem> merged = new();
        foreach (CatalogItem item in catalogStore.Load().Where(item => !newSourceIds.Contains(item.SourceId)).Concat(result.Items))
        {
            if (seenItemIds.Add(item.Id))
            {
                merged.Add(item);
            }
        }

        catalogStore.Save(merged);

        SetBusy(false);
        UpdateStatus($"{newSources.Count} source(s) ajoutee(s) et scannee(s): {result.Items.Count} fichier(s).");
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        List<LibrarySource> selectedSources = SourcesList.SelectedItems
            .OfType<LibrarySource>()
            .ToList();

        if (selectedSources.Count == 0)
        {
            MessageBox.Show(this, "Selectionnez au moins une source a effacer.", "Multivid Streamer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (LibrarySource source in selectedSources)
        {
            sources.Remove(source);
        }

        SaveSources();
        UpdateStatus($"{selectedSources.Count} source(s) effacee(s).");
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        if (sources.Count == 0)
        {
            catalogStore.Save(Array.Empty<CatalogItem>());
            UpdateStatus("Catalogue vide enregistre.");
            return;
        }

        SetBusy(true, "Scan en cours...");

        ScanResult result;
        try
        {
            List<LibrarySource> sourceSnapshot = sources.ToList();
            result = await Task.Run(() => catalogScanner.Scan(sourceSnapshot));
        }
        catch (Exception exception)
        {
            SetBusy(false);
            MessageBox.Show(this, $"Le scan a echoue: {exception.Message}", "Multivid Streamer", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus("Scan echoue.");
            return;
        }

        // Option A: keep every source, including those currently offline — do NOT
        // replace the list with only the available ones. The catalogue is rebuilt from
        // whatever was reachable this run; offline sources simply contribute no items
        // until their disk returns. Refresh so the availability greying re-evaluates.
        catalogStore.Save(result.Items);
        SourcesList.Items.Refresh();
        SetBusy(false);
        UpdateStatus(CreateScanMessage(result));
    }

    private void ApiToggleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (localApiHost.IsRunning)
            {
                StopApi();
            }
            else
            {
                StartApi();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Impossible de modifier l'etat de l'API: {exception.Message}", "Multivid Streamer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PairingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!localApiHost.IsRunning)
        {
            MessageBox.Show(this, "Demarrez l'API avant de generer un code de pairing.", "Multivid Streamer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RotatePairingCode();
    }

    private void ClearLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        TypedConfirmationDialog dialog = new(
            "Multivid Streamer",
            "Do you really want to clear the whole library?",
            "Yes")
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Sources + catalogue only: trusted headsets stay paired.
        sources.Clear();
        SaveSources();
        catalogStore.Save(Array.Empty<CatalogItem>());
        UpdateStatus("Bibliotheque videe.");
    }

    private void RevokeTrustedDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        TrustedDevice? selectedDevice = TrustedDevicesList.SelectedItem as TrustedDevice;
        if (selectedDevice == null)
        {
            MessageBox.Show(this, "Selectionnez un appareil a revoquer.", "Multivid Streamer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Revoquer l'acces de \"{selectedDevice.Name}\"?",
            "Multivid Streamer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (trustedDeviceStore.RemoveDevice(selectedDevice.Id))
        {
            RefreshTrustedDevices();
            UpdateStatus($"Appareil revoque: {selectedDevice.Name}.");
        }
    }

    private void LocalApiHost_TrustedDevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshTrustedDevices();

            // A successful pairing consumes the active code server-side; issue a new
            // one right away so the "always a valid code" invariant holds.
            if (localApiHost.IsRunning)
            {
                RotatePairingCode();
            }
        });
    }

    // Rolling activity log shown in the "Streaming" area, fed by both the detailed
    // stream snapshots and the lighter per-request lines, so you can watch the
    // headset connect, browse, and stream in real time (and spot a request that
    // arrives but never gets answered).
    private const int MaxDiagLines = 7;
    private readonly Queue<string> diagLines = new();

    private void LocalApiHost_StreamDiagnosticsUpdated(object? sender, StreamDiagnosticsSnapshot diagnostics)
    {
        AppendDiag(
            $"{diagnostics.TimestampLocal:HH:mm:ss}  {diagnostics.Method}  Range: {diagnostics.Range}  " +
            $"Auth: {diagnostics.AuthSource}  Status: {diagnostics.StatusCode}  Envoye: {FormatBytes(diagnostics.BytesSent)}  " +
            $"Moyenne: {diagnostics.AverageMbps:F1} Mbps  Client: {diagnostics.ClientState}");
    }

    private void LocalApiHost_RequestLogged(string line)
    {
        AppendDiag(line);
    }

    private void AppendDiag(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            diagLines.Enqueue(line);
            while (diagLines.Count > MaxDiagLines)
            {
                diagLines.Dequeue();
            }

            StreamDiagnosticsText.Text = string.Join(Environment.NewLine, diagLines);
        });
    }

    private void SaveSources()
    {
        sourceStore.Save(sources);
    }

    private void RefreshTrustedDevices()
    {
        trustedDevices.Clear();
        foreach (TrustedDevice device in trustedDeviceStore.Load())
        {
            trustedDevices.Add(device);
        }

        UpdateTrustedDeviceStatus();
    }

    private void UpdateStatus(string? message = null)
    {
        string countText = sources.Count == 1 ? "1 source" : $"{sources.Count} sources";
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? countText : $"{message}  {countText}.";
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        AddButton.IsEnabled = !isBusy;
        RescanButton.IsEnabled = !isBusy;
        RemoveButton.IsEnabled = !isBusy;
        ClearLibraryButton.IsEnabled = !isBusy;
        ApiToggleButton.IsEnabled = !isBusy;
        PairingCodeButton.IsEnabled = !isBusy && localApiHost.IsRunning;
        RevokeTrustedDeviceButton.IsEnabled = !isBusy;
        SourcesList.IsEnabled = !isBusy;
        TrustedDevicesList.IsEnabled = !isBusy;

        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private void UpdateApiStatus()
    {
        ApiToggleButton.Content = localApiHost.IsRunning ? "Arreter API" : "Demarrer API";
        PairingCodeButton.IsEnabled = localApiHost.IsRunning;
        ApiCatalogUrlText.Text = localApiHost.CatalogUrl;
        ApiStatusText.Text = localApiHost.IsRunning
            ? $"Demarree sur {localApiHost.BaseUrl}"
            : "Arretee";
        UpdatePairingStatus();
        UpdateTrustedDeviceStatus();
    }

    private void UpdatePairingStatus()
    {
        string? code = localApiHost.PairingCode;
        DateTime? expiresUtc = localApiHost.PairingExpiresUtc;
        PairingCodeText.Text = code ?? string.Empty;
        PairingCodeStatusText.Text = code == null || expiresUtc == null
            ? "Aucun code actif"
            : $"Nouveau code a {expiresUtc.Value.ToLocalTime():HH:mm}";
    }

    private void UpdateTrustedDeviceStatus()
    {
        TrustedDeviceStatusText.Text = trustedDevices.Count == 0
            ? "Aucun appareil approuve"
            : trustedDevices.Count == 1
                ? "1 appareil approuve"
                : $"{trustedDevices.Count} appareils approuves";
    }

    private static string CreateScanMessage(ScanResult result)
    {
        string message = $"Scan termine: {result.Items.Count} item(s), {result.VideoCount} video(s), {result.ImageCount} image(s), {result.RawImageCount} RAW, {result.ZipCount} ZIP.";

        if (result.MissingSourcesRemoved != 0)
        {
            message += $" {result.MissingSourcesRemoved} source(s) indisponible(s) (conservee(s)).";
        }

        if (result.DuplicateFilesSkipped != 0)
        {
            message += $" {result.DuplicateFilesSkipped} doublon(s) ignore(s).";
        }

        return message;
    }

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024d;
        const double mib = kib * 1024d;
        const double gib = mib * 1024d;

        if (bytes >= gib)
        {
            return $"{bytes / gib:F2} GB";
        }

        if (bytes >= mib)
        {
            return $"{bytes / mib:F2} MB";
        }

        if (bytes >= kib)
        {
            return $"{bytes / kib:F1} KB";
        }

        return $"{bytes} B";
    }
}
