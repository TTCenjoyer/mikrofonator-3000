using System.IO;
namespace FiltrMikrofonu.Audio;

internal static class AudioDiagnostics
{
    private static readonly object mutex=new();
    private static readonly AudioErrorThrottle errors=new();
    internal static string LogPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FiltrMikrofonu","audio.log");
    internal static void Close(IDisposable? resource,string name)
    {
        try{resource?.Dispose();}
        catch(Exception ex){Write("Audio cleanup failed: "+name,ex);}
    }
    internal static void Write(string message,Exception? exception=null)
    {
        try
        {
            lock(mutex)
            {
                string? entry=errors.Format(message,exception,DateTimeOffset.UtcNow);
                if(entry is null)return;
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if(File.Exists(LogPath) && new FileInfo(LogPath).Length>2_000_000)
                    File.Move(LogPath,LogPath+".previous",overwrite:true);
                File.AppendAllText(LogPath,$"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {entry}\n");
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){ }
    }
}

internal static class AudioReconnect
{
    internal static async Task<T> CreateAsync<T>(Func<T> open,Action<T> dispose,CancellationToken token,
        Func<int,CancellationToken,Task>? wait=null, int firstAttempt=0) where T:class
    {
        wait??=static (attempt,cancellation)=>Task.Delay(attempt switch{0=>200,1=>600,2=>1500,3=>3000,_=>5000},cancellation);
        for(int attempt=Math.Clamp(firstAttempt,0,4);;attempt=Math.Min(attempt+1,4))
        {
            token.ThrowIfCancellationRequested();
            await wait(attempt,token);
            token.ThrowIfCancellationRequested();
            T candidate;
            try { candidate=await Task.Run(open); }
            catch(Exception ex){AudioDiagnostics.Write($"Reconnect failed; retry backoff stage={attempt}",ex);continue;}
            // Opening WASAPI cannot be canceled halfway. Dispose a late result after Stop/Close.
            if(token.IsCancellationRequested){dispose(candidate);token.ThrowIfCancellationRequested();}
            return candidate;
        }
    }
}

// Used under AudioDiagnostics' lock; retains the first full stack, then summarizes repeats.
internal sealed class AudioErrorThrottle
{
    private sealed record Entry(DateTimeOffset Last, int Repeats);
    private readonly Dictionary<string,Entry> seen=new();
    internal string? Format(string message,Exception? error,DateTimeOffset now)
    {
        bool isError=error is not null || message.Contains("failure",StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed",StringComparison.OrdinalIgnoreCase) || message.Contains("unexpectedly",StringComparison.OrdinalIgnoreCase);
        string full=message+(error is null?"":"\n"+error);
        if(!isError)return full;
        string key=full;
        if(seen.TryGetValue(key,out var previous))
        {
            int repeats=previous.Repeats+1;
            if(now-previous.Last<TimeSpan.FromSeconds(30))
            {seen[key]=previous with{Repeats=repeats};return null;}
            seen[key]=new(now,0);
            string caption=message.Split('\n')[0].TrimEnd('\r');
            return $"Repeated audio error ({repeats} repeats since previous entry): {caption}"+
                (error is null?"":$" — {error.GetType().Name}: {error.Message}");
        }
        if(seen.Count>=64)seen.Clear();
        seen[key]=new(now,0);return full;
    }
}
