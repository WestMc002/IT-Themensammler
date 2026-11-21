// IT-Themensammler – Update-Prüfung über die GitHub-Releases und Selbst-Update (auch des Dienstes)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITThemensammler
{
    /// <summary>Neueste Veröffentlichung auf GitHub.</summary>
    internal sealed class UpdateInfo
    {
        public string Version;   // z. B. 1.27.2026.1106 (ohne "v")
        public string Url;       // Release-Seite auf GitHub
        public string ZipUrl;    // angehängtes ZIP "IT-Themensammler-<Version>.zip" (EXE und Liesmich), nur mit gültiger Signatur
        public string Sha256;    // Prüfsumme des ZIP aus dem signierten Manifest (Hex, klein)
        public byte[] Manifest;  // signiertes Manifest (das Update-Skript prüft es selbst noch einmal)
        public string Problem;   // ohne gültige Signatur: "Nicht signiert." / "Signatur ungültig." / "Signatur nicht lesbar."
        public string ProblemDetail;
        public string Notes;     // Abschnitt "Änderungen" als Klartext (Tooltip der Hinweisleiste)
    }

    /// <summary>
    /// Fragt nur die öffentliche GitHub-Schnittstelle ab (keine Anmeldung, es werden keine Daten übertragen).
    /// Selbst-Update aus dem Verwaltungsfenster: ZIP laden, Hilfsskript starten, Fenster beenden. Das Skript beendet den Dienst,
    /// ersetzt EXE und Liesmich im Programmordner des Dienstes (sonst neben dem Verwaltungsfenster), startet den Dienst wieder
    /// und öffnet das Verwaltungsfenster neu. Mit "Automatisch aktualisieren" installiert der Dienst neue Versionen selbst
    /// (das Skript beendet und startet dann nur den Dienst).
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Repo = "WestMc002/IT-Themensammler";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        private const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
        /// <summary>Kein Fehler: Es gibt noch keine fertige Version (Vorabversionen zählen bei "latest" nicht).</summary>
        public const string NoRelease = "Auf GitHub ist noch keine fertige Version veröffentlicht.";

        /// <summary>
        /// Arbeitsordner für Download, Ergebnis und Marker: im Datenordner, nur für SYSTEM und Administratoren
        /// (Verwaltungsfenster und Dienst laufen beide mit Administratorrechten; dem Temp-Ordner wird nicht vertraut).
        /// </summary>
        public static string Folder { get { return Path.Combine(Speicher.StandardOrdner, "Update"); } }
        public static string LogFile { get { return Path.Combine(Folder, "Update.log"); } }
        /// <summary>Ordner bis 1.31 (Ergebnis eines Updates, das die alte Version gestartet hat).</summary>
        internal static string OldFolder { get { return Path.Combine(Path.GetTempPath(), "IT-Themensammler-Update"); } }

        /// <summary>Datenordner absichern und den Arbeitsordner anlegen (kein umgeleiteter Ordner).</summary>
        internal static void FolderAnlegen()
        {
            Speicher.OrdnerAnlegen();
            Directory.CreateDirectory(Folder);
            if ((File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0) throw new Exception("Der Update-Ordner ist umgeleitet: " + Folder);
        }

        /// <summary>Name des Release-Anhangs, der installiert wird.</summary>
        public static string ZipName(string version) { return "IT-Themensammler-" + version + ".zip"; }

        private static string UserAgent { get { return "IT-Themensammler/" + AppInfo.Version; } } // GitHub verlangt einen User-Agent

        private static void PrepareTls()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>Neueste Veröffentlichung holen. Wirft bei Netz- oder Formatfehlern (mit verständlicher Meldung).</summary>
        public static UpdateInfo Latest()
        {
            PrepareTls();
            var req = (HttpWebRequest)WebRequest.Create(Api);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.Accept = "application/vnd.github+json";
            req.UserAgent = UserAgent;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            string text;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var http = ex.Response as HttpWebResponse;
                if (http != null && http.StatusCode == HttpStatusCode.NotFound) throw new Exception(NoRelease);
                // kurze Meldung für das Info-Fenster (vor dem Knopf ist wenig Platz), der Originaltext steht hinter "Details"
                string brief = "Nicht erreichbar.";
                if (http != null && (int)http.StatusCode == 403) brief = "Zu viele Anfragen.";
                else if (ex.Status == WebExceptionStatus.NameResolutionFailure || ex.Status == WebExceptionStatus.ConnectFailure) brief = "Keine Verbindung.";
                else if (ex.Status == WebExceptionStatus.Timeout) brief = "Keine Antwort.";
                var shown = new Exception(brief);
                shown.Data["Detail"] = ex.Message;
                throw shown;
            }

            Dictionary<string, object> obj;
            try { obj = MiniJson.Parse(text) as Dictionary<string, object>; }
            catch { obj = null; }
            string tag = obj != null && obj.ContainsKey("tag_name") ? obj["tag_name"] as string : null;
            string version = (tag ?? "").TrimStart('v', 'V').Trim();
            if (obj == null || Parse(version) == null) throw new Exception("Antwort unlesbar.");

            var info = new UpdateInfo { Version = version, Url = ReleasesPage };
            string url = obj.ContainsKey("html_url") ? obj["html_url"] as string : null;
            // nur Seiten und Downloads des eigenen Repositorys verwenden
            if (url != null && url.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase)) info.Url = url;
            info.Notes = ExtractNotes(obj.ContainsKey("body") ? obj["body"] as string : null);
            // ZIP und signiertes Manifest: nur mit genau den erwarteten Namen und von der Download-Adresse dieses Repositorys
            string wanted = ZipName(version), wantedManifest = ManifestName(version);
            string prefix = "https://github.com/" + Repo + "/releases/download/";
            string zipUrl = null, manifestUrl = null;
            var assets = obj.ContainsKey("assets") ? obj["assets"] as List<object> : null;
            if (assets != null)
                foreach (object o in assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null || !a.ContainsKey("name") || !a.ContainsKey("browser_download_url")) continue;
                    string name = a["name"] as string, dl = a["browser_download_url"] as string;
                    if (name == null || dl == null || !dl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, wanted, StringComparison.Ordinal)) zipUrl = dl;
                    else if (string.Equals(name, wantedManifest, StringComparison.Ordinal)) manifestUrl = dl;
                }
            if (zipUrl == null) return info; // kein passendes ZIP: nur die Release-Seite
            // Die Prüfsumme kommt nur aus dem signierten Manifest, nicht von GitHub: Wer das Release austauscht,
            // kann ohne den privaten Schlüssel keine passende Signatur erzeugen.
            if (manifestUrl == null)
            {
                info.Problem = "Nicht signiert.";
                info.ProblemDetail = "Im Release fehlt die Signatur (" + wantedManifest + "). IT-Themensammler installiert nur signierte Updates.";
                return info;
            }
            byte[] manifest;
            try { manifest = DownloadSmall(manifestUrl, 8192); }
            catch (Exception ex)
            {
                info.Problem = "Signatur nicht lesbar.";
                info.ProblemDetail = "Die Signatur konnte nicht geladen werden: " + ex.Message;
                return info;
            }
            string sha, detail;
            string problem = CheckManifest(manifest, version, out sha, out detail);
            if (problem != null)
            {
                info.Problem = problem;
                info.ProblemDetail = detail;
                return info;
            }
            info.ZipUrl = zipUrl;
            info.Sha256 = sha;
            info.Manifest = manifest;
            return info;
        }

        /// <summary>Name des signierten Manifests neben dem ZIP.</summary>
        public static string ManifestName(string version) { return "IT-Themensammler-" + version + ".manifest"; }

        // ------------------------------------------------------------------ Signatur

        // Öffentliche Schlüssel (RSA 3072) des Herausgebers: Haupt- und Reserveschlüssel. Signiert wird mit dem
        // Hauptschlüssel; der Reserveschlüssel ersetzt ihn, falls er verloren geht.
        private const string MainKey = "<RSAKeyValue><Modulus>tLtylaHt5SgGCrTrEaM0H3Z/UqIQWSL2c3rCpuhZEYnCyJVDIYFCNuLbZ2OeapsJgCttOnwv5djvmpHTcGV6DeFM059O699xepTE0QWKYYM69sCFG4gunH2xh39bM20q9qWbui1Fyy+nN4cqzy26eR7tzXyClAJSS1zCKBuoF1uAtt2c1f1fcCHXdHsjWXZA6Z8OZeiIOizYSfiVHNIJFr8kSFQfnGRaygwp2ta27Hv0z0oKM6Zrvld/vXkgL2um7X1+GV24IzIVT7J8nxmar//yPoc6ugYhgcLjUMBgjwe/5m4REJQFWwntMF1uj5Jf+ehBFZS87kGtSvTykh7mea71JMdLpvAuhicMaP4T8Fr1uTYCPKR4cuiRI+6uogKVOZN23BuZS5iBj1zZkcmopNOXbQupsOkYp2QwejMHoEV7cEc71ZlRqqJed0vBqO8B5H21mrBGJm3kKHxa2QFzY5BZY7ZcC4EG2TZIkC8eZRNUXXmZOdAWGR+aPKeJg7DZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        private const string ReserveKey = "<RSAKeyValue><Modulus>zY/NkGbkd4IZHMMuGty2WLmQ0hm6lEZBql2v84BPf3NT+rpenfLK0kk7aKfRXgFS1G8tz+tHHd58GhioFFhnXN2syFTUus7u5csblitORGoNjTf9Rlicxw6Ut3hX7tkn2u+2PRKE5xOOL+epZV1YvBgr/W67WTt5RZEyfA0Vgn8llhrXaSD32iVNEzkeY5yy1bvQAmoxAk3KjNcD8Uuia9XP9qs/orDO0giIj/xsfY2D7qp/rkhSaIOnt/+gRWHbjhC/PGPga3dmcIMC1C3X0Z/meg6XgI+zWeXOOxFe8jCVRlaeGDoRJlJJGvg6uzIJlLCQeNxnSEufZCD10e+ex2xx75Gp3bqCvBvr49VXEC+pLxQteQMj2V5tuhq23MtIeSV0huaKZ1SPm1kxFjG5vj/11bo2VCkcx9dhfEBDdsucJqdUecbLJp58Y6smM2Ah4Qh7nILSNHHcpWey100qMN3WdwXsfzPVuRtD1c16KgC3TQIov2/2fGQGUxn1Awox</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        private static readonly string[] ManifestKeys = { "Programm", "Version", "Datei", "SHA256", "Schluessel", "Erstellt" };

        /// <summary>
        /// Manifest prüfen (UTF-8, LF, feste Reihenfolge, letzte Zeile "Signatur=" mit RSA-SHA256/PKCS#1 v1.5 über alle
        /// Bytes davor). Rückgabe: null = gültig (dann sha gesetzt), sonst Kurzmeldung; detail erklärt sie.
        /// </summary>
        public static string CheckManifest(byte[] data, string version, out string sha, out string detail)
        {
            sha = null;
            detail = null;
            const string Invalid = "Signatur ungültig.";
            byte[] marker = Encoding.ASCII.GetBytes("\nSignatur=");
            int at = IndexOf(data, marker);
            if (at < 0) { detail = "Im Manifest fehlt die Zeile „Signatur=“."; return Invalid; }
            byte[] signed = new byte[at + 1];
            Array.Copy(data, signed, signed.Length);
            byte[] signature;
            try
            {
                string text = Encoding.ASCII.GetString(data, at + marker.Length, data.Length - at - marker.Length).Trim();
                signature = Convert.FromBase64String(text);
            }
            catch { detail = "Die Signatur im Manifest ist nicht lesbar."; return Invalid; }
            string[] lines;
            try { lines = new UTF8Encoding(false, true).GetString(signed).Split('\n'); }
            catch { detail = "Das Manifest ist kein gültiger UTF-8-Text."; return Invalid; }
            if (lines.Length != ManifestKeys.Length + 1 || lines[ManifestKeys.Length].Length != 0)
            {
                detail = "Das Manifest hat nicht den erwarteten Aufbau.";
                return Invalid;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < ManifestKeys.Length; i++)
            {
                if (!lines[i].StartsWith(ManifestKeys[i] + "=", StringComparison.Ordinal))
                {
                    detail = "Im Manifest fehlt oder steht falsch: " + ManifestKeys[i] + ".";
                    return Invalid;
                }
                values[ManifestKeys[i]] = lines[i].Substring(ManifestKeys[i].Length + 1);
            }
            string keyName = values["Schluessel"];
            string keyXml = keyName == "Haupt" ? MainKey : keyName == "Reserve" ? ReserveKey : null;
            if (keyXml == null) { detail = "Unbekannter Schlüssel im Manifest: " + keyName; return Invalid; }
            bool ok;
            try
            {
                // PROV_RSA_AES (24): RSA mit SHA-256 auch unter .NET 4
                using (var rsa = new System.Security.Cryptography.RSACryptoServiceProvider(new System.Security.Cryptography.CspParameters(24)))
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(keyXml);
                    ok = rsa.VerifyData(signed, "SHA256", signature);
                }
            }
            catch (Exception ex) { detail = "Die Signatur konnte nicht geprüft werden: " + ex.Message; return Invalid; }
            if (!ok)
            {
                detail = "Die Signatur passt nicht zum " + keyName + "schlüssel von IT-Themensammler. " +
                         "Das Release wurde nicht vom Herausgeber signiert oder nachträglich verändert.";
                return Invalid;
            }
            // gültig signiert: Inhalt muss genau zu diesem Programm und dieser Version passen
            if (values["Programm"] != "IT-Themensammler") { detail = "Das Manifest gehört zu „" + values["Programm"] + "“, nicht zu IT-Themensammler."; return Invalid; }
            if (values["Version"] != version) { detail = "Das Manifest nennt Version " + values["Version"] + ", das Release " + version + "."; return Invalid; }
            if (values["Datei"] != ZipName(version)) { detail = "Das Manifest nennt die Datei „" + values["Datei"] + "“."; return Invalid; }
            if (!System.Text.RegularExpressions.Regex.IsMatch(values["SHA256"], "^[0-9a-f]{64}$")) { detail = "Die Prüfsumme im Manifest ist ungültig."; return Invalid; }
            sha = values["SHA256"];
            return null;
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        /// <summary>Kleine Datei laden (höchstens max Bytes).</summary>
        private static byte[] DownloadSmall(string url, int max)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = UserAgent;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > max) throw new Exception("Die Datei ist zu groß.");
                }
                return ms.ToArray();
            }
        }

        /// <summary>true, wenn die Version neuer als die laufende ist.</summary>
        public static bool IsNewer(string version)
        {
            Version a = Parse(version), b = Parse(AppInfo.Version);
            return a != null && b != null && a > b;
        }

        private static Version Parse(string v)
        {
            Version r;
            return System.Version.TryParse(v ?? "", out r) ? r : null;
        }

        /// <summary>Im Projektordner (mit Erstellen.bat) wird nicht automatisch ersetzt: dort entsteht die EXE beim Bauen.</summary>
        public static string InstallBlocker()
        {
            if (File.Exists(Path.Combine(Application.StartupPath, "Erstellen.bat")))
                return "Im Projektordner (mit Erstellen.bat) wird nicht automatisch aktualisiert – dort entsteht die EXE beim Erstellen.";
            return null;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "~update-test.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// ZIP herunterladen, Hilfsskript schreiben und starten. Das Skript wartet, bis dieses Programm beendet ist,
        /// ersetzt EXE und Liesmich und startet das Programm neu. Danach muss der Aufrufer das Programm beenden.
        /// </summary>
        public static Task Install(UpdateInfo release, Action<int> progress)
        {
            return Install(release, progress, false);
        }

        /// <summary>Diese Dateien übernimmt das Update aus dem ZIP – sonst nichts.</summary>
        private const string ExeName = "IT-Themensammler.exe", ManualName = "Liesmich.txt";

        /// <summary>
        /// ausDienst: vom Dienst selbst (automatisches Update) – das Skript beendet und startet nur den Dienst.
        /// Ablauf nach den globalen Vorgaben (signierte Updates): ZIP in einen eigenen Ordner je Versuch laden, SHA-256 aus dem
        /// signierten Manifest prüfen, Skript per -EncodedCommand starten (keine Skriptdatei). Das Skript prüft Signatur und
        /// SHA-256 in einem eigenen geschützten Arbeitsordner erneut und übernimmt nur IT-Themensammler.exe und Liesmich.txt.
        /// Rückgabe: der Prozess des Skripts (der Dienst überwacht ihn).
        /// </summary>
        public static async Task<Process> Install(UpdateInfo release, Action<int> progress, bool ausDienst)
        {
            if (release.Problem != null)
            {
                var unsigned = new Exception(release.Problem);
                unsigned.Data["Detail"] = release.ProblemDetail;
                throw unsigned;
            }
            if (string.IsNullOrEmpty(release.ZipUrl) || string.IsNullOrEmpty(release.Sha256) || release.Manifest == null)
                throw new Exception("Im Release ist keine passende ZIP-Datei.");
            string blocker = InstallBlocker();
            if (blocker != null) throw new Exception(blocker);
            // nur ein Update zur Zeit: von Hand und automatisch teilen sich diese Sperre; sie bleibt nach dem Start des
            // Skripts gesetzt (Verwaltungsfenster bzw. Dienst werden danach beendet)
            if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new Exception(Running);
            bool started = false;
            try
            {
                if (ScriptRunning()) throw new Exception(Running);
                PrepareTls();
                FolderAnlegen();
                CleanOldAttempts();
                // eigener Ordner je Versuch (im geschützten Datenordner): Download und Manifest
                string attempt = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(attempt);
                string zip = Path.Combine(attempt, ZipName(release.Version));
                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                    if (progress != null) client.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                    await client.DownloadFileTaskAsync(new Uri(release.ZipUrl), zip);
                }
                string hash = Sha256(zip);
                if (hash != release.Sha256)
                {
                    try { Directory.Delete(attempt, true); } catch { }
                    var kaputt = new Exception("Download beschädigt.");
                    kaputt.Data["Detail"] = "Die Prüfsumme (SHA-256) der heruntergeladenen Datei stimmt nicht mit dem signierten Manifest überein.\r\n" +
                                            "Erwartet: " + release.Sha256 + "\r\nErhalten: " + hash;
                    throw kaputt;
                }
                string manifest = Path.Combine(attempt, ManifestName(release.Version));
                File.WriteAllBytes(manifest, release.Manifest);
                try { File.Delete(LogFile); } catch { }
                // Eingerichteter Dienst: dessen Programmdatei ersetzen (Dienst wird dafür kurz beendet), sonst die laufende EXE
                string dienstExe = DienstSteuerung.DienstPfad() ?? DienstSteuerung.ProgrammPfad;
                bool dienst = DienstSteuerung.Zustand() != DienstZustand.NichtEingerichtet && File.Exists(dienstExe);
                string exe = dienst ? dienstExe : Application.ExecutablePath;
                // Arbeitsordner des Skripts: direkt unter ProgramData, nicht vorhersagbar, vom Skript geschützt angelegt
                string work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "IT-Themensammler-Update-" + Guid.NewGuid().ToString("N"));
                var vars = new StringBuilder();
                vars.Append("$ProcessId = ").Append(ausDienst ? 0 : Process.GetCurrentProcess().Id).Append("\n");
                Var(vars, "Zip", zip);
                Var(vars, "Manifest", manifest);
                Var(vars, "Attempt", attempt);
                Var(vars, "Exe", exe);
                Var(vars, "Version", release.Version);
                Var(vars, "Sha256", release.Sha256);
                Var(vars, "AssetName", ZipName(release.Version));
                Var(vars, "MainKey", MainKey);
                Var(vars, "ReserveKey", ReserveKey);
                Var(vars, "LockName", LockName);
                Var(vars, "Log", LogFile);
                Var(vars, "Work", work);
                Var(vars, "Service", dienst ? Dienst.DienstName : "");
                Var(vars, "ExeName", ExeName);
                Var(vars, "ManualName", ManualName);
                vars.Append("$NoStart = $").Append(ausDienst ? "true" : "false").Append("\n");
                var start = PowerShell(vars + Script);
                // Programmordner ohne Schreibrechte: Skript mit Administratorrechten starten
                // (das Verwaltungsfenster läuft ohnehin als Administrator, der Dienst als SYSTEM)
                if (!ausDienst && !CanWrite(Path.GetDirectoryName(exe))) start.Verb = "runas";
                Process p;
                try { p = Process.Start(start); }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223) throw new Exception("Update abgebrochen (Administratorrechte nicht erteilt).");
                    throw;
                }
                started = true;
                return p;
            }
            finally
            {
                if (!started) System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static int _busy;
        public const string Running = "Ein Update läuft bereits.";

        /// <summary>Sperre nach einem vorzeitig abgebrochenen Update wieder freigeben (Dienst).</summary>
        internal static void Freigeben() { System.Threading.Interlocked.Exchange(ref _busy, 0); }

        /// <summary>
        /// Sperre, die das Update-Skript bis zu seinem Ende hält: sitzungsübergreifend (Dienst in Sitzung 0,
        /// Verwaltungsfenster in der Sitzung des Benutzers).
        /// </summary>
        private const string LockName = @"Global\IT-Themensammler-Update";

        /// <summary>Läuft gerade ein Update (in diesem Prozess oder als Skript)?</summary>
        public static bool Busy { get { return _busy != 0 || ScriptRunning(); } }

        private static bool ScriptRunning()
        {
            try
            {
                System.Threading.Mutex m;
                if (!System.Threading.Mutex.TryOpenExisting(LockName, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; } // vom Skript als SYSTEM angelegt
            catch { return false; }
        }

        /// <summary>Ordner abgebrochener Versuche (älter als ein Tag) entfernen.</summary>
        private static void CleanOldAttempts()
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(Folder))
                {
                    var di = new DirectoryInfo(dir);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (di.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { di.Delete(true); } catch { }
                }
            }
            catch { }
        }

        internal static string Sha256(string datei)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var f = File.OpenRead(datei))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Windows PowerShell nur über den festen Systempfad (nie über den Suchpfad).</summary>
        internal static string PowerShellPfad
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"); }
        }

        /// <summary>PowerShell mit dem Skript als Kommando (-EncodedCommand): Es liegt nirgends als Datei, die man austauschen könnte.</summary>
        private static ProcessStartInfo PowerShell(string script)
        {
            string ps = PowerShellPfad;
            if (!File.Exists(ps)) throw new Exception("Windows PowerShell wurde nicht gefunden.");
            // Kommentarzeilen und Einrückung weglassen: Die Befehlszeile darf höchstens 32767 Zeichen lang sein
            var compact = new StringBuilder();
            foreach (string raw in script.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                compact.Append(line).Append('\n');
            }
            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                               Convert.ToBase64String(Encoding.Unicode.GetBytes(compact.ToString()));
            if (arguments.Length > 32000) throw new Exception("Das Update-Skript ist zu lang.");
            return new ProcessStartInfo(ps, arguments)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
        }

        /// <summary>Variable für das Skript als PowerShell-Text in einfachen Anführungszeichen.</summary>
        private static void Var(StringBuilder sb, string name, string value)
        {
            sb.Append('$').Append(name).Append(" = '");
            foreach (char c in value ?? "")
            {
                if (c == '\n' || c == '\r') throw new Exception("Ungültiger Wert für das Update.");
                sb.Append(c);
                // PowerShell nimmt auch typografische einfache Anführungszeichen als Begrenzer: verdoppeln
                if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B') sb.Append(c);
            }
            sb.Append("'\n");
        }

        /// <summary>Ergebnis des letzten Updates ("OK x" bzw. "FEHLER …") nach dem Neustart mit --nach-update.</summary>
        public static string LastResult()
        {
            try
            {
                if (File.Exists(LogFile)) return File.ReadAllText(LogFile, Encoding.UTF8).Trim();
                string alt = Path.Combine(OldFolder, "Update.log"); // Update, das noch die Version bis 1.31 gestartet hat
                return File.Exists(alt) ? File.ReadAllText(alt, Encoding.UTF8).Trim() : null;
            }
            catch { return null; }
        }

        // Update-Skript (läuft als Administrator bzw. SYSTEM). Davor setzt das Programm: $ProcessId, $Zip, $Manifest, $Attempt,
        // $Exe, $Version, $Sha256, $AssetName, $MainKey, $ReserveKey, $LockName, $Log, $Work, $Service, $ExeName, $ManualName, $NoStart.
        // Dem Download-Ordner wird nicht vertraut: eigener Arbeitsordner direkt unter ProgramData (Besitzer Administratoren,
        // schreiben nur SYSTEM und Administratoren), dort Signatur und SHA-256 prüfen und entpacken. Übernommen werden nur
        // IT-Themensammler.exe (Pflicht) und Liesmich.txt. Nur ASCII-Anführungszeichen verwenden.
        private const string Script = @"
