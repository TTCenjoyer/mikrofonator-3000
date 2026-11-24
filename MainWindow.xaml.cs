using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FiltrMikrofonu.Audio;

namespace FiltrMikrofonu;

public partial class MainWindow : Window
{
    private sealed record FilterCard(VoiceFilter Id, string Title, string Detail, Geometry Icon);

    private static readonly FilterCard[] Filters =
    [
        new(VoiceFilter.Reference, "Alastor", "Radiowy przester", Geometry.Parse("M1,9 L4,9 7,3 10,15 13,6 16,11 19,9")),
        new(VoiceFilter.Clean, "Czysty", "Bez efektu", Geometry.Parse("M1,9 H18")),
        new(VoiceFilter.Studio, "Studio", "Barwa i kompresja", Geometry.Parse("M1,4 H18 M1,10 H18 M1,16 H18 M6,1 V7 M13,7 V13 M8,13 V19")),
        new(VoiceFilter.Radio, "Telefon", "Wąskie pasmo", Geometry.Parse("M3,8 L15,2 M3,8 H17 Q19,8 19,10 V17 H1 V10 Q1,8 3,8 M5,12 H9 M5,14 H9 M14,13 H15")),
        new(VoiceFilter.Megaphone, "Megafon", "Mocne przesterowanie", Geometry.Parse("M2,7 H6 L16,2 V16 L6,12 H2 Z M5,12 L7,18 H10 L8,13 M19,6 L22,4 M19,11 L22,13")),
        new(VoiceFilter.Anonymous, "Anonim", "Głos obniżony i zniekształcony",
            Geometry.Parse("M2,4 H18 V14 L10,20 2,14 Z M5,9 H8 M12,9 H15 M7,14 H13")),
    ];

    private static readonly Geometry Play = Geometry.Parse("M2,1 L11,6.5 2,12 Z");
    private static readonly Geometry Stop = Geometry.Parse("M1,1 H11 V11 H1 Z");
    private readonly DispatcherTimer meterTimer;
    private AudioDevices? devices;
    private AudioSession? session;
    private Task? currentChange;
    private bool initialized, updatingDevices, busy, closing, closedAudio;
    private string? transition;
    private float shownInput, shownOutput;

    public MainWindow()
    {
        InitializeComponent();
        meterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        meterTimer.Tick += UpdateMeters;

        var saved = Preferences.Load();
        FilterBox.ItemsSource = Filters;
        FilterBox.SelectedValue = VoiceFilter.Clean;
        MixSlider.Value = Math.Clamp(saved.Mix, MixSlider.Minimum, MixSlider.Maximum);
        InputSlider.Value = Math.Clamp(saved.InputDb, InputSlider.Minimum, InputSlider.Maximum);
        OutputSlider.Value = Math.Clamp(saved.OutputDb, OutputSlider.Minimum, OutputSlider.Maximum);
        BeforeMixSlider.Value = Math.Clamp(saved.BeforeMix ?? (saved.Filter == VoiceFilter.BeforeVoice ? saved.Mix : 100), 0, 100);
        BeforeTimeSlider.Value = saved.BeforeMilliseconds;
        RoomToggle.IsChecked = saved.RoomEnabled;
        var reverb = saved.Reverb ?? ReverbSettings.DefaultReverb;
        ReverbSizeSlider.Value = reverb.RoomSize;
        ReverbPreDelaySlider.Value = reverb.PreDelay;
        ReverbDecaySlider.Value = reverb.Reverberance;
        ReverbDampingSlider.Value = reverb.Damping;
        ReverbLowSlider.Value = reverb.ToneLow;
        ReverbHighSlider.Value = reverb.ToneHigh;
        ReverbWetSlider.Value = reverb.WetGain;
        ReverbDrySlider.Value = reverb.DryGain;
        ReverbWidthSlider.Value = reverb.StereoWidth;
        ReverbWetOnly.IsChecked = reverb.WetOnly;
        var preverb = saved.Preverb ?? ReverbSettings.DefaultPreverb;
        PreverbSizeSlider.Value = preverb.RoomSize;
        PreverbPreDelaySlider.Value = preverb.PreDelay;
        PreverbDecaySlider.Value = preverb.Reverberance;
        PreverbDampingSlider.Value = preverb.Damping;
        PreverbLowSlider.Value = preverb.ToneLow;
        PreverbHighSlider.Value = preverb.ToneHigh;
        PreverbWetSlider.Value = preverb.WetGain;
        PreverbDrySlider.Value = preverb.DryGain;
        PreverbWidthSlider.Value = preverb.StereoWidth;
        PreverbWetOnly.IsChecked = preverb.WetOnly;
        var noise = saved.Noise ?? new NoiseSettings();
        GateToggle.IsChecked = noise.GateEnabled;
        GateThresholdSlider.Value = noise.GateThreshold;
        GateHoldSlider.Value = noise.GateHold;
        GateReleaseSlider.Value = noise.GateRelease;
        DenoiseToggle.IsChecked = noise.DenoiseEnabled;
        NoiseFloorSlider.Value = noise.NoiseFloor;
        NoiseReductionSlider.Value = noise.Reduction;
        EchoMixSlider.Value = Math.Clamp(saved.EchoMix, 0, 100);
        EchoTimeSlider.Value = saved.EchoMilliseconds;
        RefreshDevices(saved.Microphone, saved.Output, saved.Monitor);
        initialized = true;
        RefreshEffect();
        UpdateControls();
        meterTimer.Start();
    }

