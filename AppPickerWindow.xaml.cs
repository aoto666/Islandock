using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Islandock;

/// <summary>
/// PC に入っているアプリを一覧表示し、チェックしたものを Dock に追加する画面。
/// 検索でしぼり込める。アイコンは画面を開いてから少しずつ読み込む（開くのを速くするため）。
/// </summary>
public partial class AppPickerWindow : Window
{
    public sealed class Candidate : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        private bool _isSelected;

        public required AppInfo App { get; init; }
        public required bool AlreadyAdded { get; init; }
        public string Name => App.Name;
        public bool CanAdd => !AlreadyAdded;
        public string Note => AlreadyAdded ? "追加済み" : App.Source == "デスクトップ" ? "デスクトップ" : "";

        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); SelectionChanged?.Invoke(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public Action? SelectionChanged { get; set; }
    }

    private readonly List<Candidate> _candidates;
    private readonly ICollectionView _view;

    /// <summary>「追加」を押したときに選ばれていたアプリ</summary>
    public List<DockEntry> SelectedEntries { get; } = new();

    public AppPickerWindow(IEnumerable<DockEntry> existing)
    {
        InitializeComponent();
        var existingPaths = new HashSet<string>(existing.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        var existingNames = new HashSet<string>(existing.Select(e => e.Name), StringComparer.CurrentCultureIgnoreCase);

        _candidates = InstalledApps.FindAll()
            .Select(app => new Candidate
            {
                App = app,
                AlreadyAdded = existingPaths.Contains(app.Path) || existingNames.Contains(app.Name),
            })
            .ToList();
        foreach (var c in _candidates) c.SelectionChanged = UpdateCount;

        _view = CollectionViewSource.GetDefaultView(_candidates);
        _view.Filter = o => o is Candidate c &&
                            (SearchBox.Text.Length == 0 || c.Name.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        AppList.ItemsSource = _view;
        UpdateCount();

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            LoadIconsGradually();
        };
    }

    /// <summary>アイコンを1つずつ、画面の操作を邪魔しないタイミングで読み込む</summary>
    private void LoadIconsGradually()
    {
        int index = 0;
        void Next()
        {
            if (index >= _candidates.Count || !IsLoaded) return;
            var c = _candidates[index++];
            c.Icon = IconHelper.GetIcon(c.App.Path, 64);
            Dispatcher.BeginInvoke(Next, DispatcherPriority.Background);
        }
        Dispatcher.BeginInvoke(Next, DispatcherPriority.Background);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
    }

    private void UpdateCount()
    {
        int n = _candidates.Count(c => c.IsSelected);
        CountText.Text = n == 0 ? $"{_candidates.Count} 個のアプリ" : $"{n} 個を選択中";
        AddButton.Content = n == 0 ? "追加" : $"{n} 個を追加";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        SelectedEntries.AddRange(_candidates
            .Where(c => c.IsSelected && c.CanAdd)
            .Select(c => new DockEntry { Name = c.App.Name, Path = c.App.Path, ProcessName = c.App.ProcessName }));
        DialogResult = true;
    }
}
