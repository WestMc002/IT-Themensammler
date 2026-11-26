// IT-Themensammler – Windows-Dienst sowie Einrichten, Starten, Beenden und Entfernen des Dienstes
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;

namespace ITThemensammler
{
    /// <summary>Der Windows-Dienst: startet beim Hochfahren den Webserver, auch ohne angemeldeten Benutzer (Konto LocalSystem).</summary>
    internal sealed class Dienst : ServiceBase
    {
        public const string Argument = "--dienst";
        public const string DienstName = "ITThemensammler";

        private readonly WebServer _server = new WebServer();

        public Dienst()
        {
            ServiceName = DienstName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                Umzug.Programm(); // früherer Name: aus dem alten Programmordner umziehen (startet dann neu) bzw. Reste entfernen
                _server.Starten();
                // Firewall-Regel aus älteren Versionen (ohne Programmbindung, für alle Adressen) im Hintergrund erneuern
                int port = _server.Port;
                System.Threading.Tasks.Task.Run(() => DienstSteuerung.FirewallPruefenImDienst(port));
                UpdatePruefung.DienstStarten(); // neue Version auf GitHub? (Hinweis in der Weboberfläche)
                Ereignis.Schreiben(Ereignis.Gestartet, EventLogEntryType.Information,
                    "Dienst gestartet. Weboberfläche auf Port " + _server.Port + ", Version " + AppInfo.Version + ".");
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Dienst: Webserver konnte nicht starten", ex);
                Ereignis.Schreiben(Ereignis.Startfehler, EventLogEntryType.Error, "Der Webserver konnte nicht starten: " + ex.Message);
                throw; // Dienststatus "Fehler", Windows startet ihn laut Wiederherstellungsoptionen neu
            }
        }

        protected override void OnStop()
        {
            UpdatePruefung.DienstBeenden();
            _server.Stoppen();
            Ereignis.Schreiben(Ereignis.Beendet, EventLogEntryType.Information, "Dienst beendet.");
        }

