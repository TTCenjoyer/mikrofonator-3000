using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FiltrMikrofonu.Audio;

namespace FiltrMikrofonu;

public partial class MainWindow
{
    private sealed record SavedFilterChoice(string Name, FilterPreset? Factory = null);
    private FrameworkElement CreateFilterPresetBar(string key)
    {
        var panel = new StackPanel { Margin = new Thickness(0,0,0,16) };
        panel.Children.Add(new TextBlock { Text = "Presety tego filtra", Foreground = (Brush)FindResource("SecondaryBrush"), FontSize = 11 });
        var choices = new ComboBox { Style = (Style)FindResource("DevicePicker"), Margin = new Thickness(0,5,0,7) };
        Automation(choices, "Zapisane presety — " + key);
        var name = new TextBox { Padding = new Thickness(8), Background = (Brush)FindResource("FieldBrush"),
            Foreground = (Brush)FindResource("InkBrush"), BorderBrush = (Brush)FindResource("LineBrush"), MaxLength = 80,
            ToolTip = "Nazwa nowego presetu lub nazwa do nadpisania" };
        Automation(name, "Nazwa presetu — " + key);
        var status = new TextBlock { Text = "Wczytanie zmienia ustawienia tylko tego filtra.", FontSize = 10,
            Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,5,0,0) };
        void Reload(string? select = null)
        {
            var items = new[] { new SavedFilterChoice("Wybierz preset") }
                .Concat(VoicePresets.Names.Where(p=>VoicePresets.Uses(key,VoicePresets.Settings(p.Id)))
                    .Select(p=>new SavedFilterChoice(p.Name+" (fabryczny)",new(Manual:VoicePresets.Settings(p.Id)))))
                .Concat(filterPresetStore.Names(key).Select(n => new SavedFilterChoice(n))).ToArray();
            choices.ItemsSource = items;
            choices.SelectedItem = items.FirstOrDefault(i => i.Name == select) ?? items[0];
        }
        choices.SelectionChanged += (_, _) =>
        {
            if (choices.SelectedIndex > 0 && choices.SelectedItem is SavedFilterChoice selected) name.Text = selected.Factory is null ? selected.Name : selected.Name.Replace(" (fabryczny)", " — własny");
        };
        panel.Children.Add(choices);
        panel.Children.Add(new TextBlock { Text = "Nazwa do zapisu", FontSize = 10, Foreground = (Brush)FindResource("SecondaryBrush"), Margin = new Thickness(0,0,0,4) });
        panel.Children.Add(name);
        var actions = new WrapPanel { Margin = new Thickness(0,7,0,0) };
        foreach (string action in new[] { "Wczytaj", "Zapisz", "Usuń" })
        {
            var button = new Button { Content = action, Padding = new Thickness(9,5,9,5), Margin = new Thickness(0,0,6,4),
                Background = (Brush)FindResource("FieldBrush"), Foreground = (Brush)FindResource("InkBrush"), BorderBrush = (Brush)FindResource("LineBrush") };
            Automation(button, action + " preset — " + key);
            button.Click += (_, _) =>
            {
                try
                {
                    if (action == "Zapisz")
                    {
                        string title = name.Text.Trim();
                        if(title.EndsWith(" (fabryczny)",StringComparison.Ordinal)) throw new ArgumentException("Nadaj własnemu presetowi nazwę bez dopisku (fabryczny).");
                        bool exists = filterPresetStore.Names(key).Contains(title);
                        filterPresetStore.Save(key, title, CaptureFilterPreset(key));
                        Reload(title); status.Text = exists ? "Nadpisano preset: " + title : "Zapisano preset: " + title;
                    }
                    else
                    {
                        if (choices.SelectedIndex <= 0 || choices.SelectedItem is not SavedFilterChoice selected)
                        { status.Text = "Najpierw wybierz zapisany preset."; return; }
                        if (action == "Wczytaj")
                        {
                            var saved = selected.Factory ?? filterPresetStore.Get(key, selected.Name);
                            if (saved is null) { Reload(); status.Text = "Preset nie jest już dostępny."; return; }
                            ApplyFilterPreset(key, saved); status.Text = "Wczytano preset: " + selected.Name;
                        }
                        else
                        {
                            if(selected.Factory is not null){status.Text="Preset fabryczny możesz zapisać pod własną nazwą.";return;}
                            filterPresetStore.Delete(key, selected.Name); Reload(); name.Clear(); status.Text = "Usunięto preset: " + selected.Name;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { status.Text = "Nie udało się wykonać operacji. " + ex.Message; }
            };
            actions.Children.Add(button);
        }
        panel.Children.Add(actions); panel.Children.Add(status);
        try { Reload(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { status.Text = "Nie udało się odczytać presetów. " + ex.Message; }
        return panel;
    }
    private static void Automation(DependencyObject control, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(control, name);

    private FilterPreset CaptureFilterPreset(string key) => key switch
    {
        "Preverb" => new(Room: GetPreverbSettings(), Time: BeforeTimeSlider.Value, Level: BeforeMixSlider.Value),
        "Reverb" => new(Room: GetReverbSettings()),
        "Echo" => new(Time: EchoTimeSlider.Value, Level: EchoMixSlider.Value),
        "Gate" or "Denoise" => new(Noise: GetNoiseSettings()),
        _ => new(Manual: GetManualSettings())
    };

    private void ApplyFilterPreset(string key, FilterPreset saved)
    {
        applyingPreset = true;
        try
        {
            var m = saved.Manual;
            switch (key)
            {
                case "Eq" when m is not null: EqEditor.Settings = m.Equalizer ?? new(); break;
                case "HighPass" when m is not null:
                    HighPassSlider.Value = m.HighPassHz;
                    SlopeBox.SelectedIndex = m.HighPassSlope switch { 24 => 1, 48 => 2, 6 => 3, _ => 0 }; break;
                case "Distortion" when m is not null:
                    DriveSlider.Value = m.DriveDb; DistortionMixSlider.Value = m.DistortionMix;
                    DistortionOutputSlider.Value = m.DistortionOutputDb; HardClippingToggle.IsChecked = m.HardClipping;
                    DistortionTypeBox.SelectedIndex = m.MediumOverdrive ? 1 : 0;
                    OverdriveAmountSlider.Value = m.OverdriveAmount; OverdriveOutputSlider.Value = m.OverdriveOutput; break;
                case "Normalizer" when m is not null: NormalizeSlider.Value = m.NormalizeDb; MaxGainSlider.Value = m.MaxGainDb; break;
                case "LowPass" when m is not null:
                    LowPassSlider.Value=m.LowPassHz;LowPassSlopeBox.SelectedIndex=m.LowPassSlope switch{24=>1,48=>2,_=>0};break;
                case "Compression" when m is not null:
                    CompressionSlider.Value=m.CompressionMix;CompressorThresholdSlider.Value=m.CompressorThreshold;
                    CompressorRatioSlider.Value=m.CompressorRatio;CompressorAttackSlider.Value=m.CompressorAttack;
                    CompressorReleaseSlider.Value=m.CompressorRelease;CompressorMakeupSlider.Value=m.CompressorMakeup;break;
                case "Pitch" when m is not null: PitchSlider.Value=m.PitchSemitones;PitchMixSlider.Value=m.PitchMix;break;
                case "Preverb" when saved.Room is not null:
                    SetRoomPreset("Preverb", saved.Room); BeforeTimeSlider.Value = saved.Time; BeforeMixSlider.Value = saved.Level; break;
                case "Reverb" when saved.Room is not null: SetRoomPreset("Reverb", saved.Room); break;
                case "Echo": EchoTimeSlider.Value = saved.Time; EchoMixSlider.Value = saved.Level; break;
                case "Gate" when saved.Noise is { } gate:
                    GateThresholdSlider.Value = gate.GateThreshold; GateHoldSlider.Value = gate.GateHold; GateReleaseSlider.Value = gate.GateRelease; break;
                case "Denoise" when saved.Noise is { } noise:
                    NoiseFloorSlider.Value = noise.NoiseFloor; NoiseReductionSlider.Value = noise.Reduction;
                    NoiseModeBox.SelectedIndex=noise.NeuralEnabled?0:1;NeuralStrengthSlider.Value=noise.NeuralStrength;
                    AutomaticVoiceGateToggle.IsChecked=noise.AutomaticVoiceGate;break;
                default: throw new ArgumentException("Preset nie zawiera ustawień tego filtra.");
            }
            if (saved.Manual is not null)
            {
                FilterBox.SelectedIndex = -1; PresetState.Text = "Własne ustawienia filtrów";
            }
        }
        finally { applyingPreset = false; }
        RefreshEffect();
    }
    private void SetRoomPreset(string prefix, ReverbSettings room)
    {
        foreach (var (suffix, value) in new (string, float)[] { ("Size", room.RoomSize), ("PreDelay", room.PreDelay),
            ("Decay", room.Reverberance), ("Damping", room.Damping), ("Low", room.ToneLow), ("High", room.ToneHigh),
            ("Wet", room.WetGain), ("Dry", room.DryGain), ("Width", room.StereoWidth) })
            ((Slider)FindName(prefix + suffix + "Slider")).Value = value;
        ((CheckBox)FindName(prefix + "WetOnly")).IsChecked = room.WetOnly;
    }
}
