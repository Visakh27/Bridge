using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace Bridge.Windows;

/// <summary>One photo in the Photos grid; <see cref="Thumb"/> fills in when its preview arrives.</summary>
public sealed class PhotoTile : INotifyPropertyChanged
{
    public PhotoTile(Bridge.Protocol.PhotoInfo info)
    {
        Info = info;
        var taken = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(info.TakenAt, 0, 253402300799999)).ToLocalTime();
        Tooltip = $"{info.Name}\n{taken:g} · {info.Width}×{info.Height} · {info.Size / 1024.0 / 1024.0:0.#} MB";
    }

    public Bridge.Protocol.PhotoInfo Info { get; }
    public string Tooltip { get; }

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
