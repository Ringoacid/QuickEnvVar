using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // 編集モード（ユーザーPath）
    private bool _userEditMode = false;
    private List<PathEntry>? _userEditSnapshot = null;

    // Undo履歴。各要素は複数ターゲットへの書き込みをまとめた1単位
    private readonly Stack<List<UndoOp>> _undoStack = new();

    private record UndoOp(EnvironmentVariableTarget Target, string OldValue);

    // Windowsレジストリの Path 値の推奨上限
    private const int MaxPathLength = 2047;

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

    // --- 書き込みラッパー（長さ警告 + Undo記録） ---

    private static bool ConfirmPathLength(string built, string label)
    {
        if (built.Length <= MaxPathLength) return true;
        return MessageBox.Show(
            $"{label}Pathの長さが {built.Length} 文字となり、Windowsの推奨上限（{MaxPathLength}文字）を超えています。\n" +
            "古いPath項目が切り詰められる可能性があります。続行しますか？",
            "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private bool WriteUserPathChecked(string newValue, List<UndoOp>? batch = null)
    {
        if (!ConfirmPathLength(newValue, "ユーザー")) return false;
        string oldValue = ReadPathFromRegistry(RegistryHive.CurrentUser, UserPathRegistryKey);
        WriteUserPath(newValue);
        var op = new UndoOp(EnvironmentVariableTarget.User, oldValue);
        if (batch is not null) batch.Add(op);
        else PushUndo(op);
        return true;
    }

    private bool WriteSystemPathChecked(string newValue, List<UndoOp>? batch = null)
    {
        if (!ConfirmPathLength(newValue, "システム")) return false;
        string oldValue = ReadPathFromRegistry(RegistryHive.LocalMachine, SystemPathRegistryKey);
        if (!WriteSystemPath(newValue)) return false;
        var op = new UndoOp(EnvironmentVariableTarget.Machine, oldValue);
        if (batch is not null) batch.Add(op);
        else PushUndo(op);
        return true;
    }

    private void PushUndo(UndoOp op) => PushUndo([op]);

    private void PushUndo(List<UndoOp> ops)
    {
        if (ops.Count == 0) return;
        _undoStack.Push(ops);
        UpdateUndoButton();
    }

    private void UpdateUndoButton()
    {
        if (UndoButton != null)
            UndoButton.IsEnabled = _undoStack.Count > 0;
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        if (_userEditMode || _systemEditMode)
        {
            MessageBox.Show("編集モード中はUndoできません。先に適用またはキャンセルしてください。",
                "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ops = _undoStack.Pop();
        UpdateUndoButton();

        var succeededOps = new List<UndoOp>();

        // システム側を含む場合はUACが必要。失敗時は再プッシュして整合性を保つ
        foreach (var op in ops)
        {
            bool ok = op.Target == EnvironmentVariableTarget.User
                ? TryRestoreUserPath(op.OldValue)
                : WriteSystemPath(op.OldValue);
            if (!ok)
            {
                // 実行に成功した操作を除外した「残りの操作リスト」を再プッシュする
                var remainingOps = ops.Where(x => !succeededOps.Contains(x)).ToList();
                if (remainingOps.Count > 0)
                {
                    _undoStack.Push(remainingOps);
                    UpdateUndoButton();
                }
                RefreshPathLists();
                return;
            }
            succeededOps.Add(op);
        }

        RefreshPathLists();
    }

    private static bool TryRestoreUserPath(string value)
    {
        try { WriteUserPath(value); return true; }
        catch { return false; }
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
        ExitSystemEditMode();
        ExitUserEditMode();
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
        if (PathToAdd is null) return false;
        string trimmed = PathToAdd.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Directory.Exists(Environment.ExpandEnvironmentVariables(trimmed)))
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

        string cleanPath = PathToAdd!.Trim();

        if (_userEntries.Any(en => en.Path.Equals(cleanPath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("すでにユーザーPathに含まれています。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_userEditMode)
        {
            _userEntries.Add(new PathEntry(cleanPath) { IsNewlyAdded = true });
            UpdateDuplicates();
            return;
        }

        string existing = BuildPathString(_userEntries);
        string updated = string.IsNullOrEmpty(existing) ? cleanPath : existing + ";" + cleanPath;
        if (WriteUserPathChecked(updated))
        {
            RefreshPathLists();
            MessageBox.Show("ユーザーPathに追加しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AddSystemPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSelectedPath()) return;

        string cleanPath = PathToAdd!.Trim();

        if (_systemEntries.Any(en => en.Path.Equals(cleanPath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("すでにシステムPathに含まれています。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_systemEditMode)
        {
            _systemEntries.Add(new PathEntry(cleanPath) { IsNewlyAdded = true });
            UpdateDuplicates();
            return;
        }

        string existing = BuildPathString(_systemEntries);
        string updated = string.IsNullOrEmpty(existing) ? cleanPath : existing + ";" + cleanPath;
        if (WriteSystemPathChecked(updated))
        {
            RefreshPathLists();
            MessageBox.Show("システムPathに追加しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // --- 削除 ---

    private void DeletePathEntries(IReadOnlyList<PathEntry> entries, EnvironmentVariableTarget target)
    {
        if (entries.Count == 0) return;

        string prompt = entries.Count == 1
            ? $"以下のエントリをPathから削除しますか？\n\n{entries[0].Path}"
            : $"選択された {entries.Count} 件のエントリをPathから削除しますか？\n\n"
                + string.Join("\n", entries.Take(10).Select(en => "・" + en.Path))
                + (entries.Count > 10 ? $"\n…ほか {entries.Count - 10} 件" : "");

        if (MessageBox.Show(prompt, "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var toRemove = entries.ToHashSet();

        if (target == EnvironmentVariableTarget.User)
        {
            if (_userEditMode)
            {
                foreach (var en in entries) _userEntries.Remove(en);
                UpdateDuplicates();
            }
            else
            {
                string updated = BuildPathString(_userEntries.Where(en => !toRemove.Contains(en)));
                if (WriteUserPathChecked(updated))
                    RefreshPathLists();
            }
        }
        else
        {
            if (_systemEditMode)
            {
                foreach (var en in entries) _systemEntries.Remove(en);
                UpdateDuplicates();
            }
            else
            {
                string updated = BuildPathString(_systemEntries.Where(en => !toRemove.Contains(en)));
                if (WriteSystemPathChecked(updated))
                    RefreshPathLists();
            }
        }
    }

    private static List<PathEntry> GetSelectedEntries(ListBox listBox)
        => listBox.SelectedItems.OfType<PathEntry>().ToList();

    // --- コンテキストメニュー ---

    private void PathListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        var item = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item == null)
        {
            e.Handled = true;
            return;
        }
        // 既に複数選択されている項目を右クリックした場合は選択を保持する
        if (!item.IsSelected)
        {
            listBox.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void PathListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: PathEntry entry })
            OpenPathInExplorer(entry.Path);
    }

    private void PathListBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (sender is not ListBox listBox) return;
        var entries = GetSelectedEntries(listBox);
        if (entries.Count == 0) return;
        var target = listBox == UserPathListBox ? EnvironmentVariableTarget.User : EnvironmentVariableTarget.Machine;
        DeletePathEntries(entries, target);
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
        var paths = GetSelectedEntries(UserPathListBox).Select(en => en.Path).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private void SystemPathListBoxMenuItem_Copy_Click(object sender, RoutedEventArgs e)
    {
        var paths = GetSelectedEntries(SystemPathListBox).Select(en => en.Path).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private void UserPathListBoxMenuItem_Delete_Click(object sender, RoutedEventArgs e)
        => DeletePathEntries(GetSelectedEntries(UserPathListBox), EnvironmentVariableTarget.User);

    private void SystemPathListBoxMenuItem_Delete_Click(object sender, RoutedEventArgs e)
        => DeletePathEntries(GetSelectedEntries(SystemPathListBox), EnvironmentVariableTarget.Machine);

    // --- 並び替え（ユーザーPath・編集モード） ---

    private void UserMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_userEditMode || UserPathListBox.SelectedItem is not PathEntry entry) return;
        int idx = _userEntries.IndexOf(entry);
        if (idx <= 0) return;
        _userEntries.Move(idx, idx - 1);
        UserPathListBox.SelectedItem = entry;
    }

    private void UserMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_userEditMode || UserPathListBox.SelectedItem is not PathEntry entry) return;
        int idx = _userEntries.IndexOf(entry);
        if (idx < 0 || idx >= _userEntries.Count - 1) return;
        _userEntries.Move(idx, idx + 1);
        UserPathListBox.SelectedItem = entry;
    }

    private void UserEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_userEditMode)
        {
            string updated = BuildPathString(_userEntries);
            if (WriteUserPathChecked(updated))
            {
                ExitUserEditMode();
                RefreshPathLists();
                MessageBox.Show("ユーザーPathを更新しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        else
        {
            _userEditSnapshot = [.. _userEntries];
            _userEditMode = true;
            UserEditButton.Content = "適用";
            UserCancelButton.Visibility = Visibility.Visible;
            UserMoveUpButton.IsEnabled = true;
            UserMoveDownButton.IsEnabled = true;
        }
    }

    private void UserCancelButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreUserSnapshot();
        ExitUserEditMode();
    }

    private void ExitUserEditMode()
    {
        _userEditMode = false;
        _userEditSnapshot = null;
        UserEditButton.Content = "編集";
        UserCancelButton.Visibility = Visibility.Collapsed;
        UserMoveUpButton.IsEnabled = false;
        UserMoveDownButton.IsEnabled = false;
    }

    private void RestoreUserSnapshot()
    {
        if (_userEditSnapshot is null) return;
        _userEntries.Clear();
        foreach (var e in _userEditSnapshot)
            _userEntries.Add(e);
        UpdateDuplicates();
    }

    // --- 並び替え（システムPath・編集モード） ---

    private void SystemEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_systemEditMode)
        {
            string updated = BuildPathString(_systemEntries);
            if (WriteSystemPathChecked(updated))
            {
                ExitSystemEditMode();
                RefreshPathLists();
                MessageBox.Show("システムPathを更新しました。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            // 失敗時は編集モードを維持してユーザーに再操作の機会を残す
        }
        else
        {
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
        ExitSystemEditMode();
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

    private void ExitSystemEditMode()
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
        UpdateDuplicates();
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

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"エクスポートしました:\n{dialog.FileName}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ファイルのエクスポートに失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- インポート（バックアップ復元） ---

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_userEditMode || _systemEditMode)
        {
            MessageBox.Show("編集モード中はインポートできません。先に適用またはキャンセルしてください。",
                "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "テキストファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            Title = "PATH バックアップを開く"
        };
        if (dialog.ShowDialog() != true) return;

        string content;
        try { content = File.ReadAllText(dialog.FileName, Encoding.UTF8); }
        catch (Exception ex)
        {
            MessageBox.Show($"ファイルを読み込めませんでした:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var (userPaths, systemPaths) = ParseBackup(content);
        if (userPaths.Count == 0 && systemPaths.Count == 0)
        {
            MessageBox.Show("バックアップファイルから Path を読み取れませんでした。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var choice = MessageBox.Show(
            $"バックアップを読み込みました。\nユーザー: {userPaths.Count} 件 / システム: {systemPaths.Count} 件\n\n" +
            "[はい] 現在のPathを完全に置換\n" +
            "[いいえ] 既存にないPathのみ追加（マージ）\n" +
            "[キャンセル] 中止",
            "復元方法", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;
        bool replace = choice == MessageBoxResult.Yes;

        // 置換モードでバックアップが片方空のとき、現在のPathを消す前に確認
        if (replace)
        {
            if (userPaths.Count == 0 && _userEntries.Count > 0
                && MessageBox.Show("バックアップにユーザーPathが含まれていません。\n現在のユーザーPathを全て削除しますか？",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            if (systemPaths.Count == 0 && _systemEntries.Count > 0
                && MessageBox.Show("バックアップにシステムPathが含まれていません。\n現在のシステムPathを全て削除しますか？",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }

        var batch = new List<UndoOp>();

        // ユーザーPath
        var newUserList = replace
            ? userPaths
            : MergePaths(_userEntries.Select(en => en.Path), userPaths);
        string newUserStr = string.Join(";", newUserList);
        string currentUserStr = BuildPathString(_userEntries);
        bool userChanged = !string.Equals(newUserStr, currentUserStr, StringComparison.Ordinal);

        if (userChanged && !WriteUserPathChecked(newUserStr, batch))
        {
            RefreshPathLists();
            return;
        }

        // システムPath
        var newSystemList = replace
            ? systemPaths
            : MergePaths(_systemEntries.Select(en => en.Path), systemPaths);
        string newSystemStr = string.Join(";", newSystemList);
        string currentSystemStr = BuildPathString(_systemEntries);
        bool systemChanged = !string.Equals(newSystemStr, currentSystemStr, StringComparison.Ordinal);

        if (systemChanged && !WriteSystemPathChecked(newSystemStr, batch))
        {
            // ユーザー側だけ書き込まれた状態。確定済みの操作はUndoスタックへまとめてプッシュ
            PushUndo(batch);
            RefreshPathLists();
            return;
        }

        PushUndo(batch);
        RefreshPathLists();

        if (!userChanged && !systemChanged)
            MessageBox.Show("変更はありませんでした。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show("バックアップから復元しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static (List<string> User, List<string> System) ParseBackup(string content)
    {
        var user = new List<string>();
        var system = new List<string>();
        List<string>? current = null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("## ユーザーPath")) { current = user; continue; }
            if (line.StartsWith("## システムPath")) { current = system; continue; }
            if (line.StartsWith("#")) continue;
            current?.Add(line);
        }
        return (user, system);
    }

    private static List<string> MergePaths(IEnumerable<string> existing, IEnumerable<string> incoming)
    {
        var result = existing.ToList();
        var set = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
        foreach (var p in incoming)
            if (set.Add(p)) result.Add(p);
        return result;
    }

    // --- Undo ---

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z
            && Keyboard.Modifiers == ModifierKeys.Control
            && Keyboard.FocusedElement is not TextBoxBase)
        {
            Undo();
            e.Handled = true;
        }
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