$ErrorActionPreference = 'Stop'
$target = Split-Path $Exe -Parent
$workReady = $false
$stopped = $false
$result = $null
$lock = $null
$haveLock = $false
try {
  # nur ein Update zur Zeit (Sperre bis zum Ende dieses Skripts)
  $lock = New-Object System.Threading.Mutex($false, $LockName)
  try { $haveLock = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
  if (-not $haveLock) { throw 'Ein anderes Update laeuft gerade.' }
  if ($ProcessId -gt 0) { for ($i = 0; $i -lt 120 -and (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 } }
  Start-Sleep -Milliseconds 500
  $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
  $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
  $root = [Environment]::GetFolderPath('CommonApplicationData')
  if ((Split-Path $Work -Parent) -ne $root) { throw 'Unerwarteter Arbeitsordner.' }
  # alte Arbeitsordner frueherer Updates entfernen: nur eigene (Besitzer Administratoren bzw. SYSTEM), keine Verknuepfungen
  Get-ChildItem -LiteralPath $root -Directory -Filter 'IT-Themensammler-Update-*' -Force -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      if (-not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        $o = (Get-Acl -LiteralPath $_.FullName).GetOwner([System.Security.Principal.SecurityIdentifier])
        if ($o -eq $admins -or $o -eq $system) { [System.IO.Directory]::Delete($_.FullName, $true) }
      }
    } catch {}
  }
  # Arbeitsordner gleich mit Rechten anlegen: schreiben nur SYSTEM und Administratoren, lesen angemeldete Benutzer
  $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
  $none = [System.Security.AccessControl.PropagationFlags]::None
  $sec = New-Object System.Security.AccessControl.DirectorySecurity
  $sec.SetAccessRuleProtection($true, $false)
  $sec.SetOwner($admins)
  foreach ($sid in @($admins, $system)) {
    $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', $inherit, $none, 'Allow')))
  }
  $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $none, 'Allow')))
  if (Test-Path -LiteralPath $Work) { throw 'Der Arbeitsordner fuer das Update existiert bereits.' }
  [void][System.IO.Directory]::CreateDirectory($Work, $sec)
  $o = (Get-Acl -LiteralPath $Work).GetOwner([System.Security.Principal.SecurityIdentifier])
  if (-not ($o -eq $admins -or $o -eq $system)) { throw 'Der Arbeitsordner fuer das Update ist nicht geschuetzt.' }
  if ((Get-Item -LiteralPath $Work -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) { throw 'Der Arbeitsordner fuer das Update ist umgeleitet.' }
  $workReady = $true
  $zipWork = Join-Path $Work 'Update.zip'
  $manifestWork = Join-Path $Work 'Update.manifest'
  Copy-Item -LiteralPath $Zip -Destination $zipWork -Force
  Copy-Item -LiteralPath $Manifest -Destination $manifestWork -Force
  # Signatur des Manifests selbst pruefen (an der Kopie, die verwendet wird)
  $bytes = [System.IO.File]::ReadAllBytes($manifestWork)
  if ($bytes.Length -gt 8192) { throw 'Das Manifest ist zu gross.' }
  $marker = [System.Text.Encoding]::ASCII.GetBytes([string][char]10 + 'Signatur=')
  $at = -1
  for ($i = 0; $i -le $bytes.Length - $marker.Length -and $at -lt 0; $i++) {
    $j = 0
    while ($j -lt $marker.Length -and $bytes[$i + $j] -eq $marker[$j]) { $j++ }
    if ($j -eq $marker.Length) { $at = $i }
  }
  if ($at -lt 0) { throw 'Im Manifest fehlt die Signatur.' }
  $signed = New-Object byte[] ($at + 1)
  [System.Array]::Copy($bytes, $signed, $at + 1)
  $sig = [System.Convert]::FromBase64String([System.Text.Encoding]::ASCII.GetString($bytes, $at + $marker.Length, $bytes.Length - $at - $marker.Length).Trim())
  $lines = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($signed).Split([char]10)
  $names = @('Programm', 'Version', 'Datei', 'SHA256', 'Schluessel', 'Erstellt')
  if ($lines.Count -ne 7 -or $lines[6] -ne '') { throw 'Das Manifest hat nicht den erwarteten Aufbau.' }
  $mv = @{}
  for ($i = 0; $i -lt 6; $i++) {
    $p = $names[$i] + '='
    if (-not $lines[$i].StartsWith($p, [System.StringComparison]::Ordinal)) { throw ('Im Manifest fehlt ' + $names[$i] + '.') }
    $mv[$names[$i]] = $lines[$i].Substring($p.Length)
  }
  if ($mv['Schluessel'] -ceq 'Haupt') { $keyXml = $MainKey }
  elseif ($mv['Schluessel'] -ceq 'Reserve') { $keyXml = $ReserveKey }
  else { throw 'Unbekannter Schluessel im Manifest.' }
  $csp = New-Object System.Security.Cryptography.CspParameters -ArgumentList 24
  $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList $csp
  $rsa.PersistKeyInCsp = $false
  $rsa.FromXmlString($keyXml)
  $valid = $rsa.VerifyData($signed, 'SHA256', $sig)
  $rsa.Clear()
  if (-not $valid) { throw 'Die Signatur des Updates ist ungueltig.' }
  if ($mv['Programm'] -cne 'IT-Themensammler' -or $mv['Version'] -cne $Version -or $mv['Datei'] -cne $AssetName) { throw 'Das Manifest passt nicht zu diesem Update.' }
  if ($mv['SHA256'] -cnotmatch '^[0-9a-f]{64}$') { throw 'Die Pruefsumme im Manifest ist ungueltig.' }
  $expected = $mv['SHA256']
  if ($expected -cne $Sha256) { throw 'Die Pruefsumme im Manifest weicht von der Pruefung im Programm ab.' }
  # Pruefsumme aus dem geprueften Manifest an der Datei pruefen, die tatsaechlich entpackt wird
  $hash = (Get-FileHash -LiteralPath $zipWork -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($hash -cne $expected) { throw ('Die Pruefsumme des Updates stimmt nicht (erwartet ' + $expected + ', erhalten ' + $hash + ').') }
  $dir = Join-Path $Work 'Neu'
  Expand-Archive -LiteralPath $zipWork -DestinationPath $dir -Force
  # nur bekannte Dateien uebernehmen
  $new = Join-Path $dir $ExeName
  if (-not (Test-Path -LiteralPath $new -PathType Leaf)) { throw ('Im ZIP fehlt ' + $ExeName) }
  $pairs = @(,@($new, $Exe))
  $manual = Join-Path $dir $ManualName
  if (Test-Path -LiteralPath $manual -PathType Leaf) { $pairs += ,@($manual, (Join-Path $target $ManualName)) }
  if ($Service) {
    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $Service -Force; $stopped = $true }
    for ($i = 0; $i -lt 20; $i++) { if (-not (Get-Process | Where-Object { $_.Path -eq $Exe })) { break }; Start-Sleep -Milliseconds 500 }
  }
  # Rueckfallkopien im eigenen Arbeitsordner, nicht neben den Programmdateien
  $alt = Join-Path $Work 'Alt'
  New-Item -ItemType Directory -Path $alt -Force | Out-Null
  $done = @()
  $i = 0
  try {
    foreach ($p in $pairs) {
      $i++
      $bak = Join-Path $alt ([string]$i)
      $had = Test-Path -LiteralPath $p[1]
      if ($had) { Copy-Item -LiteralPath $p[1] -Destination $bak -Force }
      $done += ,@($p[1], $bak, $had)
      Copy-Item -LiteralPath $p[0] -Destination $p[1] -Force
    }
  } catch {
    foreach ($d in $done) {
      if ($d[2]) { Copy-Item -LiteralPath $d[1] -Destination $d[0] -Force }
      else { Remove-Item -LiteralPath $d[0] -Force -ErrorAction SilentlyContinue }
    }
    throw
  }
  $result = 'OK ' + $Version
} catch {
  $result = 'FEHLER ' + $_.Exception.Message
}
if ($stopped) { try { Start-Service -Name $Service } catch { $result = 'FEHLER Dienst startet nicht: ' + $_.Exception.Message } }
try { $result | Out-File -LiteralPath $Log -Encoding utf8 } catch {}
if ($workReady) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath $Attempt -Recurse -Force -ErrorAction SilentlyContinue
if ($haveLock) { try { $lock.ReleaseMutex() } catch {} }
if (-not $NoStart) { Start-Process -FilePath $Exe -ArgumentList '--nach-update' }
";


        /// <summary>Aus dem Release-Text nur den Abschnitt "## Änderungen", Punkte als "•", ohne Markdown.</summary>
        internal static string ExtractNotes(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            var lines = body.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            bool inside = false, any = false;
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.StartsWith("#"))
                {
                    if (inside) break;
                    inside = l.TrimStart('#').Trim().StartsWith("Änderungen", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inside || l.Length == 0) continue;
                if (l.StartsWith("- ") || l.StartsWith("* ")) l = "•  " + l.Substring(2).Trim();
                l = l.Replace("**", "").Replace("`", "");
                if (any) sb.Append("\r\n");
                sb.Append(l);
                any = true;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Gemeinsamer Stand der Update-Prüfung von Dienst und Verwaltungsfenster (in einstellungen.json):
    /// automatisch etwa 15 Sekunden nach jedem Start (ohne Sperre), danach alle 6 Stunden, wenn die letzte Prüfung
    /// mindestens 20 Stunden her ist. Mit "Automatisch aktualisieren" installiert der Dienst eine neue Version selbst,
    /// sobald nichts los ist (sonst alle 5 Minuten ein neuer Versuch).
    /// </summary>
    internal static class UpdatePruefung
    {
        private static System.Threading.Timer _timer, _installTimer;
        private static bool _ersterLauf;
        private static int _installiert; // 1 = Update ist gestartet, der Dienst wird gleich beendet
        /// <summary>Nach einem Fehlschlag frühestens dann wieder versuchen (nicht alle 5 Minuten neu laden).</summary>
        private static DateTime _naechsterVersuch = DateTime.MinValue;
        /// <summary>Vom Dienst gestartetes Update: das Ergebnis meldet der Dienst nach dem Neustart.</summary>
        private static string Marker { get { return Path.Combine(UpdateCheck.Folder, "Dienst-Update.txt"); } }
        private static string OldMarker { get { return Path.Combine(UpdateCheck.OldFolder, "Dienst-Update.txt"); } }
        /// <summary>Seit wann eine neue Version nur wegen Betrieb in der Weboberfläche wartet (höchstens 24 Stunden).</summary>
        private static DateTime _wartetSeit = DateTime.MinValue;

        public static DateTime Letzte
        {
            get
            {
                DateTime t;
                return DateTime.TryParse(Speicher.EinstellungenLesen().updateLetzte, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)
                    ? t.ToUniversalTime() : DateTime.MinValue;
            }
        }

        public static bool Auto
        {
            get { return Speicher.EinstellungenLesen().updateAuto != false; }
            set { Speicher.EinstellungenAendern(e => e.updateAuto = value); }
        }

        /// <summary>Neue Version ohne Rückfrage installieren (nur zusammen mit "Automatisch prüfen").</summary>
        public static bool AutoInstall
        {
            get { return Speicher.EinstellungenLesen().updateAutoInstall == true; }
            set { Speicher.EinstellungenAendern(e => e.updateAutoInstall = value); }
        }

        /// <summary>Automatische Prüfung eingeschaltet und die letzte mindestens 20 Stunden her?</summary>
        public static bool Faellig()
        {
            return !Demo.Active && Auto && (DateTime.UtcNow - Letzte).TotalHours >= 20;
        }

        /// <summary>Auf GitHub prüfen und das Ergebnis speichern (wirft bei Fehlern).</summary>
        public static UpdateInfo Pruefen()
        {
            var info = UpdateCheck.Latest();
            bool neuer = UpdateCheck.IsNewer(info.Version);
            Speicher.EinstellungenAendern(e =>
            {
                e.updateLetzte = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                e.updateVersion = neuer ? info.Version : "";
                e.updateSeite = neuer ? info.Url : "";
            });
            return info;
        }

        /// <summary>
        /// Im Dienst: etwa 15 Sekunden nach dem Start prüfen (ohne 20-Stunden-Sperre), danach alle 6 Stunden nachsehen,
        /// ob die Prüfung fällig ist. Alle 5 Minuten: neue Version automatisch installieren, wenn eingeschaltet und nichts los ist.
        /// </summary>
        public static void DienstStarten()
        {
            DienstBeenden();
            if (Demo.Active) return;
            UpdateErgebnisMelden();
            _ersterLauf = true;
            _timer = new System.Threading.Timer(o =>
            {
                bool start = _ersterLauf;
                _ersterLauf = false;
                try
                {
                    if (!Auto || (!start && !Faellig())) return;
                    var info = Pruefen();
                    if (UpdateCheck.IsNewer(info.Version))
                    {
                        Speicher.Protokoll("Update verfügbar: Version " + info.Version + " (installiert: " + AppInfo.Version + ")");
                        DienstAutoUpdate();
                    }
                }
                catch (Exception ex)
                {
                    string more = ex.Data["Detail"] as string;
                    Speicher.Protokoll("Update-Prüfung nicht möglich: " + ex.Message + (more != null ? " " + more : ""));
                }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromHours(6));
            _installTimer = new System.Threading.Timer(o => DienstAutoUpdate(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public static void DienstBeenden()
        {
            var t = _timer;
            _timer = null;
            if (t != null) t.Dispose();
            t = _installTimer;
            _installTimer = null;
            if (t != null) t.Dispose();
        }

        /// <summary>
        /// Woran das automatische Update im Dienst gerade warten muss (null = nichts los): Verwaltungsfenster offen,
        /// AD-Abgleich läuft oder in den letzten 5 Minuten hat jemand in der Weboberfläche etwas geändert.
        /// </summary>
        private const string WebBetrieb = "Weboberfläche in Benutzung";

        private static string Beschaeftigt()
        {
            if (AdAbgleich.Laeuft) return "AD-Abgleich läuft";
            if ((DateTime.UtcNow - WebServer.LetzteAenderung).TotalMinutes < 5) return WebBetrieb;
            try
            {
                int self = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Application.ExecutablePath)))
                    using (p) if (p.Id != self) return "Verwaltungsfenster offen";
            }
            catch { }
            return null;
        }

        /// <summary>Im Dienst: bekannte neue Version ohne Rückfrage installieren (nur mit "Automatisch prüfen" und "Automatisch aktualisieren").</summary>
        private static void DienstAutoUpdate()
        {
            try
            {
                if (Demo.Active || !Auto || !AutoInstall) return;
                if (!UpdateCheck.IsNewer(Speicher.EinstellungenLesen().updateVersion)) return;
                if (UpdateCheck.InstallBlocker() != null || DateTime.UtcNow < _naechsterVersuch) return;
                string grund = Beschaeftigt();
                if (grund != null)
                {
                    // Betrieb in der Weboberfläche verschiebt höchstens 24 Stunden (sonst ließe sich ein Update dauerhaft aufhalten)
                    if (grund != WebBetrieb) return; // nächster Versuch in 5 Minuten
                    if (_wartetSeit == DateTime.MinValue) _wartetSeit = DateTime.UtcNow;
                    if ((DateTime.UtcNow - _wartetSeit).TotalHours < 24) return;
                }
                if (System.Threading.Interlocked.Exchange(ref _installiert, 1) == 1) return;
                var info = UpdateCheck.Latest(); // frisch abfragen: Adresse des ZIP
                if (!UpdateCheck.IsNewer(info.Version) || string.IsNullOrEmpty(info.ZipUrl))
                {
                    if (info.Problem != null)
                    {
                        // ohne gültige Signatur wird nicht installiert (Hinweis höchstens stündlich)
                        _naechsterVersuch = DateTime.UtcNow.AddHours(1);
                        Speicher.Protokoll("Automatisches Update auf Version " + info.Version + " nicht möglich: " + info.Problem + " " + info.ProblemDetail);
                    }
                    System.Threading.Interlocked.Exchange(ref _installiert, 0);
                    return;
                }
                Speicher.Protokoll("Automatisches Update auf Version " + info.Version + " wird installiert (der Dienst startet danach neu)");
                UpdateCheck.FolderAnlegen();
                File.WriteAllText(Marker, info.Version);
                // das Skript beendet den Dienst, ersetzt die Dateien und startet ihn neu
                var hilfe = UpdateCheck.Install(info, null, true).Result;
                if (hilfe != null) Task.Run(() => HilfeUeberwachen(hilfe));
            }
            catch (Exception ex)
            {
                _naechsterVersuch = DateTime.UtcNow.AddHours(1);
                System.Threading.Interlocked.Exchange(ref _installiert, 0);
                try { File.Delete(Marker); } catch { }
                var inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                string more = inner.Data["Detail"] as string;
                Speicher.Protokoll("Automatisches Update nicht möglich: " + inner.Message + (more != null ? " " + more : ""));
            }
        }

        /// <summary>
        /// Ein erfolgreiches Update beendet diesen Dienst. Endet das Hilfsskript, während der Dienst noch läuft, ist es vorher
        /// abgebrochen (z. B. Prüfsumme oder Entpacken): Ergebnis protokollieren, Sperre freigeben, in einer Stunde erneut versuchen.
        /// </summary>
        private static void HilfeUeberwachen(Process hilfe)
        {
            try
            {
                using (hilfe)
                {
                    if (!hilfe.WaitForExit(30 * 60 * 1000))
                    {
                        Speicher.Protokoll("Automatisches Update: das Hilfsskript läuft seit 30 Minuten – es wird beendet");
                        try { hilfe.Kill(); } catch { }
                    }
                }
                System.Threading.Thread.Sleep(5000); // falls der Dienst gerade beendet wird
                string result = UpdateCheck.LastResult();
                try { File.Delete(Marker); } catch { }
                Speicher.Protokoll("Automatisches Update fehlgeschlagen: " +
                    (result != null ? result.Replace("FEHLER", "").Trim() : "kein Ergebnis") + " – die bisherige Version läuft weiter, nächster Versuch in einer Stunde");
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Automatisches Update überwachen", ex);
            }
            finally
            {
                _naechsterVersuch = DateTime.UtcNow.AddHours(1);
                UpdateCheck.Freigeben();
                System.Threading.Interlocked.Exchange(ref _installiert, 0);
            }
        }

        /// <summary>Nach einem vom Dienst gestarteten Update: Ergebnis ins Protokoll und in die Ereignisanzeige.</summary>
        private static void UpdateErgebnisMelden()
        {
            try
            {
                bool neu = File.Exists(Marker), alt = File.Exists(OldMarker);
                if (!neu && !alt) return;
                if (neu) File.Delete(Marker);
                if (alt) File.Delete(OldMarker);
                string result = UpdateCheck.LastResult();
                if (result == null) return;
                if (result.StartsWith("OK"))
                    Speicher.Protokoll("Automatisches Update auf Version " + result.Substring(2).Trim() + " installiert");
                else
                    Speicher.Protokoll("Automatisches Update fehlgeschlagen: " + result.Replace("FEHLER", "").Trim() + " – die bisherige Version läuft weiter");
            }
            catch { }
        }
    }

    internal enum UpdateKind { Unknown, Current, Available, Failed, Checking }

    /// <summary>Stand der Update-Prüfung für das Info-Fenster.</summary>
    internal sealed class UpdateStatus
    {
        public readonly UpdateKind Kind;
        public readonly string Title, Detail;
        /// <summary>Technischer Originaltext eines Fehlers für "Details" (kann fehlen).</summary>
        public string More;
        public UpdateStatus(UpdateKind kind, string title, string detail) { Kind = kind; Title = title; Detail = detail ?? ""; }
    }

    /// <summary>Verbindung des Info-Fensters zur Update-Prüfung im Hauptfenster (wie Fensterpositionen).</summary>
    internal sealed class UpdateHooks
    {
        public Func<UpdateStatus> Status;
        public Func<bool> Auto;
        public Action<bool> SetAuto;
        /// <summary>"Automatisch aktualisieren" (ohne diese beiden fehlt die Zeile im Info-Fenster).</summary>
        public Func<bool> AutoInstall;
        public Action<bool> SetAutoInstall;
        public Func<Task<UpdateStatus>> CheckNow;
        public Func<bool> CanInstall;
        public Action Install;
        public Action OpenPage;
    }

    /// <summary>Symbol des Update-Stands (Größe wie die Navigation: 11 pt).</summary>
    internal sealed class StatusGlyph : Control
    {
        private readonly float k;
        private char _glyph;
        private Color _color;

        public StatusGlyph(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        public void Set(char glyph, Color color)
        {
            _glyph = glyph;
            _color = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Ui.Card);
            if (_glyph == '\0') return;
            int gs = (int)Math.Round(11f * 96f / 72f * k);
            var img = MenuHelper.Glyph(_glyph, _color, gs);
            if (img == null) return;
            e.Graphics.DrawImage(img, (Width - gs) / 2, (Height - gs) / 2, gs, gs);
            img.Dispose();
        }
    }

    /// <summary>Kleiner JSON-Leser (Objekte, Listen, Texte, Zahlen, true/false/null) für die GitHub-Antwort.</summary>
    internal static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Skip(s, ref i);
            if (i != s.Length) throw new InvalidDataException("JSON: unerwartete Zeichen am Ende.");
            return v;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new InvalidDataException("JSON: unerwartetes Ende.");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    Expect(s, ref i, ':');
                    d[key] = Value(s, ref i);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return l;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new InvalidDataException("JSON: unerwartetes Zeichen '" + c + "'.");
            double num;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out num);
            return num;
        }

        private static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new InvalidDataException("JSON: '" + c + "' erwartet.");
            i++;
        }

        private static string Str(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new InvalidDataException("JSON: Text ohne Ende.");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new InvalidDataException("JSON: Text ohne Ende.");
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new InvalidDataException("JSON: \\u unvollständig.");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
        }
    }

    /// <summary>
    /// Hinweisleiste oben im Inhalt: neue Version mit "Jetzt aktualisieren", "Release-Seite" und "Später"
    /// (wie Fensterpositionen wiederherstellen).
    /// </summary>
    internal sealed class UpdateBar : Control
    {
        private readonly float k;
        public readonly ModernButton Install, Page, Later;

        public UpdateBar(float scale)
        {
            k = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Back;
            Font = new Font("Segoe UI Semibold", 9.5f);
            Install = new ModernButton("Jetzt aktualisieren", MenuHelper.Download, true, k) { Margin = Padding.Empty };
            Page = new ModernButton("Release-Seite", MenuHelper.Link, false, k) { Margin = Padding.Empty };
            Later = new ModernButton("Später", '\0', false, k) { Margin = Padding.Empty };
            foreach (var b in new[] { Install, Page, Later })
            {
                b.HeightUnits = 32;
                b.UpdateSize();
                Controls.Add(b);
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        /// <summary>Farbe des Fensters hinter der Leiste (für die Ecken außerhalb der abgerundeten Fläche).</summary>
        private Color Hintergrund { get { return Parent != null ? Parent.BackColor : Ui.Back; } }

        /// <summary>
        /// Die Knöpfe zeichnen ihre abgerundeten Ecken in der Hintergrundfarbe ihres Elternelements (BackColor der Leiste).
        /// Deshalb ist BackColor die eingefärbte Fläche der Leiste und nicht die Fensterfarbe – sonst stehen helle Ecken um die Knöpfe.
        /// </summary>
        private void FlaecheSetzen()
        {
            Color fill = Accent.Mix(Ui.Primary, Hintergrund, 0.14);
            if (BackColor != fill) BackColor = fill;
        }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); FlaecheSetzen(); }
        protected override void OnParentBackColorChanged(EventArgs e) { FlaecheSetzen(); base.OnParentBackColorChanged(e); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            FlaecheSetzen();
            if (Install == null || Page == null || Later == null) return; // schon beim Setzen der Schrift im Konstruktor
            int x = Width - (int)(12 * k);
            foreach (var b in new[] { Later, Page, Install })
            {
                x -= b.Width;
                b.Location = new Point(x, (Height - b.Height) / 2);
                x -= (int)(8 * k);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FlaecheSetzen();
            Color back = Hintergrund;
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8 * k))
            using (var b = new SolidBrush(BackColor))
            using (var pen = new Pen(Accent.Mix(Ui.Primary, back, 0.45)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            int x = (int)(16 * k), gs = (int)Math.Round(11f * 96f / 72f * k);
            var img = MenuHelper.Glyph(MenuHelper.Download, Ui.AccentText, gs);
            if (img != null)
            {
                g.DrawImage(img, x, (Height - gs) / 2, gs, gs);
                img.Dispose();
            }
            x += (int)(34 * k);
            int right = (Install != null ? Install.Left : Width) - (int)(12 * k);
            var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int platz = Math.Max(10, right - x);
            // bei wenig Platz zweizeilig: oben fett "Neue Version x", darunter grau "Installiert ist y" (globale Vorgabe)
            const string Trenner = " verfügbar – installiert ist ";
            int t = (Text ?? "").IndexOf(Trenner, StringComparison.Ordinal);
            if (t > 0 && TextRenderer.MeasureText(g, Text, Font, new Size(int.MaxValue, int.MaxValue), flags).Width > platz)
            {
                string oben = Text.Substring(0, t);
                string unten = "Installiert ist " + Text.Substring(t + Trenner.Length).TrimEnd('.');
                using (var klein = new Font("Segoe UI", 9f))
                {
                    int h1 = TextRenderer.MeasureText(g, oben, Font, new Size(int.MaxValue, int.MaxValue), flags).Height;
                    int h2 = TextRenderer.MeasureText(g, unten, klein, new Size(int.MaxValue, int.MaxValue), flags).Height;
                    int abstand = (int)Math.Round(1 * k);
                    int y = (Height - h1 - h2 - abstand) / 2;
                    TextRenderer.DrawText(g, oben, Font, new Rectangle(x, y, platz, h1), Ui.Text, flags);
                    TextRenderer.DrawText(g, unten, klein, new Rectangle(x, y + h1 + abstand, platz, h2), Ui.Muted, flags);
                }
                return;
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, platz, Height), Ui.Text,
                (flags & ~TextFormatFlags.NoPadding) | TextFormatFlags.VerticalCenter);
        }
    }
}