using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace FiltrMikrofonu.Audio;

internal sealed record AudioDevice(string Id, string Name)
{
    public string? CablePair
    {
        get
        {
            if (Name.Contains("Filtr Mikrofonu", StringComparison.OrdinalIgnoreCase))
                return "Filtr Mikrofonu";
            if (!Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                return null;

            int end = Name.IndexOf(" (", StringComparison.Ordinal);
            string label = end < 0 ? Name : Name[..end];
            if (label.EndsWith(" Input", StringComparison.OrdinalIgnoreCase))
                return label[..^6];
            if (label.EndsWith(" Output", StringComparison.OrdinalIgnoreCase))
                return label[..^7];
            return null;
        }
    }

    public bool IsVirtualMicrophone => CablePair is not null ||
        Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
}

internal sealed record AudioDevices(AudioDevice[] Inputs, AudioDevice[] Outputs, string? DefaultInput, string? DefaultOutput)
{
    public static AudioDevices Read()
    {
        using var devices = new MMDeviceEnumerator();
        return new AudioDevices(
            ReadFlow(devices, DataFlow.Capture), ReadFlow(devices, DataFlow.Render),
            DefaultId(devices, DataFlow.Capture), DefaultId(devices, DataFlow.Render));
    }

    private static AudioDevice[] ReadFlow(MMDeviceEnumerator devices, DataFlow flow)
    {
        var endpoints = new List<AudioDevice>();
        foreach (MMDevice endpoint in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (endpoint)
                endpoints.Add(new AudioDevice(endpoint.ID, endpoint.FriendlyName));
        }
        return endpoints.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string? DefaultId(MMDeviceEnumerator devices, DataFlow flow)
    {
        try
        {
            using var endpoint = devices.GetDefaultAudioEndpoint(flow, Role.Console);
            return endpoint?.ID;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
