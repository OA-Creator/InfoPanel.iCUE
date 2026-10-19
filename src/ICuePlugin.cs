using System.Globalization;

using System.Text;
using InfoPanel.Plugins;

namespace InfoPanel.iCUE;

public class ICuePlugin : BasePlugin
{
    private const int MaxSpalten = 12;
    private readonly string _pluginOrdner;
    private readonly string _iniPfad;
    private readonly string _cachePfad;
    private string _logOrdner = "";
    private int _maxAlterSek = 180;
    private int _nachkomma = 1;
    private readonly List<PluginText> _texte = new();

    // Sensoren mit fester ID nach Spaltennummer (col1, col2, ...), damit das Layout in InfoPanel stabil bleibt
    private readonly List<PluginSensor> _sensoren = new();
    private readonly PluginText _datei = new("datei", "Protokolldatei", "-");
    private readonly PluginText _stand = new("stand", "Letzter Wert von", "-");

    public ICuePlugin() : base("icue-sensoren", "iCUE Sensoren", "Werte aus der iCUE-Sensorprotokollierung")
    {
        _pluginOrdner = Path.GetDirectoryName(typeof(ICuePlugin).Assembly.Location) ?? AppContext.BaseDirectory;
        _iniPfad = Path.Combine(_pluginOrdner, "InfoPanel.iCUE.ini");
        _cachePfad = Path.Combine(_pluginOrdner, "spalten.txt");
    }

