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
    private int _trendSek = 60;
    private float _trendSchwelle = 0.2f;
    private const int MittelSek = 10;
    // InfoPanel zeigt bei leerem Text "0" an – darum ein Leerzeichen statt leer
    private const string Leer = " ";
    private string _zeichenSteigt = "▲";
    private string _zeichenFaellt = "▼";
    private string _zeichenSteht = "►";
    private readonly List<PluginText> _texte = new();
    private readonly List<PluginText> _trends = new();
    private readonly List<PluginText> _steigt = new();
    private readonly List<PluginText> _faellt = new();
    private readonly List<PluginText> _steht = new();
    private readonly List<List<(DateTime zeit, float wert)>> _verlauf = new();

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
                    if (key.Equals("TrendSek", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var ts) && ts >= 10)
                        _trendSek = ts;
                    if (key.Equals("TrendSchwelle", StringComparison.OrdinalIgnoreCase)
                        && float.TryParse(val.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sw) && sw > 0)
                        _trendSchwelle = sw;
                    if (key.Equals("ZeichenSteigt", StringComparison.OrdinalIgnoreCase) && val.Length > 0) _zeichenSteigt = val;
                    if (key.Equals("ZeichenFaellt", StringComparison.OrdinalIgnoreCase) && val.Length > 0) _zeichenFaellt = val;
                    if (key.Equals("ZeichenSteht", StringComparison.OrdinalIgnoreCase) && val.Length > 0) _zeichenSteht = val;
                }
            }
        }
        catch { }
    }

    public override void Load(List<IPluginContainer> containers)
    {
        // Feste Plätze: jeder iCUE-Sensor behält seinen Platz (col1, col2 ...), auch wenn in iCUE
        // weitere Sensoren an- oder abgehakt werden. Gespeichert in plaetze.txt.
        PlaetzeAbgleichen(KopfzeileLesen(NeuesteDatei()));
        var namen = _plaetze;
        var container = new PluginContainer("icue", "iCUE");
        for (int i = 0; i < MaxSpalten; i++)
        {
            var name = i < namen.Count ? namen[i] : $"Spalte {i + 1}";
            var s = new PluginSensor($"col{i + 1}", name, 0, Einheit(name));
            var t = new PluginText($"txt{i + 1}", name + " (Anzeige)", "--");
            var tr = new PluginText($"trend{i + 1}", name + " (Trend)", _zeichenSteht);
            var up = new PluginText($"steigt{i + 1}", name + " (steigt)", Leer);
            var down = new PluginText($"faellt{i + 1}", name + " (fällt)", Leer);
            var flat = new PluginText($"steht{i + 1}", name + " (steht)", _zeichenSteht);
            _sensoren.Add(s); _texte.Add(t); _trends.Add(tr); _verlauf.Add(new());
            _steigt.Add(up); _faellt.Add(down); _steht.Add(flat);
            var avg = new PluginText($"avg{i + 1}", name + " (Durchschnitt)", Leer);
            _schnitt.Add(avg);
            if (i < Math.Max(namen.Count, 1))
            {
                container.Entries.Add(s); container.Entries.Add(t); container.Entries.Add(tr);
                container.Entries.Add(up); container.Entries.Add(down); container.Entries.Add(flat);
                container.Entries.Add(avg);
            }
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
        PlaetzeAbgleichen(namen);
        _csvZuPlatz = namen.Select(n => _plaetze.IndexOf(n)).ToArray();
        DurchschnittNachlesen(datei);
        var werte = Spalten(letzte);
        if (werte.Count > 0) _stand.Value = werte[0].Trim();

        for (int k = 0; k < namen.Count; k++)
        {
            if (k + 1 >= werte.Count) break;
            int i = _csvZuPlatz[k];
            if (i < 0 || i >= _sensoren.Count) continue;
            if (Zahl(werte[k + 1], out var v))
            {
                _sensoren[i].Value = v;
                _texte[i].Value = veraltet ? "--" : v.ToString(_nachkomma == 0 ? "0" : "0." + new string('0', _nachkomma), CultureInfo.InvariantCulture) + (_sensoren[i].Unit ?? "");
                if (!veraltet) TrendSetzen(i, Trend(i, v));
            }
        }
        if (veraltet) { AlleVeraltet(); _stand.Value = "Protokollierung gestoppt?"; }
    }

    // Durchschnitt seit Beginn der aktuellen iCUE-Protokolldatei (= seit "Protokoll starten" nach dem PC-Start).
    // Liest nur die neu hinzugekommenen Zeilen, damit auch lange Protokolle schnell bleiben.
    // ---------- feste Plätze für die iCUE-Sensoren ----------
    private readonly List<string> _plaetze = new();
    private bool _plaetzeGeladen;
    private int[] _csvZuPlatz = Array.Empty<int>();
    private string PlaetzePfad => Path.Combine(_pluginOrdner, "plaetze.txt");

    private void PlaetzeAbgleichen(List<string>? kopf)
    {
        if (!_plaetzeGeladen)
        {
            _plaetzeGeladen = true;
            try
            {
                if (File.Exists(PlaetzePfad))
                    _plaetze.AddRange(File.ReadAllLines(PlaetzePfad, Encoding.UTF8).Where(z => z.Length > 0));
            }
            catch { }
        }
        if (kopf == null) return;
        // Neue Sensoren hinten anhängen – Temperaturen zuerst, damit beim allerersten Mal
        // die Wassertemperatur auf Platz 1 landet
        var neu = kopf.Where(n => n.Length > 0 && !_plaetze.Contains(n))
                      .OrderBy(n => Einheit(n) == "°C" ? 0 : 1).ToList();
        if (neu.Count == 0) return;
        foreach (var n in neu) if (_plaetze.Count < MaxSpalten) _plaetze.Add(n);
        try { File.WriteAllLines(PlaetzePfad, _plaetze, new UTF8Encoding(false)); } catch { }
    }

    private string? _avgDatei;
    private long _avgPos;
    private readonly double[] _avgSumme = new double[MaxSpalten];
    private readonly long[] _avgAnzahl = new long[MaxSpalten];
    private readonly List<PluginText> _schnitt = new();

    private void DurchschnittNachlesen(string datei)
    {
        using var fs = new FileStream(datei, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (datei != _avgDatei || fs.Length < _avgPos)
        {
            _avgDatei = datei; _avgPos = 0;
            Array.Clear(_avgSumme); Array.Clear(_avgAnzahl);
        }
        if (fs.Length == _avgPos) { SchnittAnzeigen(); return; }

        fs.Seek(_avgPos, SeekOrigin.Begin);
        var puffer = new byte[fs.Length - _avgPos];
        int gelesen = 0;
        while (gelesen < puffer.Length)
        {
            int n = fs.Read(puffer, gelesen, puffer.Length - gelesen);
            if (n <= 0) break;
            gelesen += n;
        }
        int ende = Array.LastIndexOf(puffer, (byte)'\n', gelesen - 1);
        if (ende < 0) return; // noch keine vollständige Zeile

        bool ersteZeileIstKopf = _avgPos == 0;
        var text = Encoding.UTF8.GetString(puffer, 0, ende + 1);
        _avgPos += ende + 1;

        foreach (var roh in text.Split('\n'))
        {
            var zeile = roh.Trim('\r', ' ', '﻿');
            if (zeile.Length == 0) continue;
            if (ersteZeileIstKopf) { ersteZeileIstKopf = false; continue; }
            var werte = Spalten(zeile);
            for (int k = 0; k + 1 < werte.Count && k < _csvZuPlatz.Length; k++)
            {
                int i = _csvZuPlatz[k];
                if (i < 0 || i >= MaxSpalten) continue;
                if (Zahl(werte[k + 1], out var v)) { _avgSumme[i] += v; _avgAnzahl[i]++; }
            }
        }
        SchnittAnzeigen();
    }

    private void SchnittAnzeigen()
    {
        var format = _nachkomma == 0 ? "0" : "0." + new string('0', _nachkomma);
        for (int i = 0; i < _schnitt.Count; i++)
        {
            if (_avgAnzahl[i] == 0) { _schnitt[i].Value = Leer; continue; }
            var schnitt = _avgSumme[i] / _avgAnzahl[i];
            _schnitt[i].Value = "(Ø " + schnitt.ToString(format, CultureInfo.InvariantCulture) + (_sensoren[i].Unit ?? "") + ")";
        }
    }

    private void AlleVeraltet()
    {
        foreach (var t in _texte) t.Value = "--";
        for (int i = 0; i < _trends.Count; i++) TrendSetzen(i, "");
        foreach (var v in _verlauf) v.Clear();
    }

    // Gesamtpfeil plus je ein Eintrag pro Richtung (nur sichtbar, wenn die Richtung zutrifft) – für eigene Farben in InfoPanel
    private void TrendSetzen(int i, string pfeil)
    {
        _trends[i].Value = pfeil.Length > 0 ? pfeil : Leer;
        _steigt[i].Value = pfeil == _zeichenSteigt ? pfeil : Leer;
        _faellt[i].Value = pfeil == _zeichenFaellt ? pfeil : Leer;
        _steht[i].Value = pfeil == _zeichenSteht ? pfeil : Leer;
    }

    // Vergleicht den Mittelwert der letzten 10 s mit dem Mittelwert von vor TrendSek Sekunden
    private string Trend(int i, float wert)
    {
        var jetzt = DateTime.UtcNow;
        var liste = _verlauf[i];
        liste.Add((jetzt, wert));
        liste.RemoveAll(e => (jetzt - e.zeit).TotalSeconds > _trendSek + MittelSek + 5);

        var neu = liste.Where(e => (jetzt - e.zeit).TotalSeconds <= MittelSek).Select(e => e.wert).ToList();
        var alt = liste.Where(e =>
        {
            var sek = (jetzt - e.zeit).TotalSeconds;
            return sek >= _trendSek && sek <= _trendSek + MittelSek;
        }).Select(e => e.wert).ToList();

        if (neu.Count == 0 || alt.Count == 0) return _zeichenSteht; // noch zu wenig Verlauf
        var diff = neu.Average() - alt.Average();
        if (diff >= _trendSchwelle - 0.001f) return _zeichenSteigt;
        if (diff <= -_trendSchwelle + 0.001f) return _zeichenFaellt;
        return _zeichenSteht;
    }

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
