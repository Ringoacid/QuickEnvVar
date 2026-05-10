using System.ComponentModel;
using System.IO;

namespace QuickEnvVar;

public class PathEntry : INotifyPropertyChanged
{
    private bool _exists;
    private bool _isDuplicate;
    private bool _isNewlyAdded;

    public string Path { get; }

    public bool Exists
    {
        get => _exists;
        set { _exists = value; OnPropertyChanged(nameof(Exists)); }
    }

    public bool IsDuplicate
    {
        get => _isDuplicate;
        set { _isDuplicate = value; OnPropertyChanged(nameof(IsDuplicate)); }
    }

    /// <summary>
    /// 現在のセッション中に新しく追加されたエントリかどうか
    /// </summary>
    public bool IsNewlyAdded
    {
        get => _isNewlyAdded;
        set { _isNewlyAdded = value; OnPropertyChanged(nameof(IsNewlyAdded)); }
    }

    public PathEntry(string path)
    {
        Path = path;
        _exists = Directory.Exists(Environment.ExpandEnvironmentVariables(path));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
