using System.IO;
namespace FiltrMikrofonu.Audio;

internal static class AudioDiagnostics
{
    private static readonly object mutex=new();
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
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if(File.Exists(LogPath) && new FileInfo(LogPath).Length>2_000_000)
                    File.Move(LogPath,LogPath+".previous",overwrite:true);
                File.AppendAllText(LogPath,$"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{(exception is null?"":"\n"+exception)}\n");
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
