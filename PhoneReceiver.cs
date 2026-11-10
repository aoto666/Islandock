using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Islandock;

/// <summary>
/// スマホ → PC のファイル受信。
/// PC の中に小さな Web サーバーを立てて、スマホのブラウザからファイルを送ってもらう。
///  ・同じ Wi-Fi の中だけで使える
///  ・URL に毎回変わる合言葉（トークン）が入っているので、QR を見た人しか送れない
///  ・受け取ったファイルは「ダウンロード」フォルダーに保存
/// </summary>
public sealed class PhoneReceiver
{
    private const int PreferredPort = 53517;
    private const long MaxFileSize = 8L * 1024 * 1024 * 1024; // 8 GB まで

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";

    public bool Running => _listener != null;
    public string Url { get; private set; } = "";
    public string SaveFolder { get; set; } = "";

    /// <summary>ファイルを1つ受け取り終わった（保存先のパス）。別スレッドから呼ばれる</summary>
    public event Action<string>? Received;
    /// <summary>受信を始めた（ファイル名）。別スレッドから呼ばれる</summary>
    public event Action<string>? Receiving;

    public void Start()
    {
        if (Running) return;
        string ip = LocalAddress() ?? throw new InvalidOperationException(Loc.T("Wi-Fi / 有線 LAN に接続されていません"));

        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        _listener = Listen(PreferredPort) ?? Listen(0)!;
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://{ip}:{port}/{_token}";

        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Url = "";
    }

