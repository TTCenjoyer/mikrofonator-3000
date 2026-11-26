using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Data;
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
    private bool applyingPreset;
    private readonly Dictionary<string, Window> filterWindows = new();
    private readonly FilterPresetStore filterPresetStore = new();
    private DateTimeOffset lastReconnect;
    private int successiveRecoveries;
    private CancellationTokenSource? recoveryCancellation;
    private bool recovering, microphoneRequested;
    private bool initialized, updatingDevices, busy, closing, closedAudio;
    private string? transition;
    private float shownInput, shownOutput;
    private string? preferredEchoReference, reportedEchoFailure, reportedNeuralFailure;

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
        NoiseModeBox.SelectedIndex=noise.NeuralEnabled?0:1;
        NeuralStrengthSlider.Value=noise.NeuralStrength;
        AutomaticVoiceGateToggle.IsChecked=noise.AutomaticVoiceGate;
        AecToggle.IsChecked=noise.EchoCancellationEnabled;
        AecDelaySlider.Value=noise.EchoDelayMs;
        preferredEchoReference=noise.EchoReferenceDevice;
        EchoMixSlider.Value = Math.Clamp(saved.EchoMix, 0, 100);
        EchoTimeSlider.Value = saved.EchoMilliseconds;
        var manual = saved.Manual ?? new ManualFilterSettings();
        SetManualControls(manual);
        EqEditor.SelectionChanged += (_, _) => RefreshEqControls();
        EqEditor.Changed += (_, _) => { if (initialized) Effect_Changed(EqToggle, new RoutedEventArgs()); };
        if (new[] { "Eq", "HighPass", "LowPass", "Distortion", "Normalizer", "Compression", "Pitch" }.Any(key=>VoicePresets.Uses(key,manual)))
        { FilterBox.SelectedIndex = -1; PresetState.Text = "Własne ustawienia filtrów"; }
        RefreshDevices(saved.Microphone, saved.Output, saved.Monitor);
        RefreshEqControls();
        foreach (var (key, toggle) in new (string, CheckBox)[] {
            ("Preverb", BeforeToggle), ("Reverb", RoomToggle), ("Echo", EchoToggle),
            ("Gate", GateToggle), ("Denoise", DenoiseToggle) })
        {
            if (toggle.Parent is Grid header && header.Parent is StackPanel section)
            {
                var presets = new Expander { Header = "Presety filtra", Content = CreateFilterPresetBar(key),
                    Foreground = (Brush)FindResource("SecondaryBrush"), Margin = new Thickness(0,0,0,12) };
                section.Children.Insert(1, presets);
            }
        }
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
        int captionColor = 0x000D0A08;
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
            var echoDevices=devices.Outputs.Where(device=>device.CablePair is null).ToArray();
            string? selectedEcho=(EchoReferenceBox.SelectedItem as AudioDevice)?.Id??preferredEchoReference;
            EchoReferenceBox.ItemsSource=echoDevices;
            EchoReferenceBox.SelectedItem=Find(echoDevices,selectedEcho)??Find(echoDevices,Monitor?.Id)??Find(echoDevices,devices.DefaultOutput)??echoDevices.FirstOrDefault();
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
        if (!initialized || applyingPreset) return;
        if (sender == FilterBox)
        {
            if (FilterBox.SelectedValue is VoiceFilter preset) ApplyPreset(preset);
        }
        else
        {
            if (sender is FrameworkElement element && IsSharedFilterControl(element))
            {
                applyingPreset = true;
                FilterBox.SelectedIndex = -1;
                applyingPreset = false;
                PresetState.Text = "Własne ustawienia filtrów";
            }
            RefreshEffect();
        }
    }

    private bool IsSharedFilterControl(FrameworkElement control) =>
        control == EqToggle || control == HighPassToggle || control == DistortionToggle || control == NormalizerToggle ||
        control == LowPassToggle || control == CompressionToggle || control == PitchToggle ||
        new FrameworkElement[] { EqPanel, HighPassPanel, DistortionPanel, NormalizerPanel, LowPassPanel, CompressionPanel, PitchPanel }
            .Any(panel => panel.IsAncestorOf(control));

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
        HighPassValue.Text = $"{HighPassSlider.Value:0} Hz";
        DriveValue.Text = $"{DriveSlider.Value:0} dB";
        DistortionMixValue.Text = $"{DistortionMixSlider.Value:0}%";
        DistortionOutputValue.Text = $"{DistortionOutputSlider.Value:0} dB";
        NormalizeValue.Text = $"{NormalizeSlider.Value:0} dB";
        MaxGainValue.Text = $"{MaxGainSlider.Value:0} dB";
        LowPassValue.Text = $"{LowPassSlider.Value:0} Hz";
        CompressorThresholdValue.Text = $"{CompressorThresholdSlider.Value:0} dB";
        CompressorRatioValue.Text = $"{CompressorRatioSlider.Value:0.0}:1";
        CompressorAttackValue.Text = $"{CompressorAttackSlider.Value:0} ms";
        CompressorReleaseValue.Text = $"{CompressorReleaseSlider.Value:0} ms";
        CompressorMakeupValue.Text = $"{CompressorMakeupSlider.Value:0} dB";
        PitchMixValue.Text = $"{PitchMixSlider.Value:0}%";
        CompressionValue.Text = $"{CompressionSlider.Value:0}%";
        PitchValue.Text = $"{PitchSlider.Value:+0.0;-0.0;0.0} półtonów";
        bool medium = DistortionTypeBox.SelectedIndex == 1;
        StandardDistortionPanel.Visibility = medium ? Visibility.Collapsed : Visibility.Visible;
        MediumOverdrivePanel.Visibility = medium ? Visibility.Visible : Visibility.Collapsed;
        OverdriveAmountValue.Text = $"{OverdriveAmountSlider.Value:0}";
        OverdriveOutputValue.Text = $"{OverdriveOutputSlider.Value:0}";
        NeuralStrengthValue.Text=$"{NeuralStrengthSlider.Value:0}%";
        AecDelayValue.Text=$"{AecDelaySlider.Value:0} ms";
        NeuralControls.Visibility=NoiseModeBox.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
        LegacyNoiseControls.Visibility=NoiseModeBox.SelectedIndex==0?Visibility.Collapsed:Visibility.Visible;
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
        BeforeToggle.IsChecked == true, (float)BeforeMixSlider.Value / 100, GetNoiseSettings(), GetManualSettings());

    private ReverbSettings GetReverbSettings() => new(
        (float)ReverbSizeSlider.Value, (float)ReverbPreDelaySlider.Value, (float)ReverbDecaySlider.Value, (float)ReverbDampingSlider.Value, (float)ReverbLowSlider.Value, (float)ReverbHighSlider.Value, (float)ReverbWetSlider.Value, (float)ReverbDrySlider.Value, (float)ReverbWidthSlider.Value, ReverbWetOnly.IsChecked == true);

    private ReverbSettings GetPreverbSettings() => new(
        (float)PreverbSizeSlider.Value, (float)PreverbPreDelaySlider.Value, (float)PreverbDecaySlider.Value, (float)PreverbDampingSlider.Value, (float)PreverbLowSlider.Value, (float)PreverbHighSlider.Value, (float)PreverbWetSlider.Value, (float)PreverbDrySlider.Value, (float)PreverbWidthSlider.Value, PreverbWetOnly.IsChecked == true);

    private NoiseSettings GetNoiseSettings() => new(GateToggle.IsChecked == true,
        (float)GateThresholdSlider.Value, (int)GateHoldSlider.Value, (int)GateReleaseSlider.Value,
        DenoiseToggle.IsChecked == true, (float)NoiseFloorSlider.Value, (float)NoiseReductionSlider.Value,
        NoiseModeBox.SelectedIndex==0,(float)NeuralStrengthSlider.Value,AutomaticVoiceGateToggle.IsChecked==true,
        AecToggle.IsChecked==true,(EchoReferenceBox.SelectedItem as AudioDevice)?.Id,(int)AecDelaySlider.Value);

    private void ResetEq_Click(object sender, RoutedEventArgs e)
    {
        EqEditor.Flatten();
    }

    private void RefreshEqControls()
    {
        if (EqHzBox is null) return;
        var point = EqEditor.SelectedPoint;
        EqHzBox.Text = point.Hertz.ToString("0.##", CultureInfo.GetCultureInfo("pl-PL"));
        EqDbBox.Text = point.Gain.ToString("0.##", CultureInfo.GetCultureInfo("pl-PL"));
        EqDeleteButton.IsEnabled = EqEditor.CanDelete;
        EqUndoButton.IsEnabled = EqEditor.CanUndo;
        EqRedoButton.IsEnabled = EqEditor.CanRedo;
        EqEditStatus.Text = $"Punkt {EqEditor.SelectedIndex + 1} z {EqEditor.PointCount} · 20–20 000 Hz · od −30 do +30 dB";
    }

    private void EqView_Changed(object sender, RoutedEventArgs e)
    {
        if (EqEditor is null || EqGridToggle is null) return;
        EqEditor.LinearScale = EqLinearScale.IsChecked == true;
        EqEditor.ShowGrid = EqGridToggle.IsChecked == true;
    }

    private void EqNumber_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        EditEqPoint(false); e.Handled = true;
    }

    private void EditEqPoint(bool add)
    {
        if (!double.TryParse(EqHzBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) ||
            !float.TryParse(EqDbBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var db) ||
            !double.IsFinite(hz) || !float.IsFinite(db) || hz < 20 || hz > 20000 || db < -30 || db > 30)
        { EqEditStatus.Text = "Wpisz częstotliwość 20–20 000 Hz i poziom od −30 do +30 dB."; return; }
        if (add) EqEditor.AddPoint(hz, db); else EqEditor.EditSelected(hz, db);
    }

    private void EqAction_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "Add": EditEqPoint(true); break;
            case "Edit": EditEqPoint(false); break;
            case "Delete": EqEditor.DeleteSelected(); break;
            case "Invert": EqEditor.Invert(); break;
            case "Undo": EqEditor.Undo(); break;
            case "Redo": EqEditor.Redo(); break;
            case "Previous": EqEditor.SelectPoint(EqEditor.SelectedIndex-1); break;
            case "Next": EqEditor.SelectPoint(EqEditor.SelectedIndex+1); break;
        }
    }

    private ManualFilterSettings GetManualSettings() => new(
        EqualizerEnabled: EqToggle.IsChecked == true, Equalizer: EqEditor.Settings,
        HighPassEnabled: HighPassToggle.IsChecked == true, HighPassHz: (float)HighPassSlider.Value,
        HighPassSlope: SlopeBox.SelectedIndex switch { 1=>24,2=>48,3=>6,_=>12 },
        DistortionEnabled: DistortionToggle.IsChecked == true, HardClipping: HardClippingToggle.IsChecked == true,
        DriveDb:(float)DriveSlider.Value, DistortionMix:(float)DistortionMixSlider.Value,DistortionOutputDb:(float)DistortionOutputSlider.Value,
        NormalizerEnabled:NormalizerToggle.IsChecked == true,NormalizeDb:(float)NormalizeSlider.Value,MaxGainDb:(float)MaxGainSlider.Value,
        CompressionEnabled:CompressionToggle.IsChecked==true,CompressionMix:(float)CompressionSlider.Value,
        MediumOverdrive:DistortionTypeBox.SelectedIndex==1,OverdriveAmount:(float)OverdriveAmountSlider.Value,OverdriveOutput:(float)OverdriveOutputSlider.Value,
        LowPassEnabled:LowPassToggle.IsChecked==true,LowPassHz:(float)LowPassSlider.Value,LowPassSlope:LowPassSlopeBox.SelectedIndex switch{1=>24,2=>48,_=>12},
        PitchEnabled:PitchToggle.IsChecked==true,PitchSemitones:(float)PitchSlider.Value,PitchMix:(float)PitchMixSlider.Value,
        CompressorThreshold:(float)CompressorThresholdSlider.Value,CompressorRatio:(float)CompressorRatioSlider.Value,
        CompressorAttack:(float)CompressorAttackSlider.Value,CompressorRelease:(float)CompressorReleaseSlider.Value,CompressorMakeup:(float)CompressorMakeupSlider.Value);

    private void SetManualControls(ManualFilterSettings m)
    {
        EqToggle.IsChecked=m.EqualizerEnabled;EqEditor.Settings=m.Equalizer??new();
        HighPassToggle.IsChecked=m.HighPassEnabled;HighPassSlider.Value=m.HighPassHz;
        SlopeBox.SelectedIndex=m.HighPassSlope switch{24=>1,48=>2,6=>3,_=>0};
        LowPassToggle.IsChecked=m.LowPassEnabled;LowPassSlider.Value=m.LowPassHz;
        LowPassSlopeBox.SelectedIndex=m.LowPassSlope switch{24=>1,48=>2,_=>0};
        DistortionToggle.IsChecked=m.DistortionEnabled;HardClippingToggle.IsChecked=m.HardClipping;DriveSlider.Value=m.DriveDb;
        DistortionMixSlider.Value=m.DistortionMix;DistortionOutputSlider.Value=m.DistortionOutputDb;
        DistortionTypeBox.SelectedIndex=m.MediumOverdrive?1:0;OverdriveAmountSlider.Value=m.OverdriveAmount;OverdriveOutputSlider.Value=m.OverdriveOutput;
        NormalizerToggle.IsChecked=m.NormalizerEnabled;NormalizeSlider.Value=m.NormalizeDb;MaxGainSlider.Value=m.MaxGainDb;
        CompressionToggle.IsChecked=m.CompressionEnabled;CompressionSlider.Value=m.CompressionMix;
        CompressorThresholdSlider.Value=m.CompressorThreshold;CompressorRatioSlider.Value=m.CompressorRatio;
        CompressorAttackSlider.Value=m.CompressorAttack;CompressorReleaseSlider.Value=m.CompressorRelease;CompressorMakeupSlider.Value=m.CompressorMakeup;
        PitchToggle.IsChecked=m.PitchEnabled;PitchSlider.Value=m.PitchSemitones;PitchMixSlider.Value=m.PitchMix;
    }

    private void ApplyPreset(VoiceFilter preset)
    {
        applyingPreset=true;
        try
        {
            SetManualControls(VoicePresets.Settings(preset));
            PresetState.Text=$"{Filters.First(f=>f.Id==preset).Title} — ustawienia filtrów zastosowane";
        }
        finally{applyingPreset=false;}
        RefreshEffect();
    }

    private void OpenFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } button) return;
        if (filterWindows.TryGetValue(key, out var existing)) { existing.Show(); existing.Activate(); return; }
        var panel = (FrameworkElement)FindName(key + "Panel");
        var toggle = (CheckBox)FindName(key + "Toggle");
        FilterPanelStorage.Children.Remove(panel);
        var window = new Window
        {
            Title = button.Content.ToString()!.Replace("  ›", ""), Owner = this,
            Width = key == "Eq" ? 860 : 430, SizeToContent = SizeToContent.Height,
            MaxHeight = 850, MinWidth = 370, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("BackgroundBrush"), Foreground = (Brush)FindResource("InkBrush"),
            FontFamily = FontFamily, FontSize = 13, ResizeMode = ResizeMode.CanResize
        };
        window.SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(window).Handle;
            int dark = 1, caption = 0x000D0A08;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        };
        var content = new StackPanel { Margin = new Thickness(24) };
        var top = new DockPanel { Margin = new Thickness(0,0,0,20) };
        var enabled = new CheckBox { ToolTip = "Włącz / wyłącz filtr", Style = (Style)FindResource("MonitorSwitch"), VerticalAlignment = VerticalAlignment.Center };
        enabled.SetBinding(CheckBox.IsCheckedProperty, new Binding("IsChecked") { Source = toggle, Mode = BindingMode.TwoWay });
        DockPanel.SetDock(enabled, Dock.Right); top.Children.Add(enabled);
        top.Children.Add(new TextBlock { Text = window.Title, FontSize = 19, FontWeight = FontWeights.SemiBold });
        content.Children.Add(top); content.Children.Add(CreateFilterPresetBar(key)); content.Children.Add(panel);
        window.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.Closed += (_, _) => { content.Children.Remove(panel); FilterPanelStorage.Children.Add(panel); filterWindows.Remove(key); };
        filterWindows.Add(key, window); window.Show();
    }

    private async void Aec_Changed(object sender,RoutedEventArgs e)
    {
        if(!initialized || updatingDevices || closing || applyingPreset)return;
        preferredEchoReference=(EchoReferenceBox.SelectedItem as AudioDevice)?.Id;
        RefreshEffect();
        if(!busy && session is { } active)await RunAudioChange(()=>ConfigureEchoReferenceAsync(active));
    }
    private async Task ConfigureEchoReferenceAsync(AudioSession active)
    {
        string? device=AecToggle.IsChecked==true?(EchoReferenceBox.SelectedItem as AudioDevice)?.Id:null;
        if(device==Output?.Id)device=null;
        try{await Task.Run(()=>active.SetEchoReference(device));}
        catch(Exception ex)
        {
            await Task.Run(()=>active.SetEchoReference(null));
            SetDetail("Nie udało się odczytać słuchawek do AEC. "+ex.Message,true);
        }
    }
    private async void DiscordNoisePreset_Click(object sender,RoutedEventArgs e)
    {
        if(busy || closing)return;
        applyingPreset=true;
        try
        {
            DenoiseToggle.IsChecked=true;NoiseModeBox.SelectedIndex=0;NeuralStrengthSlider.Value=100;
            AutomaticVoiceGateToggle.IsChecked=true;GateToggle.IsChecked=false;AecToggle.IsChecked=true;
            AecDelaySlider.Value=0;
            var discordHeadphones=devices?.Outputs.FirstOrDefault(device=>device.CablePair is null && device.Name.Contains("MAONO AU-AM200",StringComparison.OrdinalIgnoreCase));
            if(discordHeadphones is not null)EchoReferenceBox.SelectedItem=discordHeadphones;
            NormalizerToggle.IsChecked=true;NormalizeSlider.Value=-9;MaxGainSlider.Value=12;
            FilterBox.SelectedIndex=-1;PresetState.Text="Własne ustawienia filtrów";
        }
        finally{applyingPreset=false;}
        RefreshEffect();
        if(session is { } active)await RunAudioChange(()=>ConfigureEchoReferenceAsync(active));
        SetDetail("Profil inspirowany Discordem ustawiony. W Discordzie wybierz CABLE Output jako mikrofon.");
    }

    private bool IsCableLoop => Microphone?.CablePair is string pair && pair == Output?.CablePair;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if(recovering && !closing)
        {
            microphoneRequested=false;
            AudioDiagnostics.Write("User requested Stop during reconnect");
            recoveryCancellation?.Cancel();
            transition="Zatrzymywanie…";UpdateControls();return;
        }
        if (busy || closing)
            return;
        SetDetail("");
        if (session is not null)
        {
            microphoneRequested=false;
            AudioDiagnostics.Write("User requested Stop");
            await RunAudioChange(StopAudioAsync, "Zatrzymywanie…");
        }
        else if (Microphone is { } microphone && Output is { } output && !IsCableLoop)
        {
            microphoneRequested=true;successiveRecoveries=0;lastReconnect=default;
            AudioDiagnostics.Write("User requested Start");
            FilterSettings parameters = GetFilterSettings();
            await RunAudioChange(async () =>
            {
                AudioSession active;
                try { active = await Task.Run(() => AudioSession.Start(microphone.Id, output.Id, parameters)); }
                catch(Exception ex)
                {
                    AudioDiagnostics.Write("Initial start failed; reconnecting until Stop",ex);
                    transition="Łączenie ponownie…";
                    await RecoverAudioAsync(null);return;
                }
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
        catch(OperationCanceledException) when(!microphoneRequested || closing)
        {
            AudioDiagnostics.Write("Audio reconnect canceled by Stop/Close");
        }
        catch (Exception ex)
        {
            if(session is null)microphoneRequested=false;
            AudioDiagnostics.Write("Audio change failed",ex);
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
            AudioDiagnostics.Write("Audio session failure: "+failure);
            if(microphoneRequested)
                await RunAudioChange(()=>RecoverAudioAsync(active), "Łączenie ponownie…");
            else
                await RunAudioChange(StopAudioAsync,"Zatrzymywanie…");
        }
        else if (active.MonitorFailure is { } monitorError)
        {
            DisableMonitor();
            await RunAudioChange(() => SetMonitorAsync(active));
            SetDetail($"Odsłuch został wyłączony. {monitorError}", true);
        }
        if(active.EchoFailure is { } echoFailure && reportedEchoFailure!=echoFailure)
        {reportedEchoFailure=echoFailure;SetDetail("Eliminacja echa jest niedostępna: "+echoFailure,true);}
        if(active.NeuralFailure is { } neuralFailure && reportedNeuralFailure!=neuralFailure)
        {reportedNeuralFailure=neuralFailure;SetDetail("Redukcja AI jest niedostępna; działa tryb klasyczny. "+neuralFailure,true);}
    }

    private async Task RecoverAudioAsync(AudioSession? failed)
    {
        string? microphoneId=Microphone?.Id,outputId=Output?.Id;
        if(microphoneId is null || outputId is null)throw new InvalidOperationException("Nie wybrano urządzeń audio.");
        recovering=true;
        using var cancellation=new CancellationTokenSource();recoveryCancellation=cancellation;
        UpdateControls();
        try
        {
            if(session==failed)session=null;
            if(failed is not null)await Task.Run(failed.Dispose);
            if(DateTimeOffset.UtcNow-lastReconnect>TimeSpan.FromSeconds(30))successiveRecoveries=0;
            else successiveRecoveries=Math.Min(successiveRecoveries+1,4);
            cancellation.Token.ThrowIfCancellationRequested();
            var parameters=GetFilterSettings();
            var restored=await AudioReconnect.CreateAsync(()=>AudioSession.Start(microphoneId,outputId,parameters),
                candidate=>candidate.Dispose(),cancellation.Token,firstAttempt:successiveRecoveries);
            if(closing || !microphoneRequested){await Task.Run(restored.Dispose);return;}
            restored.SetFilter(GetFilterSettings());session=restored;
            if(MonitorToggle.IsChecked==true)await SetMonitorAsync(restored);
            if(closing || !microphoneRequested){await StopAudioAsync();return;}
            lastReconnect=DateTimeOffset.UtcNow;
            AudioDiagnostics.Write("Audio reconnected successfully");
            SetDetail("Przywrócono połączenie audio po błędzie urządzenia.");
        }
        catch
        {
            if(session is not null)await StopAudioAsync();
            throw;
        }
        finally{recovering=false;recoveryCancellation=null;}
    }

    private static void SetMeter(LevelMeter meter, System.Windows.Controls.TextBlock label, float peak)
    {
        double db = peak > .00025f ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        meter.NormalizedLevel = Math.Clamp((db + 60) / 60, 0, 1);
        label.Text = double.IsNegativeInfinity(db) ? "−∞ dBFS" : $"{db:0.0} dBFS";
    }

    private void UpdateControls()
    {
        bool running = session is not null || recovering;
        bool canEditRoute = !running && !busy && !closing;
        MicrophoneBox.IsEnabled = canEditRoute && MicrophoneBox.HasItems;
        OutputBox.IsEnabled = canEditRoute && OutputBox.HasItems;
        RefreshButton.IsEnabled = canEditRoute;
        EchoReferenceBox.IsEnabled=!busy && !closing && EchoReferenceBox.HasItems;
        AecToggle.IsEnabled=!busy && !closing;
        MonitorBox.IsEnabled = !busy && !closing && MonitorBox.HasItems;
        MonitorToggle.IsEnabled = !busy && !closing && Monitor is not null;
        StartButton.IsEnabled = !closing && (recovering || (!busy && (running || (Microphone is not null && Output is not null && !IsCableLoop))));

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
        microphoneRequested=false;recoveryCancellation?.Cancel();
        AudioDiagnostics.Write("Window closing");
        meterTimer.Stop();
        UpdateControls();
        Preferences.Save(new Preferences(Microphone?.Id, Output?.Id, Monitor?.Id, SelectedFilter,
            MixSlider.Value, InputSlider.Value, OutputSlider.Value,
            BeforeMixSlider.Value, (int)BeforeTimeSlider.Value,
            RoomToggle.IsChecked == true, 100, VoiceAmbience.DefaultRoomMilliseconds,
            EchoToggle.IsChecked == true, EchoMixSlider.Value, (int)EchoTimeSlider.Value, GetReverbSettings(), GetPreverbSettings(), 1, GetNoiseSettings(), GetManualSettings()));
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