    private AudioDevice? Microphone => MicrophoneBox.SelectedItem as AudioDevice;
    private AudioDevice? Output => OutputBox.SelectedItem as AudioDevice;
    private AudioDevice? Monitor => MonitorBox.SelectedItem as AudioDevice;
    private VoiceFilter SelectedTone => FilterBox.SelectedValue is VoiceFilter filter ? filter : VoiceFilter.Clean;
    private VoiceFilter SelectedFilter => SelectedTone;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint handle = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        int captionColor = 0x001A1112;
        DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        SetDetail("");
        RefreshDevices(Microphone?.Id, Output?.Id, Monitor?.Id);
        UpdateControls();
    }

    private void RefreshDevices(string? microphoneId, string? outputId, string? monitorId)
    {
        updatingDevices = true;
        try
        {
            devices = AudioDevices.Read();
            AudioDevice? physicalDefault = devices.Inputs.FirstOrDefault(device =>
                device.Id == devices.DefaultInput && !device.IsVirtualMicrophone);
            MicrophoneBox.ItemsSource = devices.Inputs;
            MicrophoneBox.SelectedItem = Find(devices.Inputs, microphoneId) ?? physicalDefault ??
                devices.Inputs.FirstOrDefault(device => !device.IsVirtualMicrophone) ??
                Find(devices.Inputs, devices.DefaultInput) ?? devices.Inputs.FirstOrDefault();

            OutputBox.ItemsSource = devices.Outputs;
            OutputBox.SelectedItem = Find(devices.Outputs, outputId) ??
                devices.Outputs.FirstOrDefault(device => device.CablePair == "Filtr Mikrofonu") ??
                devices.Outputs.FirstOrDefault(device => device.CablePair == "CABLE") ??
                devices.Outputs.FirstOrDefault(device => device.CablePair is not null);
            RefreshMonitorList(monitorId);
        }
        catch (Exception ex)
        {
            SetDetail($"Windows nie udostępnił listy urządzeń. {ex.Message}", true);
        }
        finally
        {
            updatingDevices = false;
        }
    }

    private void RefreshMonitorList(string? selectedId)
    {
        if (devices is null)
            return;
        var candidates = devices.Outputs.Where(device => device.Id != Output?.Id && device.CablePair is null).ToArray();
        MonitorBox.ItemsSource = candidates;
        MonitorBox.SelectedItem = Find(candidates, selectedId) ?? Find(candidates, devices.DefaultOutput) ?? candidates.FirstOrDefault();
        if (Monitor is null)
            MonitorToggle.IsChecked = false;
    }

    private static AudioDevice? Find(IEnumerable<AudioDevice> collection, string? id) => collection.FirstOrDefault(device => device.Id == id);

    private void Device_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || updatingDevices)
            return;
        if (sender == OutputBox)
        {
            updatingDevices = true;
            RefreshMonitorList(Monitor?.Id);
            updatingDevices = false;
        }
        SetDetail("");
        UpdateControls();
    }

    private async void Monitor_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || updatingDevices || busy || closing)
            return;
        SetDetail("");
        if (session is { } active)
            await RunAudioChange(() => SetMonitorAsync(active));
        else
            UpdateControls();
    }

    private async void MonitorDevice_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || updatingDevices || busy || closing)
            return;
        if (MonitorToggle.IsChecked == true && session is { } active)
            await RunAudioChange(() => SetMonitorAsync(active));
        else
            UpdateControls();
    }

    private async Task SetMonitorAsync(AudioSession active)
    {
        string? monitorId = MonitorToggle.IsChecked == true ? Monitor?.Id : null;
        try
        {
            await Task.Run(() => active.SetMonitor(monitorId));
        }
        catch (Exception ex)
        {
            await Task.Run(() => active.SetMonitor(null));
            DisableMonitor();
            SetDetail($"Nie udało się włączyć odsłuchu. {ex.Message}", true);
        }
    }

    private void DisableMonitor()
    {
        updatingDevices = true;
        MonitorToggle.IsChecked = false;
        updatingDevices = false;
    }

    private void Effect_Changed(object sender, RoutedEventArgs e)
    {
        if (initialized)
            RefreshEffect();
    }

    private void Before_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized)
            return;
        RefreshEffect();
    }

    private void RefreshEffect()
    {
        MixSlider.IsEnabled = SelectedFilter != VoiceFilter.Clean;
        MixValue.Text = $"{MixSlider.Value:0}%";
        InputValue.Text = $"{InputSlider.Value:+0;-0;0} dB";
        OutputValue.Text = $"{OutputSlider.Value:+0;-0;0} dB";
        BeforeMixValue.Text = $"{BeforeMixSlider.Value:0}%";
        BeforeTimeValue.Text = TimeLabel(BeforeTimeSlider.Value);
        ReverbSizeValue.Text = $"{ReverbSizeSlider.Value:0} %";
        ReverbPreDelayValue.Text = $"{ReverbPreDelaySlider.Value:0} ms";
        ReverbDecayValue.Text = $"{ReverbDecaySlider.Value:0} %";
        ReverbDampingValue.Text = $"{ReverbDampingSlider.Value:0} %";
        ReverbLowValue.Text = $"{ReverbLowSlider.Value:0} %";
        ReverbHighValue.Text = $"{ReverbHighSlider.Value:0} %";
        ReverbWetValue.Text = $"{ReverbWetSlider.Value:0} dB";
        ReverbDryValue.Text = $"{ReverbDrySlider.Value:0} dB";
        ReverbWidthValue.Text = $"{ReverbWidthSlider.Value:0} %";
        PreverbSizeValue.Text = $"{PreverbSizeSlider.Value:0} %";
        PreverbPreDelayValue.Text = $"{PreverbPreDelaySlider.Value:0} ms";
        PreverbDecayValue.Text = $"{PreverbDecaySlider.Value:0} %";
        PreverbDampingValue.Text = $"{PreverbDampingSlider.Value:0} %";
        PreverbLowValue.Text = $"{PreverbLowSlider.Value:0} %";
        PreverbHighValue.Text = $"{PreverbHighSlider.Value:0} %";
        PreverbWetValue.Text = $"{PreverbWetSlider.Value:0} dB";
        PreverbDryValue.Text = $"{PreverbDrySlider.Value:0} dB";
        PreverbWidthValue.Text = $"{PreverbWidthSlider.Value:0} %";
        EchoMixValue.Text = $"{EchoMixSlider.Value:0}%";
        EchoTimeValue.Text = TimeLabel(EchoTimeSlider.Value);
        GateThresholdValue.Text = $"{GateThresholdSlider.Value:0} dB";
        GateHoldValue.Text = $"{GateHoldSlider.Value:0} ms";
        GateReleaseValue.Text = $"{GateReleaseSlider.Value:0} ms";
        NoiseFloorValue.Text = $"{NoiseFloorSlider.Value:0} dB";
        NoiseReductionValue.Text = $"{NoiseReductionSlider.Value:0}%";
        session?.SetFilter(GetFilterSettings());
    }

    private static string TimeLabel(double milliseconds) => milliseconds < 1000
        ? $"{milliseconds:0} ms"
        : (milliseconds / 1000).ToString("0.##", CultureInfo.GetCultureInfo("pl-PL")) + " s";

    private FilterSettings GetFilterSettings() => new(
        SelectedFilter,
        (float)MixSlider.Value / 100,
        FilterSettings.DecibelsToGain(InputSlider.Value), FilterSettings.DecibelsToGain(OutputSlider.Value),
        (int)BeforeTimeSlider.Value,
        RoomToggle.IsChecked == true ? 1 : 0, VoiceAmbience.DefaultRoomMilliseconds,
        EchoToggle.IsChecked == true ? (float)EchoMixSlider.Value / 100 : 0, (int)EchoTimeSlider.Value, GetReverbSettings(), GetPreverbSettings(),
        BeforeToggle.IsChecked == true, (float)BeforeMixSlider.Value / 100, GetNoiseSettings());

    private ReverbSettings GetReverbSettings() => new(
        (float)ReverbSizeSlider.Value, (float)ReverbPreDelaySlider.Value, (float)ReverbDecaySlider.Value, (float)ReverbDampingSlider.Value, (float)ReverbLowSlider.Value, (float)ReverbHighSlider.Value, (float)ReverbWetSlider.Value, (float)ReverbDrySlider.Value, (float)ReverbWidthSlider.Value, ReverbWetOnly.IsChecked == true);

    private ReverbSettings GetPreverbSettings() => new(
        (float)PreverbSizeSlider.Value, (float)PreverbPreDelaySlider.Value, (float)PreverbDecaySlider.Value, (float)PreverbDampingSlider.Value, (float)PreverbLowSlider.Value, (float)PreverbHighSlider.Value, (float)PreverbWetSlider.Value, (float)PreverbDrySlider.Value, (float)PreverbWidthSlider.Value, PreverbWetOnly.IsChecked == true);

    private NoiseSettings GetNoiseSettings() => new(GateToggle.IsChecked == true,
        (float)GateThresholdSlider.Value, (int)GateHoldSlider.Value, (int)GateReleaseSlider.Value,
        DenoiseToggle.IsChecked == true, (float)NoiseFloorSlider.Value, (float)NoiseReductionSlider.Value);

    private bool IsCableLoop => Microphone?.CablePair is string pair && pair == Output?.CablePair;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closing)
            return;
        SetDetail("");
        if (session is not null)
        {
            await RunAudioChange(StopAudioAsync, "Zatrzymywanie…");
        }
        else if (Microphone is { } microphone && Output is { } output && !IsCableLoop)
        {
            FilterSettings parameters = GetFilterSettings();
            await RunAudioChange(async () =>
            {
                var active = await Task.Run(() => AudioSession.Start(microphone.Id, output.Id, parameters));
                session = active;
                RefreshEffect();
                if (MonitorToggle.IsChecked == true)
                    await SetMonitorAsync(active);
            }, "Uruchamianie…");
        }
    }

    private Task RunAudioChange(Func<Task> change, string? state = null)
    {
        busy = true;
        transition = state;
        UpdateControls();
        currentChange = CompleteAudioChangeAsync(change);
        return currentChange;
    }

    private async Task CompleteAudioChangeAsync(Func<Task> change)
    {
        try
        {
            await change();
        }
        catch (Exception ex)
        {
            SetDetail($"Sprawdź wybrane urządzenia i dostęp Windows do mikrofonu. {ex.Message}", true);
        }
        finally
        {
            busy = false;
            transition = null;
            UpdateControls();
        }
    }

    private async Task StopAudioAsync()
    {
        AudioSession? active = session;
        session = null;
        if (active is not null)
            await Task.Run(active.Dispose);
        shownInput = shownOutput = 0;
    }

    private async void UpdateMeters(object? sender, EventArgs e)
    {
        (float input, float output) = session?.TakePeaks() ?? (0f, 0f);
        shownInput = Math.Max(input, shownInput * .815f);
        shownOutput = Math.Max(output, shownOutput * .815f);
        SetMeter(InputMeter, InputDb, shownInput);
        SetMeter(OutputMeter, OutputDb, shownOutput);

        if (busy || closing || session is not { } active)
            return;
        if (active.Failure is { } failure)
        {
            await RunAudioChange(StopAudioAsync, "Zatrzymywanie…");
            SetDetail($"Przerwano dźwięk. {failure} Odśwież listę urządzeń i uruchom ponownie.", true);
        }
        else if (active.MonitorFailure is { } monitorError)
        {
            DisableMonitor();
            await RunAudioChange(() => SetMonitorAsync(active));
            SetDetail($"Odsłuch został wyłączony. {monitorError}", true);
        }
    }

    private static void SetMeter(LevelMeter meter, System.Windows.Controls.TextBlock label, float peak)
    {
        double db = peak > .00025f ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        meter.NormalizedLevel = Math.Clamp((db + 60) / 60, 0, 1);
        label.Text = double.IsNegativeInfinity(db) ? "−∞ dBFS" : $"{db:0.0} dBFS";
    }

    private void UpdateControls()
    {
        bool running = session is not null;
        bool canEditRoute = !running && !busy && !closing;
        MicrophoneBox.IsEnabled = canEditRoute && MicrophoneBox.HasItems;
        OutputBox.IsEnabled = canEditRoute && OutputBox.HasItems;
        RefreshButton.IsEnabled = canEditRoute;
        MonitorBox.IsEnabled = !busy && !closing && MonitorBox.HasItems;
        MonitorToggle.IsEnabled = !busy && !closing && Monitor is not null;
        StartButton.IsEnabled = !busy && !closing && (running || (Microphone is not null && Output is not null && !IsCableLoop));

        StatusText.Text = transition ?? (running ? "Na żywo" : Microphone is null || Output is null ? "Wybierz urządzenia" : "Gotowy");
        StatusDot.Fill = Brush(running && transition is null ? "MintBrush" : "MutedBrush");
        StatusText.Foreground = Brush(running && transition is null ? "MintBrush" : "SecondaryBrush");
        StartText.Text = running ? "Zatrzymaj mikrofon" : "Uruchom mikrofon";
        StartGlyph.Data = running ? Stop : Play;
        System.Windows.Automation.AutomationProperties.SetName(StartButton, StartText.Text);
        AudioInfo.Text = session is { } active ? $"{(active.SampleRate / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)} kHz  ·  WASAPI" : "";

        if (IsCableLoop)
            OutputHint.Text = "Wybierz w aplikacji fizyczny mikrofon, aby uniknąć zapętlenia sygnału.";
        else if (Output?.CablePair == "Filtr Mikrofonu")
            OutputHint.Text = "W Discordzie wybierz Filtr Mikrofonu Microphone jako wejście.";
        else if (Output?.CablePair is { } cable && devices?.Inputs.Any(device => device.CablePair == cable) == true)
            OutputHint.Text = $"W Discordzie ustaw {cable} Output jako urządzenie wejściowe.";
        else if (devices?.Outputs.Any(device => device.CablePair is not null) == true)
            OutputHint.Text = "Wybierz CABLE Input, żeby przesłać dźwięk do Discorda.";
        else
            OutputHint.Text = "Do przesłania dźwięku do Discorda przyda się wirtualne wyjście, np. VB-CABLE.";
    }

    private void SetDetail(string text, bool problem = false)
    {
        StatusDetail.Text = text;
        StatusDetail.Foreground = Brush(problem ? "ProblemBrush" : "MutedBrush");
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closedAudio)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        meterTimer.Stop();
        UpdateControls();
        Preferences.Save(new Preferences(Microphone?.Id, Output?.Id, Monitor?.Id, SelectedFilter,
            MixSlider.Value, InputSlider.Value, OutputSlider.Value,
            BeforeMixSlider.Value, (int)BeforeTimeSlider.Value,
            RoomToggle.IsChecked == true, 100, VoiceAmbience.DefaultRoomMilliseconds,
            EchoToggle.IsChecked == true, EchoMixSlider.Value, (int)EchoTimeSlider.Value, GetReverbSettings(), GetPreverbSettings(), 1, GetNoiseSettings()));
        _ = FinishCloseAsync();
    }

    private async Task FinishCloseAsync()
    {
        // Yield even when there is no stream, so the original Close call can return.
        await Dispatcher.Yield();
        if (currentChange is not null)
            await currentChange;
        try
        {
            await StopAudioAsync();
        }
        finally
        {
            closedAudio = true;
            Close();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
}