    private static TcpListener? Listen(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            return l;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>この PC の LAN 内の IPv4 アドレス（ゲートウェイのある Wi-Fi / 有線を優先）</summary>
    private static string? LocalAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                            or NetworkInterfaceType.GigabitEthernet &&
                        !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                        !a.ToString().StartsWith("169.254."));
        return candidates.FirstOrDefault()?.ToString();
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Handle(client, ct), ct);
        }
    }

    // ===================== HTTP の処理 =====================

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var (method, path, headers, leftover) = await ReadHead(stream, ct);
            if (method == null) return;

            string query = "";
            int q = path.IndexOf('?');
            if (q >= 0) { query = path[(q + 1)..]; path = path[..q]; }

            if (method == "GET" && path == "/" + _token)
            {
                await Respond(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(UploadPage()));
            }
            else if (method == "POST" && path == $"/{_token}/upload")
            {
                await ReceiveFile(stream, headers, leftover, query, ct);
            }
            else
            {
                await Respond(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
            }
        }
        catch
        {
            // 通信が途中で切れた等
        }
    }

    private static async Task<(string?, string, Dictionary<string, string>, byte[])> ReadHead(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        int filled = 0;
        while (true)
        {
            if (filled == buffer.Length) return (null, "", new(), Array.Empty<byte>());
            int n = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (n == 0) return (null, "", new(), Array.Empty<byte>());
            filled += n;
            int end = IndexOf(buffer, filled, "\r\n\r\n"u8);
            if (end < 0) continue;

            var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return (null, "", new(), Array.Empty<byte>());
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int c = line.IndexOf(':');
                if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
            }
            return (first[0], first[1], headers, buffer[(end + 4)..filled]);
        }
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) =>
        data.AsSpan(0, length).IndexOf(pattern);

    private async Task ReceiveFile(NetworkStream stream, Dictionary<string, string> headers, byte[] leftover, string query,
        CancellationToken ct)
    {
        if (!headers.TryGetValue("Content-Length", out var lenText) || !long.TryParse(lenText, out long length) ||
            length < 0 || length > MaxFileSize)
        {
            await Respond(stream, "411 Length Required", "text/plain", "Length required"u8.ToArray());
            return;
        }

        string name = "ファイル";
        foreach (var part in query.Split('&'))
            if (part.StartsWith("name=")) name = Uri.UnescapeDataString(part[5..].Replace('+', ' '));
        name = SafeFileName(name);
        Receiving?.Invoke(name);

        Directory.CreateDirectory(SaveFolder);
        string target = UniquePath(Path.Combine(SaveFolder, name));
        string temp = target + ".part";
        try
        {
            await using (var file = File.Create(temp))
            {
                long remaining = length;
                int first = (int)Math.Min(leftover.Length, remaining);
                await file.WriteAsync(leftover.AsMemory(0, first), ct);
                remaining -= first;
                var buf = new byte[256 * 1024];
                while (remaining > 0)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct);
                    if (n == 0) throw new IOException("途中で切れました");
                    await file.WriteAsync(buf.AsMemory(0, n), ct);
                    remaining -= n;
                }
            }
            target = UniquePath(target); // 受信中に同じ名前のファイルができていた場合に備えて
            File.Move(temp, target);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }

        await Respond(stream, "200 OK", "text/plain; charset=utf-8", "OK"u8.ToArray());
        Received?.Invoke(target);
    }

    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimStart('.');
        return string.IsNullOrEmpty(name) ? "ファイル" : name.Length > 150 ? name[^150..] : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private static async Task Respond(NetworkStream stream, string status, string type, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    // ===================== スマホに表示するページ =====================

    private string UploadPage() => LocalizePage("""
<!DOCTYPE html>
<html lang="ja"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<title>Islandock ファイル受信</title>
<style>
 :root{color-scheme:dark}
 body{margin:0;font-family:-apple-system,BlinkMacSystemFont,"Hiragino Sans",sans-serif;background:#000;color:#f2f2f7;
      min-height:100vh;display:flex;flex-direction:column;align-items:center;padding:48px 20px;box-sizing:border-box}
 .logo{width:88px;height:88px;border-radius:50%;background:linear-gradient(#40a9ff,#0a64e0);display:flex;
       align-items:center;justify-content:center;box-shadow:0 0 0 10px rgba(10,132,255,.15)}
 h1{font-size:24px;margin:22px 0 6px} p{color:#8e8e93;margin:0 0 28px;text-align:center;font-size:15px}
 label{display:block;width:100%;max-width:360px;text-align:center;background:#0a84ff;color:#fff;font-size:17px;
       font-weight:600;padding:16px;border-radius:14px;box-sizing:border-box}
 input{display:none}
 ul{list-style:none;padding:0;width:100%;max-width:360px;margin-top:22px}
 li{background:#1c1c1e;border-radius:12px;padding:12px 14px;margin-bottom:8px;font-size:14px}
 .name{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
 .bar{height:4px;background:#3a3a3c;border-radius:2px;margin-top:8px;overflow:hidden}
 .bar i{display:block;height:100%;width:0;background:#0a84ff;transition:width .2s}
 .done .bar i{background:#30d158} .err .bar i{background:#ff453a}
 .st{color:#8e8e93;font-size:12px;margin-top:6px}
</style></head><body>
<div class="logo"><svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2"
 stroke-linecap="round" stroke-linejoin="round"><path d="M12 16V4M7 9l5-5 5 5"/><path d="M5 14v4a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-4"/></svg></div>
<h1>PC に送る</h1>
<p>写真やファイルを選ぶと、PC の「ダウンロード」に保存されます</p>
<label>ファイルを選ぶ<input id="f" type="file" multiple></label>
<ul id="list"></ul>
<script>
const f=document.getElementById('f'),list=document.getElementById('list');
let queue=Promise.resolve();
f.onchange=()=>{for(const file of f.files){const li=add(file);queue=queue.then(()=>send(file,li));}f.value='';};
function add(file){const li=document.createElement('li');
 li.innerHTML='<div class="name"></div><div class="bar"><i></i></div><div class="st">待機中</div>';
 li.querySelector('.name').textContent=file.name;list.prepend(li);return li;}
function send(file,li){return new Promise(res=>{
 const x=new XMLHttpRequest(),bar=li.querySelector('i'),st=li.querySelector('.st');
 x.open('POST',location.pathname+'/upload?name='+encodeURIComponent(file.name));
 x.upload.onprogress=e=>{if(e.lengthComputable){const p=Math.round(e.loaded/e.total*100);bar.style.width=p+'%';st.textContent=p+'%';}};
 x.onload=()=>{if(x.status==200){li.className='done';bar.style.width='100%';st.textContent='送信しました ✓';}
   else{li.className='err';st.textContent='失敗しました ('+x.status+')';}res();};
 x.onerror=()=>{li.className='err';st.textContent='送れませんでした。PC の受信が止まっていないか確認してください';res();};
 x.send(file);});}
</script></body></html>
""");

    /// <summary>スマホに出すページの文字を、いまの言語に置き換える</summary>
    private static string LocalizePage(string html)
    {
        // 長いものから置き換える（「ファイルを選ぶ」が「写真やファイルを選ぶと…」の一部なので）
        foreach (var ja in new[]
                 {
                     "写真やファイルを選ぶと、PC の「ダウンロード」に保存されます",
                     "送れませんでした。PC の受信が止まっていないか確認してください",
                     "Islandock ファイル受信", "PC に送る", "ファイルを選ぶ", "送信しました ✓", "失敗しました", "待機中",
                 })
            html = html.Replace(ja, Loc.T(ja));
        string lang = Loc.Current switch { UiLanguage.English => "en", UiLanguage.Hindi => "hi", _ => "ja" };
        return html.Replace("<html lang=\"ja\">", $"<html lang=\"{lang}\">");
    }
}