    public override string? ConfigFilePath => _iniPfad;
    public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public override void Initialize()
    {
        _logOrdner = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            if (File.Exists(_iniPfad))
            {
                foreach (var zeile in File.ReadAllLines(_iniPfad))
                {
                    var z = zeile.Trim();
                    if (z.StartsWith(';') || !z.Contains('=')) continue;
                    var teile = z.Split('=', 2);
                    var key = teile[0].Trim(); var val = teile[1].Trim();
                    if (key.Equals("Ordner", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                        _logOrdner = Environment.ExpandEnvironmentVariables(val.Trim('"'));
                    if (key.Equals("MaxAlterSek", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var m) && m > 0)
                        _maxAlterSek = m;
                    if (key.Equals("Nachkommastellen", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var n) && n >= 0 && n <= 2)
                        _nachkomma = n;
                }
            }
        }
        catch { }
    }

    public override void Load(List<IPluginContainer> containers)
    {
        // Spaltennamen: aus der neuesten Protokolldatei, sonst aus dem Zwischenspeicher
        var namen = KopfzeileLesen(NeuesteDatei()) ?? CacheLesen();
        var container = new PluginContainer("icue", "iCUE");
        for (int i = 0; i < MaxSpalten; i++)
        {
            var name = i < namen.Count ? namen[i] : $"Spalte {i + 1}";
            var s = new PluginSensor($"col{i + 1}", name, 0, Einheit(name));
            var t = new PluginText($"txt{i + 1}", name + " (Anzeige)", "--");
            _sensoren.Add(s); _texte.Add(t);
            if (i < Math.Max(namen.Count, 1)) { container.Entries.Add(s); container.Entries.Add(t); }
        }
        container.Entries.Add(_datei);
        container.Entries.Add(_stand);
        containers.Add(container);
    }

    public override Task UpdateAsync(CancellationToken cancellationToken)
    {
        try { Aktualisieren(); } catch { }
        return Task.CompletedTask;
    }

    public override void Update()
    {
        try { Aktualisieren(); } catch { }
    }

    public override void Close() { }

    private void Aktualisieren()
    {
        var datei = NeuesteDatei();
        if (datei == null) { _datei.Value = "keine Datei gefunden"; AlleVeraltet(); return; }
        _datei.Value = Path.GetFileName(datei);
        var alter = DateTime.UtcNow - File.GetLastWriteTimeUtc(datei);
        bool veraltet = alter.TotalSeconds > _maxAlterSek;

        var (kopf, letzte) = KopfUndLetzteZeile(datei);
        if (kopf == null || letzte == null) return;

        var namen = Spalten(kopf).Skip(1).Select(n => n.Trim()).ToList();
        CacheSchreiben(namen);
        var werte = Spalten(letzte);
        if (werte.Count > 0) _stand.Value = werte[0].Trim();

        for (int i = 0; i < namen.Count && i < _sensoren.Count; i++)
        {
            if (i + 1 >= werte.Count) break;
            if (Zahl(werte[i + 1], out var v))
            {
                _sensoren[i].Value = v;
                _texte[i].Value = veraltet ? "--" : v.ToString(_nachkomma == 0 ? "0" : "0." + new string('0', _nachkomma), CultureInfo.InvariantCulture) + (_sensoren[i].Unit ?? "");
            }
        }
        if (veraltet) { AlleVeraltet(); _stand.Value = "Protokollierung gestoppt?"; }
    }

    private void AlleVeraltet() { foreach (var t in _texte) t.Value = "--"; }

    private string? NeuesteDatei()
    {
        try
        {
            if (!Directory.Exists(_logOrdner)) return null;
            return new DirectoryInfo(_logOrdner).GetFiles("corsair_cue_*.csv")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private static List<string>? KopfzeileLesen(string? datei)
    {
        if (datei == null) return null;
        try
        {
            var (kopf, _) = KopfUndLetzteZeile(datei);
            return kopf == null ? null : Spalten(kopf).Skip(1).Select(n => n.Trim()).ToList();
        }
        catch { return null; }
    }

    // Liest die erste und die letzte vollständige Zeile, ohne die von iCUE geöffnete Datei zu sperren
    private static (string? kopf, string? letzte) KopfUndLetzteZeile(string datei)
    {
        using var fs = new FileStream(datei, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string? kopf;
        using (var sr = new StreamReader(fs, Encoding.UTF8, true, 4096, leaveOpen: true))
            kopf = sr.ReadLine();

        long start = Math.Max(0, fs.Length - 8192);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr2 = new StreamReader(fs, Encoding.UTF8, false);
        var rest = sr2.ReadToEnd();
        var zeilen = rest.Split('\n').Select(z => z.Trim('\r', ' ', '﻿')).Where(z => z.Length > 0).ToList();
        if (start > 0 && zeilen.Count > 0) zeilen.RemoveAt(0); // evtl. abgeschnittene erste Zeile
        string? letzte = zeilen.LastOrDefault();
        if (letzte != null && kopf != null && letzte.Trim('﻿') == kopf.Trim('﻿')) letzte = null;
        return (kopf?.Trim('﻿'), letzte);
    }

    private static List<string> Spalten(string zeile)
    {
        var liste = new List<string>();
        var sb = new StringBuilder();
        bool inAnf = false;
        foreach (var c in zeile)
        {
            if (c == '"') { inAnf = !inAnf; continue; }
            if (c == ',' && !inAnf) { liste.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        liste.Add(sb.ToString());
        return liste;
    }

    private static bool Zahl(string text, out float wert)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim())
            if (char.IsDigit(c) || c == '.' || c == ',' || c == '-') sb.Append(c);
        var s = sb.ToString();
        if (s.Contains(',') && !s.Contains('.')) s = s.Replace(',', '.');
        else s = s.Replace(",", "");
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out wert);
    }

    private static string? Einheit(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("auslastung") || n.Contains("load")) return "%";
        if (n.Contains("lüfter") || n.Contains("pumpe") || n.Contains("fan") || n.Contains("pump") || n.Contains("rpm")) return " RPM";
        if (n.Contains("vcpu") || n.Contains("volt")) return " V";
        return "°C";
    }

    private List<string> CacheLesen()
    {
        try { return File.Exists(_cachePfad) ? File.ReadAllLines(_cachePfad, Encoding.UTF8).Where(z => z.Length > 0).ToList() : new(); }
        catch { return new(); }
    }

    private List<string>? _letzterCache;
    private void CacheSchreiben(List<string> namen)
    {
        if (_letzterCache != null && _letzterCache.SequenceEqual(namen)) return;
        try { File.WriteAllLines(_cachePfad, namen, new UTF8Encoding(false)); _letzterCache = namen; } catch { }
    }
}