        protected override void OnShutdown()
        {
            UpdatePruefung.DienstBeenden();
            _server.Stoppen();
            Ereignis.Schreiben(Ereignis.Beendet, EventLogEntryType.Information, "Dienst beendet (Windows fährt herunter).");
        }
    }

    /// <summary>
    /// Einträge in der Windows-Ereignisanzeige (Protokoll "Anwendung", Quelle "IT-Themensammler") für die Überwachung,
    /// z. B. durch ein RMM-System. Feste Ereignis-IDs, siehe Liesmich.txt. Fehlermeldungen gleicher ID höchstens alle 10 Minuten.
    /// </summary>
    internal static class Ereignis
    {
        public const string Quelle = AppInfo.Name;
        public const int Gestartet = 1000, Beendet = 1001, Eingerichtet = 1002, Aktualisiert = 1003, Entfernt = 1004;
        public const int Startfehler = 1100, Anfragefehler = 1101, Datenfehler = 1102, AdFehler = 1103;

        private static readonly Dictionary<int, DateTime> Zuletzt = new Dictionary<int, DateTime>();

        /// <summary>Quelle anlegen (braucht Administratorrechte; beim Einrichten des Dienstes).</summary>
        public static void QuelleAnlegen()
        {
            try
            {
                if (!EventLog.SourceExists(Quelle)) EventLog.CreateEventSource(Quelle, "Application");
            }
            catch (Exception ex)
            {
                Speicher.Fehler("Ereignisquelle anlegen", ex);
            }
        }

        public static void Schreiben(int id, EventLogEntryType typ, string text)
        {
            try
            {
                if (typ != EventLogEntryType.Information)
                {
                    lock (Zuletzt)
                    {
                        DateTime t;
                        if (Zuletzt.TryGetValue(id, out t) && t > DateTime.UtcNow.AddMinutes(-10)) return;
                        Zuletzt[id] = DateTime.UtcNow;
                    }
                }
                if (!EventLog.SourceExists(Quelle)) EventLog.CreateEventSource(Quelle, "Application");
                EventLog.WriteEntry(Quelle, text, typ, id);
            }
            catch { } // Ereignisanzeige nicht erreichbar: Fehler.log bleibt die Quelle
        }
    }

    internal enum DienstZustand
    {
        NichtEingerichtet,
        Gestoppt,
        Startet,
        Laeuft,
        Stoppt,
        Unbekannt
    }

    /// <summary>
    /// Dienst einrichten und steuern (aus dem Verwaltungsfenster, mit Administratorrechten). Beim Einrichten wird die EXE nach
    /// "C:\Program Files\IT-Themensammler" kopiert und von dort als Dienst gestartet. So bleibt der Projekt- bzw.
    /// Download-Ordner frei, und eine neue Version wird über "Dienst aktualisieren" übernommen.
    /// </summary>
    internal static class DienstSteuerung
    {
        public const string FirewallRegel = "IT-Themensammler";
        public const string FirewallRegelProxy = FirewallRegel + " (Anwendungsproxy)";

        public static string ProgrammOrdner
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.Name); }
        }

        public static string ProgrammPfad
        {
            get { return Path.Combine(ProgrammOrdner, AppInfo.Name + ".exe"); }
        }

        public static DienstZustand Zustand()
        {
            if (Demo.Active) return DienstZustand.Laeuft; // Screenshots: eingerichteter, laufender Dienst
            try
            {
                var sc = ServiceController.GetServices().FirstOrDefault(s => string.Equals(s.ServiceName, Dienst.DienstName, StringComparison.OrdinalIgnoreCase));
                if (sc == null) return DienstZustand.NichtEingerichtet;
                using (sc)
                {
                    switch (sc.Status)
                    {
                        case ServiceControllerStatus.Running: return DienstZustand.Laeuft;
                        case ServiceControllerStatus.Stopped: return DienstZustand.Gestoppt;
                        case ServiceControllerStatus.StartPending:
                        case ServiceControllerStatus.ContinuePending: return DienstZustand.Startet;
                        case ServiceControllerStatus.StopPending:
                        case ServiceControllerStatus.PausePending:
                        case ServiceControllerStatus.Paused: return DienstZustand.Stoppt;
                    }
                }
            }
            catch { }
            return DienstZustand.Unbekannt;
        }

        /// <summary>
        /// Programmdatei, die der eingerichtete Dienst startet (aus der Registrierung; null ohne Dienst). Weicht nach einem
        /// Update aus der Zeit vor der Umbenennung kurz vom ProgrammPfad ab (alter Programmordner, siehe Umzug).
        /// </summary>
        public static string DienstPfad()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Dienst.DienstName))
                {
                    string bild = key == null ? null : key.GetValue("ImagePath") as string;
                    if (string.IsNullOrEmpty(bild)) return null;
                    bild = Environment.ExpandEnvironmentVariables(bild.Trim());
                    if (bild.StartsWith("\"")) { int e = bild.IndexOf('"', 1); return e > 1 ? bild.Substring(1, e - 1) : null; }
                    int ex = bild.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    return ex > 0 ? bild.Substring(0, ex + 4) : bild;
                }
            }
            catch { return null; }
        }

        /// <summary>Version der eingerichteten Programmdatei (leer, wenn nicht vorhanden).</summary>
        public static string InstallierteVersion()
        {
            if (Demo.Active) return AppInfo.Version;
            try
            {
                string pfad = DienstPfad() ?? ProgrammPfad;
                return File.Exists(pfad) ? FileVersionInfo.GetVersionInfo(pfad).FileVersion ?? "" : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Läuft dieses Fenster aus einer anderen Datei als der eingerichtete Dienst und ist die Version neuer?
        /// Ebenso, wenn der Dienst noch im alten Programmordner (früherer Name) eingerichtet ist.
        /// </summary>
        public static bool NeuereVersion()
        {
            if (Demo.Active) return false;
            string dienst = DienstPfad();
            if (dienst != null && !SelbePfade(dienst, ProgrammPfad) && !SelbePfade(Application.ExecutablePath, dienst)) return true;
            string inst = InstallierteVersion();
            if (inst.Length == 0 || SelbePfade(Application.ExecutablePath, dienst ?? ProgrammPfad)) return false;
            Version a, b;
            return Version.TryParse(AppInfo.Version, out a) && Version.TryParse(inst, out b) && a > b;
        }

        /// <summary>Dienst auf den Programmpfad und den Anzeigenamen dieser Version umstellen.</summary>
        public static void PfadSetzen()
        {
            Sc("config " + Dienst.DienstName + " binPath= \"\\\"" + ProgrammPfad + "\\\" " + Dienst.Argument + "\" DisplayName= \"" + AppInfo.Name + "\"");
        }

        /// <summary>Programm kopieren, Dienst anlegen (automatischer Start, Neustart bei Fehlern), Firewall freigeben und starten.</summary>
        public static void Einrichten(int port)
        {
            if (Zustand() != DienstZustand.NichtEingerichtet) throw new InvalidOperationException("Der Dienst ist bereits eingerichtet.");
            ProgrammKopieren();
            Sc("create " + Dienst.DienstName + " binPath= \"\\\"" + ProgrammPfad + "\\\" " + Dienst.Argument + "\" start= delayed-auto DisplayName= \"" + AppInfo.Name + "\"");
            Sc("description " + Dienst.DienstName + " \"Weboberfläche zum Sammeln von IT-Themen für den Vor-Ort-Termin\"", true);
            Sc("failure " + Dienst.DienstName + " reset= 86400 actions= restart/60000/restart/60000/restart/300000", true);
            FirewallSetzen(port);
            Ereignis.QuelleAnlegen();
            Starten();
            Speicher.Protokoll("Dienst eingerichtet (" + ProgrammPfad + ")");
            Ereignis.Schreiben(Ereignis.Eingerichtet, EventLogEntryType.Information, "Dienst eingerichtet (" + ProgrammPfad + "), Version " + AppInfo.Version + ".");
        }

        /// <summary>Neue Programmversion übernehmen: Dienst beenden, EXE ersetzen, wieder starten.</summary>
        public static void Aktualisieren()
        {
            bool lief = Zustand() == DienstZustand.Laeuft;
            Beenden();
            ProgrammKopieren();
            // früherer Programmname: Dienst auf den neuen Programmordner umstellen, Daten und Firewall übernehmen
            PfadSetzen();
            Umzug.Daten();
            Umzug.Firewall();
            Umzug.AltenOrdnerEntfernen();
            Ereignis.QuelleAnlegen();
            if (lief) Starten();
            Speicher.Protokoll("Dienst aktualisiert auf Version " + AppInfo.Version);
            Ereignis.Schreiben(Ereignis.Aktualisiert, EventLogEntryType.Information, "Dienst aktualisiert auf Version " + AppInfo.Version + ".");
        }

        /// <summary>Dienst und Firewall-Regel entfernen. Die Daten im Datenordner bleiben erhalten.</summary>
        public static void Entfernen()
        {
            Beenden();
            Sc("delete " + Dienst.DienstName);
            FirewallEntfernen();
            // Programmordner aufräumen (nur, wenn das Verwaltungsfenster nicht selbst von dort läuft)
            try
            {
                if (!SelbePfade(Path.GetDirectoryName(Application.ExecutablePath), ProgrammOrdner) && Directory.Exists(ProgrammOrdner))
                {
                    for (int i = 0; i < 10 && File.Exists(ProgrammPfad); i++)
                    {
                        try { File.Delete(ProgrammPfad); }
                        catch { System.Threading.Thread.Sleep(500); }
                    }
                    Directory.Delete(ProgrammOrdner, true);
                }
            }
            catch { }
            Speicher.Protokoll("Dienst entfernt");
            Ereignis.Schreiben(Ereignis.Entfernt, EventLogEntryType.Information, "Dienst entfernt. Die Daten bleiben erhalten.");
        }

        public static void Starten()
        {
            using (var sc = new ServiceController(Dienst.DienstName))
            {
                if (sc.Status == ServiceControllerStatus.Running) return;
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }

        public static void Beenden()
        {
            using (var sc = new ServiceController(Dienst.DienstName))
            {
                if (sc.Status == ServiceControllerStatus.Stopped) return;
                if (sc.CanStop) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }

        public static void Neustarten()
        {
            Beenden();
            Starten();
        }

        private static void ProgrammKopieren()
        {
            string quelle = Application.ExecutablePath;
            Directory.CreateDirectory(ProgrammOrdner);
            if (!SelbePfade(quelle, ProgrammPfad))
            {
                Exception letzter = null;
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        File.Copy(quelle, ProgrammPfad, true);
                        letzter = null;
                        break;
                    }
                    catch (IOException ex) { letzter = ex; System.Threading.Thread.Sleep(500); } // Dienst gibt die Datei gerade frei
                    catch (UnauthorizedAccessException ex) { letzter = ex; System.Threading.Thread.Sleep(500); }
                }
                if (letzter != null) throw new IOException("Die Programmdatei konnte nicht nach " + ProgrammOrdner + " kopiert werden: " + letzter.Message, letzter);
            }
            // Anleitung mitnehmen (Info-Fenster › Anleitung)
            string liesmich = Path.Combine(Path.GetDirectoryName(quelle), "Liesmich.txt");
            try
            {
                if (File.Exists(liesmich) && !SelbePfade(liesmich, Path.Combine(ProgrammOrdner, "Liesmich.txt")))
                    File.Copy(liesmich, Path.Combine(ProgrammOrdner, "Liesmich.txt"), true);
            }
            catch { }
        }

        // ------------------------------------------------------------------ Firewall

        /// <summary>
        /// Remote-Adressen der Hauptregel: eigenes Teilnetz sowie private Adressbereiche (Firmennetz, Heimnetz, VPN). Aus dem
        /// Internet ist die Weboberfläche nur über den Entra-Anwendungsproxy erreichbar (eigene Regel nur für dessen Connector).
        /// </summary>
        private static readonly string[] LokaleBereiche =
        {
            "LocalSubnet", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "fc00::/7", "fe80::/10"
        };

        private const int FwTcp = 6, FwDomaene = 1, FwPrivat = 2, FwOeffentlich = 4;

        /// <summary>
        /// Eingehende Verbindungen auf den Port erlauben, vorhandene Regeln ersetzen. Nur TCP, nur das Programm des Dienstes,
        /// nur die Profile Domäne und Privat, edge=no; Hauptregel nur für lokale Adressen, Proxy-Regel nur für die Connector-IPs.
        /// </summary>
        public static void FirewallSetzen(int port)
        {
            string exe = ProgrammPfad;
            if (exe.IndexOf('"') >= 0) throw new InvalidOperationException("Der Programmpfad enthält ein Anführungszeichen.");
            FirewallEntfernen();
            Ausfuehren(Netsh, "advfirewall firewall add rule name=\"" + FirewallRegel + "\" dir=in action=allow protocol=TCP localport=" +
                port.ToString(CultureInfo.InvariantCulture) + " remoteip=" + string.Join(",", LokaleBereiche) +
                " profile=domain,private edge=no program=\"" + exe + "\" enable=yes", false);
            // Zugriff von außen: Port für den Anwendungsproxy nur für die Connector-Server
            var e = Speicher.EinstellungenLesen();
            var ips = ProxyIps(e);
            if (e.proxyAn == true && ips.Count > 0 && e.proxyPort != port)
                Ausfuehren(Netsh, "advfirewall firewall add rule name=\"" + FirewallRegelProxy + "\" dir=in action=allow protocol=TCP localport=" +
                    e.proxyPort.ToString(CultureInfo.InvariantCulture) + " remoteip=" + string.Join(",", ips.ToArray()) +
                    " profile=domain,private edge=no program=\"" + exe + "\" enable=yes", false);
        }

        private static List<string> ProxyIps(Einstellungen e)
        {
            return e.proxyIps.Where(x => { System.Net.IPAddress a; return System.Net.IPAddress.TryParse(x, out a); }).ToList();
        }

        public static void FirewallEntfernen()
        {
            Ausfuehren(Netsh, "advfirewall firewall delete rule name=\"" + FirewallRegel + "\"", true);
            Ausfuehren(Netsh, "advfirewall firewall delete rule name=\"" + FirewallRegelProxy + "\"", true);
        }

        /// <summary>
        /// Im Dienst beim Start: Fehlt eine eigene Regel, ist sie zu weit gefasst (z. B. aus einer Version vor 1.38 ohne
        /// Programmbindung und für alle Adressen) oder für einen anderen Port bzw. Programmordner, wird sie neu angelegt.
        /// Ist die Windows-Firewall aus, bleibt alles, wie es ist.
        /// </summary>
        public static void FirewallPruefenImDienst(int port)
        {
            try
            {
                bool reparieren;
                string problem = FirewallProblem(port, out reparieren);
                if (problem == null || !reparieren) return;
                FirewallSetzen(port);
                Speicher.Protokoll("Firewall-Regel neu angelegt (" + problem + ")");
            }
            catch (Exception ex) { Speicher.Fehler("Firewall-Regel prüfen", ex); }
        }

        /// <summary>
        /// Eigene Regeln prüfen (alle Regeln mit diesem Namen, von Hand angelegte Doppel eingeschlossen). null = in Ordnung
        /// bzw. nicht prüfbar oder Windows-Firewall aus. reparieren = mit FirewallSetzen behebbar.
        /// </summary>
        public static string FirewallProblem(int port, out bool reparieren)
        {
            reparieren = false;
            try
            {
                var typ = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (typ == null) return null;
                object policy = Activator.CreateInstance(typ);
                int aktuell = Zahl(Lesen(policy, "CurrentProfileTypes"));
                bool aktiv = false;
                foreach (int p in new[] { FwDomaene, FwPrivat, FwOeffentlich })
                {
                    if ((aktuell & p) == 0) continue;
                    object an = typ.InvokeMember("FirewallEnabled", System.Reflection.BindingFlags.GetProperty, null, policy, new object[] { p });
                    if (an is bool && (bool)an) aktiv = true;
                }
                if (!aktiv) return null; // Windows-Firewall aus (z. B. andere Firewall)
                var e = Speicher.EinstellungenLesen();
                var proxyIps = ProxyIps(e);
                bool proxy = e.proxyAn == true && proxyIps.Count > 0 && e.proxyPort != port;
                var haupt = new List<object>();
                var proxyRegeln = new List<object>();
                foreach (object r in (System.Collections.IEnumerable)Lesen(policy, "Rules"))
                {
                    string name = Convert.ToString(Lesen(r, "Name"), CultureInfo.InvariantCulture);
                    if (string.Equals(name, FirewallRegel, StringComparison.OrdinalIgnoreCase)) haupt.Add(r);
                    else if (string.Equals(name, FirewallRegelProxy, StringComparison.OrdinalIgnoreCase)) proxyRegeln.Add(r);
                }
                reparieren = true;
                if (haupt.Count == 0) return "Die Firewall-Regel fehlt";
                string problem = Gesamt(haupt, port, null);
                if (problem == null && proxy)
                {
                    if (proxyRegeln.Count == 0) return "Die Firewall-Regel für den Anwendungsproxy fehlt";
                    problem = Gesamt(proxyRegeln, e.proxyPort, proxyIps);
                }
                else if (problem == null && proxyRegeln.Count > 0)
                    problem = "!Die Firewall-Regel für den Anwendungsproxy ist noch vorhanden, obwohl der Zugriff von außen aus ist";
                if (problem == null) return null;
                return problem.TrimStart('!');
            }
            catch
            {
                reparieren = false;
                return null; // nicht prüfbar: keine Anzeige
            }
        }

        /// <summary>Mehrere Regeln gleichen Namens: eine zu weite („!“) zählt immer, sonst genügt eine passende.</summary>
        private static string Gesamt(List<object> regeln, int port, List<string> erlaubteIps)
        {
            string problem = null;
            bool passend = false;
            foreach (object r in regeln)
            {
                string p = RegelProblem(r, port, erlaubteIps);
                if (p == null) passend = true;
                else if (problem == null || p.StartsWith("!", StringComparison.Ordinal)) problem = p;
            }
            if (problem != null && (problem.StartsWith("!", StringComparison.Ordinal) || !passend)) return problem;
            return null;
        }

        /// <summary>Problem einer Regel; „!“ am Anfang = die Regel reicht weiter als vorgesehen.</summary>
        private static string RegelProblem(object r, int port, List<string> erlaubteIps)
        {
            bool an = Lesen(r, "Enabled") is bool && (bool)Lesen(r, "Enabled");
            if (!an || Zahl(Lesen(r, "Action")) != 1 || Zahl(Lesen(r, "Direction")) != 1) return "Die Firewall-Regel ist ausgeschaltet";
            string app = Convert.ToString(Lesen(r, "ApplicationName"), CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(app)) return "!Die Firewall-Regel gilt für alle Programme";
            if (!SelbePfade(app, ProgrammPfad)) return "Die Firewall-Regel gilt für einen anderen Programmordner";
            int protokoll = Zahl(Lesen(r, "Protocol"));
            if (protokoll != FwTcp) return protokoll == 256 ? "!Die Firewall-Regel gilt für alle Protokolle" : "Die Firewall-Regel gilt nicht für TCP";
            string portProblem = PortProblem(Convert.ToString(Lesen(r, "LocalPorts"), CultureInfo.InvariantCulture), port);
            if (portProblem != null) return portProblem;
            if ((Zahl(Lesen(r, "Profiles")) & FwOeffentlich) != 0) return "!Die Firewall-Regel gilt auch in öffentlichen Netzen";
            bool edge = false;
            try { object x = Lesen(r, "EdgeTraversal"); edge = x is bool && (bool)x; } catch { }
            if (edge) return "!Die Firewall-Regel lässt Verbindungen über das Internet zu (Edge Traversal)";
            string remote = Convert.ToString(Lesen(r, "RemoteAddresses"), CultureInfo.InvariantCulture);
            if (erlaubteIps == null ? !NurLokal(remote) : !NurDiese(remote, erlaubteIps))
                return erlaubteIps == null ? "!Die Firewall-Regel gilt auch für Adressen aus dem Internet"
                                           : "!Die Firewall-Regel für den Anwendungsproxy gilt nicht nur für die Connector-Adressen";
            return null;
        }

        /// <summary>
        /// Genau der eigene Port = in Ordnung. Portlisten, Bereiche und Schlüsselwörter, die mehr freigeben, gelten als zu
        /// weit („!“); fehlt der eigene Port, gilt die Regel für einen anderen Port.
        /// </summary>
        private static string PortProblem(string ports, int port)
        {
            string alle = (ports ?? "").Trim();
            if (alle == port.ToString(CultureInfo.InvariantCulture)) return null;
            if (alle == "*") return "!Die Firewall-Regel gilt für alle Ports";
            bool enthalten = false, weitere = false;
            foreach (string roh in alle.Split(','))
            {
                string e = roh.Trim();
                if (e.Length == 0) continue;
                int a, b, strich = e.IndexOf('-');
                if (strich > 0 && int.TryParse(e.Substring(0, strich).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out a) &&
                    int.TryParse(e.Substring(strich + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out b))
                {
                    if (a <= port && port <= b) enthalten = true;
                    if (a != port || b != port) weitere = true;
                }
                else if (int.TryParse(e, NumberStyles.None, CultureInfo.InvariantCulture, out a))
                {
                    if (a == port) enthalten = true; else weitere = true;
                }
                else weitere = true; // Schlüsselwörter (z. B. RPC) oder Unlesbares
            }
            if (!enthalten) return "Die Firewall-Regel gilt für einen anderen Port";
            if (weitere) return "!Die Firewall-Regel gilt auch für weitere Ports";
            return null;
        }

        /// <summary>Liegt jeder Eintrag ganz im eigenen Teilnetz bzw. in privaten Bereichen? Unbekanntes zählt als nein.</summary>
        private static bool NurLokal(string remote)
        {
            if (string.IsNullOrEmpty(remote)) return false;
            foreach (string roh in remote.Split(','))
            {
                string e = roh.Trim();
                if (e.Length == 0) continue;
                if (e.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] lo, hi;
                if (!Bereich(e, out lo, out hi)) return false; // "*", "Internet", Unlesbares
                bool drin = false;
                foreach (string block in LokaleBereiche)
                {
                    byte[] blo, bhi;
                    if (Bereich(block, out blo, out bhi) && blo.Length == lo.Length && Vergleich(lo, blo) >= 0 && Vergleich(hi, bhi) <= 0) { drin = true; break; }
                }
                if (!drin) return false;
            }
            return true;
        }

        /// <summary>Enthält die Regel nur Einzeladressen aus der Liste der Connector-IPs?</summary>
        private static bool NurDiese(string remote, List<string> ips)
        {
            if (string.IsNullOrEmpty(remote)) return false;
            foreach (string roh in remote.Split(','))
            {
                byte[] lo, hi;
                if (!Bereich(roh.Trim(), out lo, out hi) || Vergleich(lo, hi) != 0) return false;
                bool bekannt = false;
                foreach (string ip in ips)
                {
                    System.Net.IPAddress a;
                    if (System.Net.IPAddress.TryParse(ip, out a))
                    {
                        byte[] b = a.GetAddressBytes();
                        if (b.Length == lo.Length && Vergleich(b, lo) == 0) { bekannt = true; break; }
                    }
                }
                if (!bekannt) return false;
            }
            return true;
        }

        /// <summary>„Adresse“, „Adresse/Maske“, „Adresse/Präfix“ oder „Von-Bis“ als Bereich (Bytes).</summary>
        private static bool Bereich(string e, out byte[] lo, out byte[] hi)
        {
            lo = hi = null;
            System.Net.IPAddress a, b;
            int strich = e.IndexOf('-');
            if (strich > 0)
            {
                if (!System.Net.IPAddress.TryParse(e.Substring(0, strich), out a) || !System.Net.IPAddress.TryParse(e.Substring(strich + 1), out b)) return false;
                lo = a.GetAddressBytes(); hi = b.GetAddressBytes();
                return lo.Length == hi.Length && Vergleich(lo, hi) <= 0;
            }
            int schraeg = e.IndexOf('/');
            if (schraeg < 0)
            {
                if (!System.Net.IPAddress.TryParse(e, out a)) return false;
                lo = a.GetAddressBytes(); hi = (byte[])lo.Clone();
                return true;
            }
            if (!System.Net.IPAddress.TryParse(e.Substring(0, schraeg), out a)) return false;
            byte[] adr = a.GetAddressBytes();
            string m = e.Substring(schraeg + 1);
            int praefix;
            System.Net.IPAddress maske;
            if (!int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out praefix))
            {
                if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !System.Net.IPAddress.TryParse(m, out maske)) return false;
                praefix = 0;
                foreach (byte x in maske.GetAddressBytes()) for (int bit = 7; bit >= 0; bit--) if ((x & (1 << bit)) != 0) praefix++;
            }
            if (praefix < 0 || praefix > adr.Length * 8) return false;
            lo = new byte[adr.Length]; hi = new byte[adr.Length];
            for (int i = 0; i < adr.Length; i++)
            {
                int bits = Math.Max(0, Math.Min(8, praefix - i * 8));
                byte behalten = (byte)(bits == 0 ? 0 : 0xFF << (8 - bits));
                lo[i] = (byte)(adr[i] & behalten);
                hi[i] = (byte)(adr[i] | ~behalten);
            }
            return true;
        }

        private static int Vergleich(byte[] x, byte[] y)
        {
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            return 0;
        }

        private static object Lesen(object com, string eigenschaft)
        {
            return com.GetType().InvokeMember(eigenschaft, System.Reflection.BindingFlags.GetProperty, null, com, null);
        }

        private static int Zahl(object wert)
        {
            return wert == null ? 0 : Convert.ToInt32(wert, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Hinweis zur Firewall für die Karte „Webserver“ (leer = alles in Ordnung): Problem der eigenen Regel; „Öffentlich“
        /// nur, wenn kein Netz als Privat oder Domäne eingestuft ist (CurrentProfileTypes enthält die Profile aller
        /// Verbindungen, ein öffentlicher VPN- oder virtueller Adapter allein löst also nichts aus); andere bei Windows
        /// registrierte Firewalls (z. B. ESET) mit dem freizugebenden Port. Fehler bei der Abfrage zeigen nichts an.
        /// </summary>
        public static string FirewallHinweis(int port)
        {
            var teile = new List<string>();
            try
            {
                bool reparieren;
                string problem = Zustand() == DienstZustand.NichtEingerichtet ? null : FirewallProblem(port, out reparieren);
                if (problem != null) teile.Add(problem + " – „Speichern“ legt sie neu an.");
            }
            catch { }
            try
            {
                var typ = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (typ != null)
                {
                    object policy = Activator.CreateInstance(typ);
                    int profile = Convert.ToInt32(typ.InvokeMember("CurrentProfileTypes", System.Reflection.BindingFlags.GetProperty, null, policy, null), CultureInfo.InvariantCulture);
                    // 1 = Domäne, 2 = Privat, 4 = Öffentlich
                    if ((profile & 3) == 0 && (profile & 4) != 0)
                        teile.Add("Das Netzwerk dieses Rechners ist als „Öffentlich“ eingestuft – die Firewall-Regel gilt nur für Domäne und privates Netz. " +
                                  "Netzwerk in den Windows-Einstellungen auf „Privat“ stellen.");
                }
            }
            catch { }
            try
            {
                var typ = Type.GetTypeFromProgID("HNetCfg.FwProducts");
                if (typ != null)
                {
                    object produkte = Activator.CreateInstance(typ);
                    int anzahl = Convert.ToInt32(typ.InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, produkte, null), CultureInfo.InvariantCulture);
                    var namen = new List<string>();
                    for (int i = 0; i < anzahl && i < 10; i++)
                    {
                        object produkt = typ.InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, produkte, new object[] { i });
                        if (produkt == null) continue;
                        string name = Convert.ToString(produkt.GetType().InvokeMember("DisplayName", System.Reflection.BindingFlags.GetProperty, null, produkt, null), CultureInfo.InvariantCulture);
                        name = Speicher.Einzeilig(name ?? "").Trim();
                        if (name.Length > 60) name = name.Substring(0, 60);
                        if (name.Length > 0 && !namen.Contains(name)) namen.Add(name);
                    }
                    if (namen.Count > 0)
                        teile.Add("Zusätzlich ist " + string.Join(", ", namen.ToArray()) + " als Firewall aktiv – dort TCP-Port " +
                                  port.ToString(CultureInfo.InvariantCulture) + " eingehend freigeben.");
                }
            }
            catch { }
            return string.Join(" ", teile.ToArray());
        }

        // ------------------------------------------------------------------ Hilfen

        /// <summary>PowerShell-Befehl unsichtbar ausführen; Rückgabe: Exit-Code (-1 bei Fehler).</summary>
        public static int PowerShell(string befehl)
        {
            try
            {
                return Ausfuehren(UpdateCheck.PowerShellPfad, "-NoProfile -ExecutionPolicy Bypass -Command \"" + befehl.Replace("\"", "\\\"") + "\"", true);
            }
            catch { return -1; }
        }

        /// <summary>Systemprogramme nur über feste Pfade (nie über den Suchpfad).</summary>
        private static string Netsh { get { return Path.Combine(Environment.SystemDirectory, "netsh.exe"); } }

        private static void Sc(string argumente, bool fehlerEgal = false)
        {
            Ausfuehren(Path.Combine(Environment.SystemDirectory, "sc.exe"), argumente, fehlerEgal);
        }

        private static int Ausfuehren(string programm, string argumente, bool fehlerEgal)
        {
            var psi = new ProcessStartInfo(programm, argumente)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using (var p = Process.Start(psi))
            {
                string aus = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                if (!fehlerEgal && p.ExitCode != 0)
                    throw new InvalidOperationException(Path.GetFileName(programm) + " " + argumente + "\r\n\r\n" + aus.Trim());
                return p.ExitCode;
            }
        }

        internal static bool SelbePfade(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}