using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace QuickEnvVar;

public partial class MainWindow : Window
{
    private static readonly string UserPathRegistryKey = @"Environment";
    private static readonly string SystemPathRegistryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>
    /// モザイクモードの ON/OFF（DependencyProperty: DataTemplate から参照可能）
    /// </summary>
    public static readonly DependencyProperty IsMosaicModeProperty =
        DependencyProperty.Register(nameof(IsMosaicMode), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false));

    public bool IsMosaicMode
    {
        get => (bool)GetValue(IsMosaicModeProperty);
        set => SetValue(IsMosaicModeProperty, value);
    }

    private readonly ObservableCollection<PathEntry> _userEntries = [];
    private readonly ObservableCollection<PathEntry> _systemEntries = [];
    private ICollectionView _userView = null!;
    private ICollectionView _systemView = null!;

    // 起動時のシステムPathスナップショット（モザイク用: 新規追加の判定に使用）
    private readonly HashSet<string> _initialSystemPaths;

    // 編集モード（システムPath）
    private bool _systemEditMode = false;
    private List<PathEntry>? _systemEditSnapshot = null;

    public string? PathToAdd
    {
        get;
        set
        {
            field = value;
            PathTextBox.Text = value ?? "";
        }
    }

    public MainWindow()
    {
        // 起動時のシステムPathを保存（モザイク機能で「新規追加」を判定するため）
        string systemRaw = ReadPathFromRegistry(RegistryHive.LocalMachine, SystemPathRegistryKey);
        _initialSystemPaths = new HashSet<string>(
            systemRaw.Split(';', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        InitializeComponent();
        AllowDrop = true;
        Drop += Window_Drop;

        _userView = CollectionViewSource.GetDefaultView(_userEntries);
        _userView.Filter = FilterPath;
        UserPathListBox.ItemsSource = _userView;

        _systemView = CollectionViewSource.GetDefaultView(_systemEntries);
        _systemView.Filter = FilterPath;
        SystemPathListBox.ItemsSource = _systemView;

        _userEntries.CollectionChanged += (_, _) => UpdateStatusBar();
        _systemEntries.CollectionChanged += (_, _) => UpdateStatusBar();

        RefreshPathLists();
    }

    private void UpdateStatusBar()
    {
        UserCountRun.Text = _userEntries.Count.ToString();
        SystemCountRun.Text = _systemEntries.Count.ToString();
    }

    // --- レジストリ読み書き ---

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private const int HWND_BROADCAST = 0xffff;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private static void NotifyEnvironmentChanged()
    {
        SendMessageTimeout(
            (IntPtr)HWND_BROADCAST,
            WM_SETTINGCHANGE,
            UIntPtr.Zero,
            "Environment",
            SMTO_ABORTIFHUNG,
            1000,
            out _);
    }

    private static string ReadPathFromRegistry(RegistryHive hive, string subKey)
    {
        using var key = (hive == RegistryHive.CurrentUser
            ? Registry.CurrentUser
            : Registry.LocalMachine).OpenSubKey(subKey);
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    private static void WriteUserPath(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserPathRegistryKey, writable: true)
            ?? throw new InvalidOperationException("ユーザーPathレジストリキーが開けません");
        key.SetValue("Path", value, RegistryValueKind.ExpandString);
        NotifyEnvironmentChanged();
    }

    // システムPathへの書き込みはUAC経由のPowerShellで行う
    private static bool WriteSystemPath(string value)
    {
        string escaped = value.Replace("'", "''");
        string script = $"Set-ItemProperty -Path 'HKLM:\\{SystemPathRegistryKey}' -Name 'Path' -Value '{escaped}' -Type ExpandString";
        bool result = RunElevatedPowerShell(script);
        if (result)
        {
            NotifyEnvironmentChanged();
        }
        return result;
    }

    // --- リスト更新 ---

    private void RefreshPathLists()
    {
        string userRaw = ReadPathFromRegistry(RegistryHive.CurrentUser, UserPathRegistryKey);
        string systemRaw = ReadPathFromRegistry(RegistryHive.LocalMachine, SystemPathRegistryKey);

        var userPaths = userRaw.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var systemPaths = systemRaw.Split(';', StringSplitOptions.RemoveEmptyEntries);

        _userEntries.Clear();
        foreach (var p in userPaths)
            _userEntries.Add(new PathEntry(p) { IsNewlyAdded = true });

        _systemEntries.Clear();
        foreach (var p in systemPaths)
        {
            var entry = new PathEntry(p);
            entry.IsNewlyAdded = !_initialSystemPaths.Contains(p);
            _systemEntries.Add(entry);
        }

        UpdateDuplicates();
        ExitSystemEditMode(apply: false);
    }

    private void UpdateDuplicates()
    {
        var userSet = _userEntries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var systemSet = _systemEntries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var e in _userEntries)
            e.IsDuplicate = systemSet.Contains(e.Path);
        foreach (var e in _systemEntries)
            e.IsDuplicate = userSet.Contains(e.Path);
    }

    private void RefreshPathListsButton_Click(object sender, RoutedEventArgs e) => RefreshPathLists();

    // --- フィルタ ---

    private string _filterText = "";

    private bool FilterPath(object obj)
    {
        if (string.IsNullOrEmpty(_filterText)) return true;
        return obj is PathEntry entry && entry.Path.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = SearchTextBox.Text;
        _userView.Refresh();
        _systemView.Refresh();
    }

    // --- パス入力 ---

    private void ValidateAndSetPath(string? path)
    {
        PathToAdd = path;
    }

    private bool ValidateSelectedPath()
    {
        if (PathToAdd is null || !Directory.Exists(Environment.ExpandEnvironmentVariables(PathToAdd)))
        {
            MessageBox.Show("有効なパスを選択してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        return true;
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog() == true)
            ValidateAndSetPath(dialog.FolderName);
    }

    private void PathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PathToAdd = PathTextBox.Text;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            string path = files[0];
            if (Directory.Exists(Environment.ExpandEnvironmentVariables(path)))
                ValidateAndSetPath(path);
        }
    }

    // --- Pathに追加 ---

    private void AddUserPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSelectedPath()) return;

        if (_userEntries.Any(en => en.Path.Equals(PathToAdd, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("すでにユーザーPathに含まれています。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string updated = BuildPathString(_userEntries) + ";" + PathToAdd;
        WriteUserPath(updated);
        RefreshPathLists();
        MessageBox.Show("ユーザーPathに追加しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddSystemPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSelectedPath()) return;

        if (_systemEntries.Any(en => en.Path.Equals(PathToAdd, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("すでにシステムPathに含まれています。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string updated = BuildPathString(_systemEntries) + ";" + PathToAdd;
        if (WriteSystemPath(updated))
        {
            RefreshPathLists();
            MessageBox.Show("システムPathに追加しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // --- 削除 ---

    private void DeletePathEntry(PathEntry entry, EnvironmentVariableTarget target)
    {
        if (MessageBox.Show($"以下のエントリをPathから削除しますか？\n\n{entry.Path}",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (target == EnvironmentVariableTarget.User)
        {
            _userEntries.Remove(entry);
            WriteUserPath(BuildPathString(_userEntries));
            UpdateDuplicates();
        }
        else
        {
            if (_systemEditMode)
            {
                _systemEntries.Remove(entry);
            }
            else
            {
                string updated = BuildPathString(_systemEntries.Where(en => en != entry));
                if (WriteSystemPath(updated))
                {
                    RefreshPathLists();
                }
            }
        }
    }

    // --- コンテキストメニュー ---

    private void PathListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        var item = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item != null)
            item.IsSelected = true;
        else
            e.Handled = true;
    }

    private void PathListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: PathEntry entry })
            OpenPathInExplorer(entry.Path);
    }

    private void PathListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (sender is not ListBox listBox || listBox.SelectedItem is not PathEntry entry) return;
        var target = listBox == UserPathListBox ? EnvironmentVariableTarget.User : EnvironmentVariableTarget.Machine;
        DeletePathEntry(entry, target);
    }

    private void UserPathListBoxMenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (UserPathListBox.SelectedItem is PathEntry entry) OpenPathInExplorer(entry.Path);
    }

    private void SystemPathListBoxMenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SystemPathListBox.SelectedItem is PathEntry entry) OpenPathInExplorer(entry.Path);
    }

    private void UserPathListBoxMenuItem_Copy_Click(object sender, RoutedEventArgs e)
    {
        if (UserPathListBox.SelectedItem is PathEntry entry) Clipboard.SetText(entry.Path);
    }

    private void SystemPathListBoxMenuItem_Copy_Click(object sender, RoutedEventArgs e)
    {
        if (SystemPathListBox.SelectedItem is PathEntry entry) Clipboard.SetText(entry.Path);
    }

    private void UserPathListBoxMenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (UserPathListBox.SelectedItem is PathEntry entry)
            DeletePathEntry(entry, EnvironmentVariableTarget.User);
    }

    private void SystemPathListBoxMenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SystemPathListBox.SelectedItem is PathEntry entry)
            DeletePathEntry(entry, EnvironmentVariableTarget.Machine);
    }

    // --- 並び替え（ユーザーPath） ---

    private void UserMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserPathListBox.SelectedItem is not PathEntry entry) return;
        int idx = _userEntries.IndexOf(entry);
        if (idx <= 0) return;
        _userEntries.Move(idx, idx - 1);
        WriteUserPath(BuildPathString(_userEntries));
        UserPathListBox.SelectedItem = entry;
    }

    private void UserMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserPathListBox.SelectedItem is not PathEntry entry) return;
        int idx = _userEntries.IndexOf(entry);
        if (idx < 0 || idx >= _userEntries.Count - 1) return;
        _userEntries.Move(idx, idx + 1);
        WriteUserPath(BuildPathString(_userEntries));
        UserPathListBox.SelectedItem = entry;
    }

    // --- 並び替え（システムPath・編集モード） ---

    private void SystemEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_systemEditMode)
        {
            // 適用
            string updated = BuildPathString(_systemEntries);
            if (WriteSystemPath(updated))
            {
                RefreshPathLists();
                MessageBox.Show("システムPathを更新しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // 失敗したらスナップショットに戻す
                RestoreSystemSnapshot();
            }
        }
        else
        {
            // 編集開始
            _systemEditSnapshot = [.. _systemEntries];
            _systemEditMode = true;
            SystemEditButton.Content = "適用（UAC）";
            SystemCancelButton.Visibility = Visibility.Visible;
            SystemMoveUpButton.IsEnabled = true;
            SystemMoveDownButton.IsEnabled = true;
        }
    }

    private void SystemCancelButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreSystemSnapshot();
        ExitSystemEditMode(apply: false);
    }

    private void SystemMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_systemEditMode || SystemPathListBox.SelectedItem is not PathEntry entry) return;
        int idx = _systemEntries.IndexOf(entry);
        if (idx <= 0) return;
        _systemEntries.Move(idx, idx - 1);
        SystemPathListBox.SelectedItem = entry;
    }

    private void SystemMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_systemEditMode || SystemPathListBox.SelectedItem is not PathEntry entry) return;
        int idx = _systemEntries.IndexOf(entry);
        if (idx < 0 || idx >= _systemEntries.Count - 1) return;
        _systemEntries.Move(idx, idx + 1);
        SystemPathListBox.SelectedItem = entry;
    }

    private void ExitSystemEditMode(bool apply)
    {
        _systemEditMode = false;
        _systemEditSnapshot = null;
        SystemEditButton.Content = "編集";
        SystemCancelButton.Visibility = Visibility.Collapsed;
        SystemMoveUpButton.IsEnabled = false;
        SystemMoveDownButton.IsEnabled = false;
    }

    private void RestoreSystemSnapshot()
    {
        if (_systemEditSnapshot is null) return;
        _systemEntries.Clear();
        foreach (var e in _systemEditSnapshot)
            _systemEntries.Add(e);
    }

    // --- エクスポート ---

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "テキストファイル (*.txt)|*.txt",
            FileName = $"PATH_backup_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"# PATH バックアップ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("## ユーザーPath");
        foreach (var entry in _userEntries)
            sb.AppendLine(entry.Path);
        sb.AppendLine();
        sb.AppendLine("## システムPath");
        foreach (var entry in _systemEntries)
            sb.AppendLine(entry.Path);

        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show($"エクスポートしました:\n{dialog.FileName}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // --- ヘルパー ---

    private static string BuildPathString(IEnumerable<PathEntry> entries)
        => string.Join(";", entries.Select(e => e.Path));

    private static void OpenPathInExplorer(string path)
    {
        string expandedPath = Environment.ExpandEnvironmentVariables(path);
        if (Directory.Exists(expandedPath))
            Process.Start(new ProcessStartInfo { FileName = expandedPath, UseShellExecute = true });
        else
            MessageBox.Show($"フォルダが見つかりません:\n{path}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool RunElevatedPowerShell(string script)
    {
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-EncodedCommand {encodedCommand}",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            var process = Process.Start(psi)!;
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                MessageBox.Show("操作に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("管理者権限の取得がキャンセルされました。", "キャンセル", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
