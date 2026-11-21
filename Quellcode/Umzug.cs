// IT-Themensammler – Umzug vom früheren Programmnamen (Datenordner, Programmordner, Dienst, Firewall)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ITThemensammler
{
    /// <summary>
    /// Bis Version 1.22 hatte der Programmname noch einen Namenszusatz ("<Zusatz> IT-Themensammler"). Neue Versionen
    /// ziehen eine solche Installation selbst um: Daten aus %ProgramData%\* IT-Themensammler, Programm aus
    /// C:\Program Files\* IT-Themensammler, Dienst (Programmpfad und Anzeigename) und Firewall-Regeln. Der Dienstname
    /// "ITThemensammler" bleibt gleich. Der alte Name wird nicht ausgeschrieben, sondern über das Muster gesucht.
    /// Ablauf beim automatischen Update aus 1.22: Das alte Update-Skript ersetzt die EXE im alten Programmordner (das ZIP
    /// enthält dafür eine Kopie unter dem alten Namen) und startet den Dienst. Der Dienst (neue Version) zieht dann die Daten um,
    /// kopiert sich in den neuen Programmordner, stellt den Dienst darauf um und startet sich von dort neu; beim nächsten
    /// Start räumt er den alten Programmordner weg.
    /// </summary>
    internal static class Umzug
    {
        /// <summary>Suchmuster für Ordner und Firewall-Regeln unter dem früheren Namen ("* IT-Themensammler").</summary>
        private static string Muster { get { return "* " + AppInfo.Name; } }

        /// <summary>Früherer Ordner unter basis (bei mehreren der mit daten.json bzw. EXE), sonst null.</summary>
        private static string AlterOrdner(Environment.SpecialFolder basis, string kennDatei)
        {
            try
            {
                string[] treffer = Directory.GetDirectories(Environment.GetFolderPath(basis), Muster);
                foreach (string d in treffer)
                    if (kennDatei == null || File.Exists(Path.Combine(d, kennDatei))) return d;
                return treffer.Length > 0 ? treffer[0] : null;
            }
            catch { return null; }
        }

        private static string AlterDatenordner
        {
            get { return AlterOrdner(Environment.SpecialFolder.CommonApplicationData, "daten.json"); }
        }

        public static string AlterProgrammordner
        {
            get { return AlterOrdner(Environment.SpecialFolder.ProgramFiles, null); }
        }

        /// <summary>
        /// Daten übernehmen, wenn es im alten Datenordner eine daten.json gibt, die neuer ist als die im neuen Ordner
        /// (oder dort keine). Der alte Ordner wird danach gelöscht bzw. umbenannt, der Umzug passiert also nur einmal.
        /// </summary>
        public static void Daten()
        {
            if (Demo.Active) return;
            string alt = AlterDatenordner, neu = Speicher.StandardOrdner, meldung = null;
            if (alt == null) return;
            try
            {
                string altDatei = Path.Combine(alt, "daten.json"), neuDatei = Path.Combine(neu, "daten.json");
                if (!File.Exists(altDatei)) return;
                if (File.Exists(neuDatei) && File.GetLastWriteTimeUtc(neuDatei) >= File.GetLastWriteTimeUtc(altDatei)) return;
                bool verschoben = false;
                if (!Directory.Exists(neu))
                {
                    try { Directory.Move(alt, neu); verschoben = true; }
                    catch { }
                }
                if (!verschoben)
                {
                    Kopieren(alt, neu);
                    try { Directory.Delete(alt, true); }
                    catch
                    {
                        // Datei noch offen: wenigstens umbenennen, damit der Umzug nicht noch einmal läuft
                        try { Directory.Move(alt, alt + " (übernommen " + DateTime.Now.ToString("yyyy-MM-dd HH-mm") + ")"); } catch { }
                    }
                }
                meldung = "Daten aus „" + alt + "“ übernommen (früherer Programmname)";
            }
            catch (Exception ex)
            {
                try { Speicher.Fehler("Umzug der Daten aus „" + alt + "“ nicht möglich", ex); } catch { }
                return;
            }
            try { Speicher.Protokoll(meldung); } catch { }
        }

        private static void Kopieren(string von, string nach)
        {
            Directory.CreateDirectory(nach);
            foreach (string d in Directory.GetDirectories(von))
                Kopieren(d, Path.Combine(nach, Path.GetFileName(d)));
            foreach (string f in Directory.GetFiles(von))
                File.Copy(f, Path.Combine(nach, Path.GetFileName(f)), true);
        }

        /// <summary>
        /// Läuft noch ein Dienst der alten Version (vor 1.23)? Dann zieht das Verwaltungsfenster die Daten nicht um,
        /// sonst schriebe der alte Dienst weiter in den alten Ordner. Der Umzug folgt dann mit "Dienst aktualisieren".
        /// </summary>
        public static bool AlterDienstLaeuft()
        {
            try
            {
                if (DienstSteuerung.Zustand() != DienstZustand.Laeuft) return false;
                string pfad = DienstSteuerung.DienstPfad();
                if (string.IsNullOrEmpty(pfad) || !File.Exists(pfad)) return false;
                Version v;
                return Version.TryParse(FileVersionInfo.GetVersionInfo(pfad).FileVersion ?? "", out v) && v < new Version(1, 23);
            }
            catch { return false; }
        }

        /// <summary>
        /// Im Dienst: Läuft er noch aus dem alten Programmordner, sich in den neuen kopieren, Dienst und Firewall umstellen und
        /// in ein paar Sekunden von dort neu starten (true). Sonst Reste des alten Programmordners wegräumen (false).
        /// </summary>
        public static bool Programm()
        {
            if (Demo.Active) return false;
            string exe = Application.ExecutablePath, altOrdner = AlterProgrammordner;
            if (altOrdner == null) return false;
            if (!DienstSteuerung.SelbePfade(Path.GetDirectoryName(exe), altOrdner))
            {
                try { Directory.Delete(altOrdner, true); }
                catch { } // z. B. Verwaltungsfenster läuft noch von dort: beim nächsten Start
                return false;
            }
            try
            {
                Directory.CreateDirectory(DienstSteuerung.ProgrammOrdner);
                File.Copy(exe, DienstSteuerung.ProgrammPfad, true);
                string liesmich = Path.Combine(altOrdner, "Liesmich.txt");
                if (File.Exists(liesmich)) File.Copy(liesmich, Path.Combine(DienstSteuerung.ProgrammOrdner, "Liesmich.txt"), true);
                DienstSteuerung.PfadSetzen();
                Firewall();
                Speicher.Protokoll("Programm nach " + DienstSteuerung.ProgrammOrdner + " umgezogen (früherer Programmname), der Dienst startet neu");
                // Neustart aus dem neuen Ordner (das Hilfsprogramm läuft weiter, wenn der Dienst beendet wird)
                Process.Start(new ProcessStartInfo(UpdateCheck.PowerShellPfad,
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"Start-Sleep -Seconds 5; Restart-Service -Name '" + Dienst.DienstName + "' -Force\"")
                { UseShellExecute = false, CreateNoWindow = true });
                return true;
            }
            catch (Exception ex)
            {
                try { Speicher.Fehler("Umzug des Programms aus „" + altOrdner + "“ nicht möglich", ex); } catch { }
                return false;
            }
        }

        /// <summary>Firewall-Regeln unter dem früheren Namen ("* IT-Themensammler", auch "… (Anwendungsproxy)") durch neue ersetzen.</summary>
        public static void Firewall()
        {
            // Rückgabe 1: es gab alte Regeln (jetzt gelöscht)
            string muster = Muster.Replace("'", "''");
            int alt = DienstSteuerung.PowerShell("$r = @(Get-NetFirewallRule -DisplayName '" + muster + "','" + muster + " (*)' -ErrorAction SilentlyContinue); " +
                "if ($r.Count -gt 0) { $r | Remove-NetFirewallRule; exit 1 } else { exit 0 }");
            if (alt == 1) DienstSteuerung.FirewallSetzen(Speicher.EinstellungenLesen().port);
        }

        /// <summary>Verwaltungsfenster ("Dienst aktualisieren"): Reste des alten Programmordners entfernen.</summary>
        public static void AltenOrdnerEntfernen()
        {
            try
            {
                string alt = AlterProgrammordner;
                if (alt != null && !DienstSteuerung.SelbePfade(Path.GetDirectoryName(Application.ExecutablePath), alt))
                    Directory.Delete(alt, true);
            }
            catch { }
        }
    }
}