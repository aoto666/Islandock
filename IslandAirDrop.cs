using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Islandock;

/// <summary>
/// Dynamic Island の「AirDrop」。
/// Windows の「近距離共有」（Nearby Sharing）を使って、近くの Windows PC にファイルを送る。
///  ・ファイルをアイランドにドラッグすると、AirDrop の受け皿が開く → 離すと共有画面が出る
///  ・AirDrop タブの丸をクリックすると、ファイルを選んで送れる
///  ・下の行に「受信」の状態（オフ / 自分のデバイスのみ / すべての人）を表示
/// </summary>
public partial class IslandWindow
{
    private const string CdpKey = @"Software\Microsoft\Windows\CurrentVersion\CDP";
    private static readonly Color AirBlue = Color.FromRgb(0x0A, 0x84, 0xFF);

    private readonly DispatcherTimer _dragLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private DataTransferManager? _share;
    private List<string> _pendingShare = new();
    private bool _dragOver, _expandedByDrag;

    private void InitAirDrop()
    {
        Log("ファイル共有 準備");
        // アイランド・上の細い帯・小さな丸のどこにファイルを落としてもよい
        foreach (var zone in new FrameworkElement[] { Island, HoverStrip, Bubble })
        {
            zone.AllowDrop = true;
            zone.DragEnter += OnAirDropDragEnter;
            zone.DragOver += OnAirDropDragOver;
            zone.DragLeave += OnAirDropDragLeave;
            zone.Drop += OnAirDropDrop;
        }

        // ドラッグがアイランドの外へ出たまま戻ってこなければ閉じる
        _dragLeaveTimer.Tick += (_, _) =>
        {
            _dragLeaveTimer.Stop();
            if (_dragOver) return;
            SetDropHighlight(false);
            if (_expandedByDrag) { _expandedByDrag = false; Collapse(); }
        };
    }

    private void OnAirDropTab(object sender, RoutedEventArgs e) => ShowTab(Tab.AirDrop);

    // ===================== ドラッグ & ドロップ =====================

    private static List<string> FilesIn(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.ToList()
            : new List<string>();

    private void OnAirDropDragEnter(object sender, DragEventArgs e)
    {
        Log($"DragEnter ({((FrameworkElement)sender).Name}) ファイル={e.Data.GetDataPresent(DataFormats.FileDrop)}");
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || !Features.ShowFileShare) return;
        _dragOver = true;
        _dragLeaveTimer.Stop();

        if (!_expanded)
        {
            _expandedByDrag = true;
            Expand(Tab.AirDrop);
        }
        else if (_tab != Tab.AirDrop)
        {
            ShowTab(Tab.AirDrop);
        }
        SetDropHighlight(true);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnAirDropDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && Features.ShowFileShare
            ? DragDropEffects.Copy : DragDropEffects.None;
        _dragOver = e.Effects != DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAirDropDragLeave(object sender, DragEventArgs e)
    {
        // 子要素の間を移るときにも呼ばれるので、少し待ってから判断する
        _dragOver = false;
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private void OnAirDropDrop(object sender, DragEventArgs e)
    {
        _dragOver = false;
        _expandedByDrag = false;
        _dragLeaveTimer.Stop();
        SetDropHighlight(false);
        e.Handled = true;

        if (!Features.ShowFileShare) return;
        var files = FilesIn(e.Data);
        Log($"Drop {files.Count} 個: {string.Join(", ", files)}");
        if (files.Count > 0) ShareFiles(files);
    }

    /// <summary>ドラッグ中は受け皿を青く光らせ、丸を波打たせる</summary>
    private void SetDropHighlight(bool on)
    {
        AirDropZone.BorderBrush = new SolidColorBrush(on ? AirBlue : Color.FromRgb(0x3A, 0x3A, 0x3C));
        AirDropZone.Background = new SolidColorBrush(on ? Color.FromRgb(0x0B, 0x1E, 0x36) : Color.FromRgb(0x14, 0x14, 0x16));
        AirDropTitle.Text = on ? Loc.T("離すと送信します") : Loc.T("ファイル共有");

        if (on)
        {
            var pulse = new DoubleAnimation(1, 1.28, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            AirDropRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            AirDropRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            AirDropRing.Opacity = 0.9;
        }
        else
        {
            AirDropRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AirDropRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AirDropRing.Opacity = 0.35;
        }
    }

    // ===================== ファイルを選んで送る =====================

    private void OnAirDropPick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Log("クリック → ファイル選択");
        var dialog = new OpenFileDialog { Title = Loc.T("共有するファイルを選択"), Multiselect = true };
        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0) ShareFiles(dialog.FileNames.ToList());
    }

    // ===================== 共有（近距離共有） =====================

    private void ShareFiles(List<string> paths)
    {
        // 近距離共有はファイルだけ送れる（フォルダーは不可）
        var files = paths.Where(File.Exists).ToList();
        int skipped = paths.Count - files.Count;
        if (files.Count == 0)
        {
            AirDropHint.Text = Loc.T("フォルダーは送れません。ファイルを選んでください（必要なら ZIP にまとめてください）");
            return;
        }

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_share == null)
            {
                _share = DataTransferManagerInterop.GetForWindow(hwnd);
                _share.DataRequested += OnShareDataRequested;
                Log("DataTransferManager 取得 OK");
            }
            _pendingShare = files;
            // 共有画面は前面のウィンドウから出す必要があるので、先にアイランドを前に出す
            Activate();
            NativeMethods.SetForegroundWindow(hwnd);
            Log($"ShowShareUIForWindow ({files.Count} 個, 前面={NativeMethods.GetForegroundWindow() == hwnd})");
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);

