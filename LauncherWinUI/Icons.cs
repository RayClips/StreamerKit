using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace StreamerKit;

/// <summary>
/// Platform logos, held as base64 data URIs in icons.json rather than as loose image
/// files — one file to ship, and nothing to go missing next to the exe.
///
/// Regenerate it by base64-encoding a picture into {"name": "data:image/png;base64,..."}.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Payloads = Load();
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    private static Dictionary<string, string> Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icons.json");
            if (!File.Exists(path)) return new Dictionary<string, string>();

            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Decoded image for a key such as "twitch", or null if it isn't there.</summary>
    public static ImageSource? Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Cache.TryGetValue(name, out var cached)) return cached;

        var image = Decode(name);
        Cache[name] = image;
        return image;
    }

    private static ImageSource? Decode(string name)
    {
        if (!Payloads.TryGetValue(name, out var payload) || string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            // Strip the "data:image/png;base64," prefix if it's there.
            var comma = payload.IndexOf(',');
            var base64 = comma >= 0 ? payload[(comma + 1)..] : payload;
            var bytes = Convert.FromBase64String(base64);

            var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);

            var image = new BitmapImage();
            image.SetSource(stream);
            return image;
        }
        catch
        {
            return null;   // bad base64, or a format Windows has no codec for
        }
    }
}
