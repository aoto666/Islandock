using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Islandock;

/// <summary>表示する言語</summary>
public enum UiLanguage { Japanese, English, Hindi }

/// <summary>
/// 画面に出す文字の翻訳（日本語 / English / हिन्दी）。
/// コードの中では日本語の文字をそのままキーにして <see cref="T"/> で包む。表にない文字は日本語のまま出す。
/// 言語は設定（settings.json の Language）で選ぶ。空なら Windows の表示言語に合わせる。
/// 切り替えたときはアプリを再起動して反映する（<see cref="ChangeAndRestart"/>）。
/// </summary>
public static class Loc
{
    private static UiLanguage? _current;

    /// <summary>いまの表示言語</summary>
    public static UiLanguage Current => _current ??= Resolve(
        // 開発用: 環境変数 ISLANDOCK_LANG=en などで、設定を変えずに言語を試せる
        Environment.GetEnvironmentVariable("ISLANDOCK_LANG") is { Length: > 0 } dev ? dev : DockConfig.LoadSettings().Language);

    /// <summary>設定に保存する値（"ja" / "en" / "hi"、空 = 自動）から言語を決める</summary>
    public static UiLanguage Resolve(string? code) => code switch
    {
        "ja" => UiLanguage.Japanese,
        "en" => UiLanguage.English,
        "hi" => UiLanguage.Hindi,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "ja" => UiLanguage.Japanese,
            "hi" => UiLanguage.Hindi,
            _ => UiLanguage.English,
        },
    };

    /// <summary>日付などの書式に使うカルチャ</summary>
    public static CultureInfo Culture => Current switch
    {
        UiLanguage.English => CultureInfo.GetCultureInfo("en-US"),
        UiLanguage.Hindi => CultureInfo.GetCultureInfo("hi-IN"),
        _ => CultureInfo.GetCultureInfo("ja-JP"),
    };

    /// <summary>日本語の文字を、いまの言語に訳して返す</summary>
    public static string T(string ja)
    {
        if (Current == UiLanguage.Japanese || !Map.TryGetValue(ja, out var t)) return ja;
        return Current == UiLanguage.Hindi ? t.Hi : t.En;
    }

    /// <summary>書式つきの訳（{0} などを埋める）</summary>
    public static string F(string ja, params object?[] args) => string.Format(T(ja), args);

    /// <summary>日付の書式（日本語の書式をキーに、言語ごとの書式を返す）</summary>
    public static string DateFormat(string ja) => Current switch
    {
        UiLanguage.Japanese => ja,
        _ => ja switch
        {
            "yyyy年M月d日 dddd" => Current == UiLanguage.Hindi ? "dddd, d MMMM yyyy" : "dddd, MMMM d, yyyy",
            "M月d日 (ddd)" => Current == UiLanguage.Hindi ? "ddd, d MMM" : "ddd, MMM d",
            "yyyy年M月" => "MMMM yyyy",
            _ => ja,
        },
    };

    /// <summary>カレンダーの曜日の見出し（日曜始まり）</summary>
    public static string[] WeekdayHeaders() => Current == UiLanguage.Japanese
        ? new[] { "日", "月", "火", "水", "木", "金", "土" }
        : Culture.DateTimeFormat.AbbreviatedDayNames;

    /// <summary>
    /// XAML で書いた画面の文字（TextBlock・ボタン・ツールチップ・ウィンドウのタイトル）をまとめて訳す。
    /// InitializeComponent のあとに呼ぶ。
    /// </summary>
    public static void Apply(DependencyObject root)
    {
        if (Current == UiLanguage.Japanese) return;
        Walk(root);
    }

    private static void Walk(DependencyObject d)
    {
        switch (d)
        {
            case Window w when w.Title is { Length: > 0 }:
                w.Title = T(w.Title);
                break;
            case TextBlock tb:
                // 文字を書き換えると中身（Run）の一覧も変わるので、先に写してから書き換え、中には入らない
                if (tb.Inlines.Count > 1 || tb.Inlines.FirstInline is not Run)
                {
                    foreach (var run in tb.Inlines.OfType<Run>().ToList()) run.Text = T(run.Text);
                }
                else if (tb.Text.Length > 0)
                {
                    tb.Text = T(tb.Text);
                }
                if (tb.ToolTip is string tbTip) tb.ToolTip = T(tbTip);
                return;
            case ContentControl cc when cc.Content is string s:
                cc.Content = T(s);
                break;
        }
        if (d is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = T(tip);

        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().ToList())
            Walk(child);
    }

    /// <summary>言語を保存して、Islandock を再起動する</summary>
    public static void ChangeAndRestart(string code)
    {
        var settings = DockConfig.LoadSettings();
        if (settings.Language == code) return;
        settings.Language = code;
        DockConfig.SaveSettings(settings);

        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        // 後片付け（タスクバーを戻す・二重起動防止の印を手放す）が終わってから起動し直す
        Application.Current.Exit += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, Updater.UpdatedArg)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
            });
        Application.Current.Shutdown();
    }

    // ================================================================ 翻訳表（日本語 → English / हिन्दी）

    private static readonly Dictionary<string, (string En, string Hi)> Map = new()
    {
        // ----- 共通 -----
        ["追加"] = ("Add", "जोड़ें"),
        ["追加済み"] = ("Added", "जोड़ा गया"),
        ["キャンセル"] = ("Cancel", "रद्द करें"),
        ["デスクトップ"] = ("Desktop", "डेस्कटॉप"),
        ["アプリ"] = ("App", "ऐप"),
        ["開く"] = ("Open", "खोलें"),
        ["終了"] = ("Quit", "बंद करें"),
        ["設定"] = ("Settings", "सेटिंग्स"),
        ["表示"] = ("Show", "दिखाएं"),
        ["保存"] = ("Save", "सेव करें"),
        ["やめる"] = ("Cancel", "रद्द करें"),
        ["変更"] = ("Change", "बदलें"),
        ["自動"] = ("Auto", "ऑटो"),
        ["ライト"] = ("Light", "लाइट"),
        ["ダーク"] = ("Dark", "डार्क"),

        // ----- アプリの追加 -----
        ["Dock にアプリを追加"] = ("Add Apps to Dock", "Dock में ऐप जोड़ें"),
        ["Dock に追加するアプリを選んでください"] = ("Choose apps to add to the Dock", "Dock में जोड़ने के लिए ऐप चुनें"),
        ["🔍 アプリ名で検索"] = ("🔍 Search apps", "🔍 ऐप खोजें"),
        ["{0} 個のアプリ"] = ("{0} apps", "{0} ऐप"),
        ["{0} 個を選択中"] = ("{0} selected", "{0} चुने गए"),
        ["{0} 個を追加"] = ("Add {0}", "{0} जोड़ें"),

        // ----- 時計ウィジェット -----
        ["透明"] = ("Clear", "पारदर्शी"),
        ["自動（ライト / ダークに合わせる）"] = ("Auto (match light / dark)", "ऑटो (लाइट / डार्क के अनुसार)"),
        ["小"] = ("Small", "छोटा"),
        ["中"] = ("Medium", "मध्यम"),
        ["大"] = ("Large", "बड़ा"),
        ["サイズ: {0}"] = ("Size: {0}", "आकार: {0}"),
        ["ウィジェットを削除"] = ("Remove Widget", "विजेट हटाएं"),

        // ----- デスクトップの右クリック -----
        ["Islandock の設定..."] = ("Islandock Settings...", "Islandock सेटिंग्स..."),
        ["ディスプレイの設定"] = ("Display Settings", "डिस्प्ले सेटिंग्स"),
        ["個人用設定"] = ("Personalize", "वैयक्तिकृत करें"),
        ["Windows のメニューを表示"] = ("Show Windows Menu", "Windows मेनू दिखाएं"),

        // ----- 初期の Dock -----
        ["エクスプローラー"] = ("File Explorer", "फ़ाइल एक्सप्लोरर"),
        ["メモ帳"] = ("Notepad", "नोटपैड"),
        ["電卓"] = ("Calculator", "कैलकुलेटर"),
        ["コマンドプロンプト"] = ("Command Prompt", "कमांड प्रॉम्प्ट"),

        // ----- Dock -----
        ["アプリを追加"] = ("Add App", "ऐप जोड़ें"),
        ["ごみ箱"] = ("Recycle Bin", "रीसायकल बिन"),
        ["Dock から削除"] = ("Remove from Dock", "Dock से हटाएं"),
        ["起動できませんでした:\n{0}\n\n{1}"] = ("Couldn't open:\n{0}\n\n{1}", "नहीं खुल सका:\n{0}\n\n{1}"),
        ["ごみ箱を空にする..."] = ("Empty Recycle Bin...", "रीसायकल बिन खाली करें..."),
        ["新しいウィンドウ"] = ("New Window", "नई विंडो"),
        ["エクスプローラーで表示"] = ("Show in File Explorer", "फ़ाइल एक्सप्लोरर में दिखाएं"),
        ["左へ移動"] = ("Move Left", "बाएं ले जाएं"),
        ["右へ移動"] = ("Move Right", "दाएं ले जाएं"),
        ["拡大"] = ("Magnification", "मैग्निफिकेशन"),
        ["Dock を自動的に表示/非表示"] = ("Automatically Hide and Show the Dock", "Dock को अपने-आप छिपाएं / दिखाएं"),
        ["開く/しまうときのアニメーション（ジニー）"] = ("Open / Minimize Animation (Genie)", "खोलने / छोटा करने का एनिमेशन (Genie)"),
        ["アイコンをタイルに揃える"] = ("Fit Icons to Tiles", "आइकन को टाइल में रखें"),
        ["デスクトップのアプリを自動で追加"] = ("Auto-add Desktop Apps", "डेस्कटॉप ऐप अपने-आप जोड़ें"),
        ["ごみ箱を表示"] = ("Show Recycle Bin", "रीसायकल बिन दिखाएं"),
        ["時計ウィジェットを表示"] = ("Show Clock Widget", "घड़ी विजेट दिखाएं"),
        ["アプリを追加..."] = ("Add Apps...", "ऐप जोड़ें..."),
        ["ファイルを選んで追加..."] = ("Add from File...", "फ़ाइल से जोड़ें..."),
        ["設定ファイルを開く"] = ("Open Config File", "कॉन्फ़िग फ़ाइल खोलें"),
        ["Islandock を終了"] = ("Quit Islandock", "Islandock बंद करें"),
        ["Dock に追加するアプリを選択"] = ("Select an App to Add to the Dock", "Dock में जोड़ने के लिए ऐप चुनें"),
        ["アプリ・ショートカット (*.exe;*.lnk)|*.exe;*.lnk|すべてのファイル (*.*)|*.*"] =
            ("Apps & shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
             "ऐप और शॉर्टकट (*.exe;*.lnk)|*.exe;*.lnk|सभी फ़ाइलें (*.*)|*.*"),
        ["ごみ箱に移動できませんでした:\n{0}\n\n{1}"] =
            ("Couldn't move to the Recycle Bin:\n{0}\n\n{1}", "रीसायकल बिन में नहीं भेज सके:\n{0}\n\n{1}"),

        // ----- ファイル共有 -----
        ["ファイル共有"] = ("File Sharing", "फ़ाइल शेयरिंग"),
        ["離すと送信します"] = ("Release to share", "शेयर करने के लिए छोड़ें"),
        ["共有するファイルを選択"] = ("Choose Files to Share", "शेयर करने के लिए फ़ाइलें चुनें"),
        ["フォルダーは送れません。ファイルを選んでください（必要なら ZIP にまとめてください）"] =
            ("Folders can't be sent. Please choose files (zip them if needed).",
             "फ़ोल्डर नहीं भेजे जा सकते। फ़ाइलें चुनें (ज़रूरत हो तो ZIP बना लें)।"),
        ["{0} 個のファイル"] = ("{0} files", "{0} फ़ाइलें"),
        [" — 送信先を選んでください"] = (" — choose where to send", " — कहां भेजना है चुनें"),
        ["（フォルダー {0} 個は除外）"] = (" ({0} folders skipped)", " ({0} फ़ोल्डर छोड़े गए)"),
        ["共有画面を開けませんでした: "] = ("Couldn't open the share window: ", "शेयर विंडो नहीं खुल सकी: "),
        ["共有画面を開けませんでした。\n\n"] = ("Couldn't open the share window.\n\n", "शेयर विंडो नहीं खुल सकी।\n\n"),
        ["受信中: "] = ("Receiving: ", "प्राप्त हो रहा है: "),
        ["受信を始められませんでした: "] = ("Couldn't start receiving: ", "प्राप्त करना शुरू नहीं हो सका: "),
        ["待機中…（15 分で自動停止）"] = ("Waiting… (stops automatically after 15 min)", "इंतज़ार… (15 मिनट बाद अपने-आप रुकेगा)"),
        ["受信しました ✓ "] = ("Received ✓ ", "प्राप्त हुआ ✓ "),
        ["ファイルを受信しました"] = ("File received", "फ़ाइल प्राप्त हुई"),
        ["Islandock ファイル共有"] = ("Islandock File Sharing", "Islandock फ़ाइल शेयरिंग"),
        ["ファイルを読み込めませんでした: "] = ("Couldn't read the file: ", "फ़ाइल नहीं पढ़ी जा सकी: "),
        ["PC間: すべての人"] = ("Nearby: Everyone", "आस-पास: सभी"),
        ["PC間: 自分のみ"] = ("Nearby: My devices only", "आस-पास: केवल मेरे डिवाइस"),
        ["PC間: オフ"] = ("Nearby: Off", "आस-पास: बंद"),
        ["PC間: 不明"] = ("Nearby: Unknown", "आस-पास: अज्ञात"),
        ["Windows の近距離共有で、ほかの PC から受け取れる相手（「設定」で変更）"] =
            ("Who can send to this PC with Windows Nearby Sharing (change in Settings)",
             "Windows Nearby Sharing से कौन इस PC पर भेज सकता है (सेटिंग्स में बदलें)"),
        ["ファイルをここにドラッグ、またはクリックして選択"] =
            ("Drag files here, or click to choose", "फ़ाइलें यहां खींचें, या चुनने के लिए क्लिक करें"),
        ["ファイル共有（近距離共有）"] = ("File Sharing (Nearby Sharing)", "फ़ाइल शेयरिंग (Nearby Sharing)"),
        ["スマホから受け取る"] = ("Receive from Phone", "फ़ोन से पाएं"),
        ["1. スマホを PC と同じ Wi-Fi につなぐ"] = ("1. Connect your phone to the same Wi-Fi as this PC", "1. फ़ोन को इसी PC वाले Wi-Fi से जोड़ें"),
        ["2. カメラでこの QR コードを読み取る"] = ("2. Scan this QR code with your camera", "2. कैमरे से यह QR कोड स्कैन करें"),
        ["3. 開いたページでファイルを選ぶ"] = ("3. Choose files on the page that opens", "3. खुले पेज पर फ़ाइलें चुनें"),
        ["待機中…"] = ("Waiting…", "इंतज़ार…"),
        ["受信を止める"] = ("Stop Receiving", "प्राप्त करना बंद करें"),
        ["受信: オフ"] = ("Receiving: Off", "प्राप्त करना: बंद"),
        ["近距離共有の設定を開く"] = ("Open Nearby Sharing settings", "Nearby Sharing सेटिंग्स खोलें"),
        ["受信したファイル"] = ("Received Files", "प्राप्त फ़ाइलें"),
        ["Wi-Fi / 有線 LAN に接続されていません"] = ("Not connected to Wi-Fi or a wired LAN", "Wi-Fi या वायर्ड LAN से कनेक्ट नहीं है"),

        // スマホで開くページ
        ["Islandock ファイル受信"] = ("Islandock File Receive", "Islandock फ़ाइल प्राप्ति"),
        ["PC に送る"] = ("Send to PC", "PC पर भेजें"),
        ["写真やファイルを選ぶと、PC の「ダウンロード」に保存されます"] =
            ("Choose photos or files — they'll be saved to the PC's Downloads folder",
             "फ़ोटो या फ़ाइलें चुनें — वे PC के Downloads फ़ोल्डर में सेव होंगी"),
        ["ファイルを選ぶ"] = ("Choose Files", "फ़ाइलें चुनें"),
        ["待機中"] = ("Waiting", "इंतज़ार"),
        ["送信しました ✓"] = ("Sent ✓", "भेज दी गई ✓"),
        ["失敗しました"] = ("Failed", "विफल"),
        ["送れませんでした。PC の受信が止まっていないか確認してください"] =
            ("Couldn't send. Check that receiving is still on on the PC.", "नहीं भेज सके। देखें कि PC पर प्राप्त करना चालू है।"),

        // ----- アイランド -----
        ["メディア プレーヤー"] = ("Media Player", "मीडिया प्लेयर"),
        ["再生 / 一時停止"] = ("Play / Pause", "चलाएं / पॉज़"),
        ["再生中"] = ("Now Playing", "अभी चल रहा"),
        ["タイマー"] = ("Timer", "टाइमर"),
        ["カレンダー"] = ("Calendar", "कैलेंडर"),
        ["ストップウォッチ"] = ("Stopwatch", "स्टॉपवॉच"),
        ["今日"] = ("Today", "आज"),
        ["消音"] = ("Mute", "म्यूट"),
        ["再生中の曲はありません"] = ("Nothing playing", "अभी कुछ नहीं चल रहा"),
        ["Spotify・YouTube（ブラウザ）・メディア プレーヤーなどで再生すると、ここに表示されます"] =
            ("Play something in Spotify, YouTube (browser), Media Player and more to see it here",
             "Spotify, YouTube (ब्राउज़र), मीडिया प्लेयर आदि में कुछ चलाएं, वह यहां दिखेगा"),
        ["▶  BGMを再生"] = ("▶  Play BGM", "▶  BGM चलाएं"),
        ["設定した YouTube の再生リストを開いて再生"] = ("Open and play your YouTube playlist", "आपकी YouTube प्लेलिस्ट खोलकर चलाएं"),
        ["BGM に使う再生リストを変える"] = ("Change the BGM playlist", "BGM प्लेलिस्ट बदलें"),
        ["YouTube の再生リストの URL を貼り付け"] = ("Paste a YouTube playlist URL", "YouTube प्लेलिस्ट URL पेस्ट करें"),
        ["♪  BGM の再生リストを設定"] = ("♪  Set BGM playlist", "♪  BGM प्लेलिस्ट सेट करें"),
        ["例: https://www.youtube.com/playlist?list=PL…（空にして保存すると解除）"] =
            ("e.g. https://www.youtube.com/playlist?list=PL… (save empty to clear)",
             "उदाहरण: https://www.youtube.com/playlist?list=PL… (खाली सेव करने पर हट जाएगा)"),
        ["YouTube の再生リスト（または動画）の URL を貼り付けてください"] =
            ("Paste the URL of a YouTube playlist (or video)", "YouTube प्लेलिस्ट (या वीडियो) का URL पेस्ट करें"),
        ["{0}分"] = ("{0} min", "{0} मिनट"),
        ["+1分"] = ("+1 min", "+1 मिनट"),
        ["−1分"] = ("−1 min", "−1 मिनट"),
        ["+5分"] = ("+5 min", "+5 मिनट"),
        ["開始"] = ("Start", "शुरू"),
        ["一時停止"] = ("Pause", "पॉज़"),
        ["再開"] = ("Resume", "फिर शुरू"),
        ["停止"] = ("Stop", "स्टॉप"),
        ["リセット"] = ("Reset", "रीसेट"),
        ["ラップ"] = ("Lap", "लैप"),
        ["ラップ {0}"] = ("Lap {0}", "लैप {0}"),
        ["時間になりました"] = ("Time's up", "समय पूरा हुआ"),
        ["カメラ: "] = ("Camera: ", "कैमरा: "),
        ["マイク: "] = ("Microphone: ", "माइक: "),
        ["消音 オン"] = ("Mute On", "म्यूट चालू"),
        ["消音 オフ"] = ("Mute Off", "म्यूट बंद"),
        ["オーディオ出力"] = ("Audio Output", "ऑडियो आउटपुट"),
        ["音の出力先が切り替わりました"] = ("Audio output changed", "ऑडियो आउटपुट बदल गया"),
        ["充電中"] = ("Charging", "चार्ज हो रहा है"),
        ["電源から外れました"] = ("Unplugged", "चार्जर हटाया गया"),
        ["バッテリー残量が少なくなっています"] = ("Battery is low", "बैटरी कम है"),
        ["残り {0}%"] = ("{0}% left", "{0}% बाकी"),
        ["省電力モード オン"] = ("Battery Saver On", "बैटरी सेवर चालू"),
        ["省電力モード オフ"] = ("Battery Saver Off", "बैटरी सेवर बंद"),
        ["カメラを使用中"] = ("Camera in use", "कैमरा इस्तेमाल में"),
        ["マイクを使用中"] = ("Microphone in use", "माइक इस्तेमाल में"),

        // ----- 検索 -----
        ["Islandock 検索"] = ("Islandock Search", "Islandock खोज"),
        ["検索"] = ("Search", "खोजें"),
        ["（使えるキーがありません）"] = ("(no shortcut available)", "(कोई शॉर्टकट उपलब्ध नहीं)"),
        ["トップヒット"] = ("Top Hit", "सबसे सही परिणाम"),
        ["アプリケーション"] = ("Applications", "ऐप्लिकेशन"),
        ["フォルダ"] = ("Folders", "फ़ोल्डर"),
        ["ファイル"] = ("Files", "फ़ाइलें"),
        ["開けませんでした:\n{0}\n\n{1}"] = ("Couldn't open:\n{0}\n\n{1}", "नहीं खुल सका:\n{0}\n\n{1}"),
        ["ホーム"] = ("Home", "होम"),
        ["Enter でコピー"] = ("Press Enter to copy", "कॉपी करने के लिए Enter दबाएं"),

        // ----- 設定画面 -----
        ["Islandock の設定"] = ("Islandock Settings", "Islandock सेटिंग्स"),
        ["一般"] = ("General", "सामान्य"),
        ["外観"] = ("Appearance", "रूप"),
        ["Dock・メニュー・時計ウィジェットの色"] = ("Colors of the Dock, menus and clock widget", "Dock, मेनू और घड़ी विजेट के रंग"),
        ["言語"] = ("Language", "भाषा"),
        ["変えると Islandock が再起動します"] = ("Islandock restarts when you change it", "बदलने पर Islandock फिर से शुरू होगा"),
        ["PC の起動時に Islandock を開く"] = ("Open Islandock at startup", "PC शुरू होने पर Islandock खोलें"),
        ["Windows にサインインしたときに自動で起動します"] =
            ("Starts automatically when you sign in to Windows", "Windows में साइन इन करने पर अपने-आप शुरू होता है"),
        ["デスクトップの右クリックに Islandock のメニューを出す"] =
            ("Show the Islandock menu when right-clicking the desktop", "डेस्कटॉप पर राइट-क्लिक करने पर Islandock मेनू दिखाएं"),
        ["デスクトップの何もないところを右クリックすると、この設定画面を開けます"] =
            ("Right-click an empty spot on the desktop to open these settings", "डेस्कटॉप की खाली जगह पर राइट-क्लिक करके ये सेटिंग्स खोलें"),
        ["検索バー"] = ("Search bar", "सर्च बार"),
        ["{0} で、アプリとファイルを探せる検索バーを出します"] =
            ("Press {0} to open a search bar for apps and files", "ऐप और फ़ाइलें खोजने वाला सर्च बार खोलने के लिए {0} दबाएं"),
        ["検索バーを常にデスクトップに表示"] = ("Always show the search bar on the desktop", "सर्च बार हमेशा डेस्कटॉप पर दिखाएं"),
        ["キーを押さなくても、画面の中央（ウィンドウの後ろ）に置いておきます"] =
            ("Keeps it in the center of the screen (behind windows) without pressing a key",
             "बिना की दबाए स्क्रीन के बीच में (विंडो के पीछे) रखता है"),
        ["アップデートを自動で確認"] = ("Check for updates automatically", "अपडेट अपने-आप जांचें"),
        ["起動時に新しいバージョンがないか確かめます（いまはバージョン {0}）"] =
            ("Checks for a new version at startup (current version {0})", "शुरू होने पर नया वर्ज़न जांचता है (मौजूदा वर्ज़न {0})"),
        ["マウスを近づけたアイコンを大きくします"] = ("Enlarges icons as the mouse gets close", "माउस पास आने पर आइकन बड़े होते हैं"),
        ["Dock を自動的に表示 / 非表示"] = ("Automatically hide and show the Dock", "Dock को अपने-आप छिपाएं / दिखाएं"),
        ["画面の下端にマウスを当てたときだけ出します"] =
            ("Shows only when the mouse touches the bottom edge of the screen", "माउस स्क्रीन के निचले किनारे पर जाने पर ही दिखता है"),
        ["Windows のタスクバーを隠す"] = ("Hide the Windows taskbar", "Windows टास्कबार छिपाएं"),
        ["Dock だけにします。Islandock を終了すると元に戻ります"] =
            ("Use only the Dock. The taskbar comes back when Islandock quits", "सिर्फ Dock रखें। Islandock बंद करने पर टास्कबार वापस आ जाएगा"),
        ["開く / しまうときのアニメーション（ジニー）"] = ("Open / minimize animation (Genie)", "खोलने / छोटा करने का एनिमेशन (Genie)"),
        ["デスクトップにアナログ時計を置きます（右クリックでスタイルを変更）"] =
            ("Puts an analog clock on the desktop (right-click to change its style)",
             "डेस्कटॉप पर एनालॉग घड़ी रखता है (स्टाइल बदलने के लिए राइट-क्लिक करें)"),
        ["アイランド"] = ("Island", "आइलैंड"),
        ["アイランドを使う"] = ("Use the Island", "आइलैंड इस्तेमाल करें"),
        ["画面上部の真ん中に出るカプセル型のバー"] = ("A capsule-shaped bar at the top center of the screen", "स्क्रीन के ऊपर बीच में कैप्सूल जैसी बार"),
        ["再生中の曲の表示と操作"] = ("Show and control what's playing", "चल रहा गाना देखें और कंट्रोल करें"),
        ["タイマー / ストップウォッチ"] = ("Timer / Stopwatch", "टाइमर / स्टॉपवॉच"),
        ["近距離共有で送る・スマホから受け取る"] = ("Send with Nearby Sharing, receive from your phone", "Nearby Sharing से भेजें, फ़ोन से पाएं"),
        ["お知らせ"] = ("Alerts", "सूचनाएं"),
        ["音量・充電・出力先の切り替えなど"] = ("Volume, charging, audio output changes and more", "वॉल्यूम, चार्जिंग, ऑडियो आउटपुट बदलाव आदि"),
        ["マイク / カメラのランプ"] = ("Mic / camera indicator", "माइक / कैमरा इंडिकेटर"),
        ["使っている間、アイランドの右に点を表示します"] =
            ("Shows a dot to the right of the Island while they're in use", "इस्तेमाल के दौरान आइलैंड के दाईं ओर एक बिंदु दिखाता है"),
        ["設定フォルダーを開く"] = ("Open Settings Folder", "सेटिंग्स फ़ोल्डर खोलें"),
        ["アップデートを確認"] = ("Check for Updates", "अपडेट जांचें"),
        ["背景の透明度"] = ("Background transparency", "बैकग्राउंड पारदर्शिता"),
        ["大きくするほど Dock の背景が透けます（100% で背景なし）"] =
            ("Higher values make the Dock background more see-through (100% = no background)",
             "ज़्यादा करने पर Dock का बैकग्राउंड ज़्यादा पारदर्शी होगा (100% = कोई बैकग्राउंड नहीं)"),

        // ----- アップデート -----
        ["アップデートを確認できませんでした。\nインターネットにつながっているか確かめてください。\n\n"] =
            ("Couldn't check for updates.\nPlease check your internet connection.\n\n",
             "अपडेट नहीं जांच सके।\nकृपया इंटरनेट कनेक्शन जांचें।\n\n"),
        ["Islandock は最新です（バージョン {0}）。"] = ("Islandock is up to date (version {0}).", "Islandock नवीनतम है (वर्ज़न {0})।"),
        ["新しいバージョン {0} があります。\n開発用のビルドなので、自動では入れ替えません。"] =
            ("Version {0} is available.\nThis is a development build, so it won't be replaced automatically.",
             "नया वर्ज़न {0} उपलब्ध है।\nयह डेवलपमेंट बिल्ड है, इसलिए अपने-आप नहीं बदला जाएगा।"),
        ["Islandock の新しいバージョン {0} があります（いまは {1}）。\n\nいま更新しますか？ ダウンロードが終わると、Islandock が自動で再起動します。\n\n「いいえ」を選ぶと、このバージョンはもうお知らせしません\n（設定画面の「アップデートを確認」からいつでも更新できます）。"] =
            ("A new version of Islandock ({0}) is available. You have {1}.\n\nUpdate now? Islandock restarts automatically after the download.\n\n" +
             "If you choose \"No\", you won't be told about this version again\n(you can always update from \"Check for Updates\" in Settings).",
             "Islandock का नया वर्ज़न {0} उपलब्ध है (आपके पास {1} है)।\n\nअभी अपडेट करें? डाउनलोड के बाद Islandock अपने-आप फिर से शुरू होगा।\n\n" +
             "\"No\" चुनने पर इस वर्ज़न की सूचना दोबारा नहीं मिलेगी\n(सेटिंग्स में \"अपडेट जांचें\" से कभी भी अपडेट कर सकते हैं)।"),
        ["Islandock のアップデート"] = ("Islandock Update", "Islandock अपडेट"),
        ["ダウンロードした zip に Islandock.exe が入っていません。"] =
            ("The downloaded zip doesn't contain Islandock.exe.", "डाउनलोड की गई zip में Islandock.exe नहीं है।"),
        ["ダウンロードした Islandock.exe が壊れています。"] =
            ("The downloaded Islandock.exe is damaged.", "डाउनलोड की गई Islandock.exe खराब है।"),
        ["アップデートできませんでした。\n\n"] = ("Couldn't update.\n\n", "अपडेट नहीं हो सका।\n\n"),
        ["\n\nダウンロードページを開きますか？（zip をダウンロードして、いまの Islandock.exe と入れ替えてください）"] =
            ("\n\nOpen the download page? (Download the zip and replace your current Islandock.exe.)",
             "\n\nडाउनलोड पेज खोलें? (zip डाउनलोड करके मौजूदा Islandock.exe को बदलें।)"),
    };
}