            AirDropHint.Text = (files.Count == 1 ? Path.GetFileName(files[0]) : Loc.F("{0} 個のファイル", files.Count))
                               + Loc.T(" — 送信先を選んでください")
                               + (skipped > 0 ? Loc.F("（フォルダー {0} 個は除外）", skipped) : "");
            Collapse(); // 共有画面と重ならないよう縮める
        }
        catch (Exception ex)
        {
            Log("共有画面エラー: " + ex);
            AirDropHint.Text = Loc.T("共有画面を開けませんでした: ") + ex.Message;
            MessageBox.Show(Loc.T("共有画面を開けませんでした。\n\n") + ex.Message, "Islandock");
        }
    }

    // ===================== スマホから受け取る =====================

    private readonly PhoneReceiver _phone = new();
    private readonly DispatcherTimer _phoneIdle = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _phoneHooked;

    /// <summary>QR を表示している間は、マウスが離れてもアイランドを閉じない（スマホで読み取れるように）</summary>
    private bool PinnedOpen => _expanded && _tab == Tab.AirDrop && _phone.Running;

    private void OnPhoneStart(object sender, RoutedEventArgs e)
    {
        if (!_phoneHooked)
        {
            _phoneHooked = true;
            _phone.Receiving += name => Dispatcher.BeginInvoke(() =>
            {
                RestartPhoneIdle();
                PhoneState.Text = Loc.T("受信中: ") + name;
                PhoneState.Foreground = new SolidColorBrush(AirBlue);
            });
            _phone.Received += path => Dispatcher.BeginInvoke(() => OnPhoneReceived(path));
            _phoneIdle.Tick += (_, _) => StopPhone(); // 15 分なにも来なければ自動で止める
        }

        try
        {
            _phone.SaveFolder = ReceivedFolder();
            _phone.Start();
            Log("スマホ受信 開始: " + _phone.Url);
        }
        catch (Exception ex)
        {
            Log("スマホ受信 エラー: " + ex);
            AirDropHint.Text = Loc.T("受信を始められませんでした: ") + ex.Message;
            return;
        }

        PhoneQr.Source = MakeQr(_phone.Url);
        PhoneUrl.Text = _phone.Url;
        PhoneState.Text = Loc.T("待機中…（15 分で自動停止）");
        PhoneState.Foreground = new SolidColorBrush(Green);
        RestartPhoneIdle();
        UpdateAirDropStatus();
    }

    private void OnPhoneStop(object sender, RoutedEventArgs e) => StopPhone();

    private void StopPhone()
    {
        _phoneIdle.Stop();
        _phone.Stop();
        Log("スマホ受信 停止");
        UpdateAirDropStatus();
    }

    private void RestartPhoneIdle()
    {
        _phoneIdle.Stop();
        _phoneIdle.Start();
    }

    private void OnPhoneReceived(string path)
    {
        Log("受信: " + path);
        string name = Path.GetFileName(path);
        PhoneState.Text = Loc.T("受信しました ✓ ") + name;
        PhoneState.Foreground = new SolidColorBrush(Green);

        var open = new Button
        {
            Content = Loc.T("表示"),
            Style = (Style)FindResource("PillButton"),
            Padding = new Thickness(12, 5, 12, 5),
        };
        open.Click += (_, _) =>
        {
            DismissAlert();
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")); } catch { }
        };
        ShowAlert("phone", CircleGlyph("", AirBlue), "ファイルを受信しました", name,
            right: open, width: 380, height: 56, seconds: 5);
    }

    private static BitmapImage MakeQr(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>うまく動かないときの調査用ログ（%APPDATA%\Islandock\share.log）</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "share.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
        }
    }

    private async void OnShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        Log("DataRequested 受信");
        var request = args.Request;
        var deferral = request.GetDeferral();
        try
        {
            var items = new List<IStorageItem>();
            foreach (var path in _pendingShare)
                items.Add(await StorageFile.GetFileFromPathAsync(path));

            request.Data.Properties.Title = items.Count == 1 ? items[0].Name : Loc.F("{0} 個のファイル", items.Count);
            request.Data.Properties.Description = Loc.T("Islandock ファイル共有");
            request.Data.SetStorageItems(items);
            Log("共有データ設定 OK");
        }
        catch (Exception ex)
        {
            Log("DataRequested エラー: " + ex);
            request.FailWithDisplayText(Loc.T("ファイルを読み込めませんでした: ") + ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ===================== 受信の状態・設定 =====================

    /// <summary>近距離共有の「受信できる相手」を Windows の設定から読む</summary>
    private void UpdateAirDropStatus()
    {
        int? policy = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CdpKey);
            if (key?.GetValue("NearShareChannelUserAuthzPolicy") is int v) policy = v;
        }
        catch
        {
        }

        (string text, Color dot) = policy switch
        {
            2 => ("PC間: すべての人", AirBlue),
            1 => ("PC間: 自分のみ", Green),
            0 => ("PC間: オフ", Color.FromRgb(0x8E, 0x8E, 0x93)),
            _ => ("PC間: 不明", Color.FromRgb(0x8E, 0x8E, 0x93)),
        };
        AirDropStatus.Text = Loc.T(text);
        AirDropStatus.ToolTip = Loc.T("Windows の近距離共有で、ほかの PC から受け取れる相手（「設定」で変更）");
        PhonePanel.Visibility = _phone.Running ? Visibility.Visible : Visibility.Collapsed;
        PhoneButton.Visibility = _phone.Running ? Visibility.Collapsed : Visibility.Visible;
        AirDropStatusDot.Fill = new SolidColorBrush(dot);
        if (!_dragOver) AirDropHint.Text = Loc.T("ファイルをここにドラッグ、またはクリックして選択");
    }

    private void OnAirDropSettings(object sender, RoutedEventArgs e)
    {
        Collapse();
        try { Process.Start(new ProcessStartInfo("ms-settings:crossdevice") { UseShellExecute = true }); } catch { }
    }

    private void OnAirDropOpenReceived(object sender, RoutedEventArgs e)
    {
        Collapse();
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ReceivedFolder()}\"")); } catch { }
    }

    /// <summary>近距離共有で受け取ったファイルの保存先（設定がなければ「ダウンロード」）</summary>
    private static string ReceivedFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CdpKey);
            if (key?.GetValue("NearShareFileSaveLocation") is string custom && Directory.Exists(custom)) return custom;
        }
        catch
        {
        }

        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out var ptr) == 0)
        {
            string path = Marshal.PtrToStringUni(ptr) ?? "";
            Marshal.FreeCoTaskMem(ptr);
            if (Directory.Exists(path)) return path;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
