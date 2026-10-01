// File: Localization.cs
// Namespace: TrackMaintenance
// Mehrsprachigkeit: Englisch + Deutsch hier, 19 weitere Sprachen in Translations.cs.
// Korrekturen oder zusätzliche Sprachen als Textdatei im Mod-Ordner (überschreiben die eingebauten):
//   <Mod>/lang/<code>.txt   (z. B. lang/fr.txt)
//
// Format der Datei (UTF-8), eine Zeile pro Eintrag:
//   # Kommentar
//   lang.name = Français
//   menu.label = Réparation de voie
//   ...
// "\n" im Wert wird als Zeilenumbruch gelesen. Fehlende Schlüssel fallen auf Englisch zurück.
//
// Die Sprache wird immer automatisch erkannt: Sprache des Spiels (I2 Localization, per Reflection),
// sonst Systemsprache. Eine Auswahl in den Einstellungen gibt es nicht.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace TrackMaintenance
{
    public static class Loc
    {
        private const string Fallback = "en";

        private static readonly Dictionary<string, Dictionary<string, string>> languages =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static string current = Fallback;
        private static float nextCheck;

        private static string cultureFor;
        private static CultureInfo culture = CultureInfo.InvariantCulture;

        private static bool i2Searched;
        private static PropertyInfo i2LanguageCode;

        // =========================
        // INITIALISIERUNG
        // =========================

        public static void Init(string modPath)
        {
            languages.Clear();
            AddBuiltIn();
            LoadFiles(modPath);
            Refresh();
        }

        // Aktuelle Sprache (wird alle 2 s neu ermittelt, damit ein Sprachwechsel im Spiel greift)
        public static string Current
        {
            get
            {
                if (Time.realtimeSinceStartup >= nextCheck) Refresh();
                return current;
            }
        }

        // Sprache immer automatisch: Sprache des Spiels, sonst Systemsprache
        public static void Refresh()
        {
            nextCheck = Time.realtimeSinceStartup + 2f;
            current = Resolve(GameLanguage());
        }

        // =========================
        // ÜBERSETZEN / FORMATIEREN
        // =========================

        public static string T(string key)
        {
            Dictionary<string, string> d;
            string value;
            if (languages.TryGetValue(Current, out d) && d.TryGetValue(key, out value)) return value;
            if (languages.TryGetValue(Fallback, out d) && d.TryGetValue(key, out value)) return value;
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            try { return string.Format(Culture, T(key), args); }
            catch (FormatException) { return T(key); }
        }

        // Zahlenformat passend zur Sprache (1,234.00 bzw. 1.234,00)
        public static CultureInfo Culture
        {
            get
            {
                string lang = Current;
                if (lang != cultureFor)
                {
                    cultureFor = lang;
                    try { culture = CultureInfo.GetCultureInfo(lang == "en" ? "en-US" : lang); }
                    catch { culture = CultureInfo.InvariantCulture; }
                }
                return culture;
            }
        }

        // Geldbetrag wie im Spiel: "$" + N2
        public static string Money(double value)
        {
            return "$" + value.ToString("N2", Culture);
        }

        // =========================
        // SPRACHERKENNUNG
        // =========================

        private static string Resolve(string code)
        {
            if (string.IsNullOrEmpty(code)) return Fallback;
            code = code.Replace('_', '-');
            if (languages.ContainsKey(code)) return code;

            // Varianten, die das Spiel bzw. das System liefern kann
            string lower = code.ToLowerInvariant();
            if (lower == "zh-hant" || lower == "zh-tw" || lower == "zh-hk" || lower == "zh-mo") return Pick("zh-TW");
            if (lower.StartsWith("zh")) return Pick("zh-CN");
            if (lower == "nb" || lower == "nn" || lower.StartsWith("nb-") || lower.StartsWith("nn-")) return Pick("no");

            string baseCode = code.Split('-')[0];
            if (languages.ContainsKey(baseCode)) return baseCode;

            return Fallback;
        }

        private static string Pick(string code)
        {
            return languages.ContainsKey(code) ? code : Fallback;
        }

        private static string GameLanguage()
        {
            try
            {
                if (!i2Searched)
                {
                    i2Searched = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t;
                        try { t = asm.GetType("I2.Loc.LocalizationManager", false); }
                        catch { continue; }
                        if (t == null) continue;
                        i2LanguageCode = t.GetProperty("CurrentLanguageCode", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }

                if (i2LanguageCode != null)
                {
                    var code = i2LanguageCode.GetValue(null, null) as string;
                    if (!string.IsNullOrEmpty(code)) return code;
                }
            }
            catch { }

            switch (Application.systemLanguage)
            {
                case SystemLanguage.German: return "de";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.Hungarian: return "hu";
                case SystemLanguage.Swedish: return "sv";
                case SystemLanguage.Danish: return "da";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Finnish: return "fi";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.ChineseSimplified: return "zh-CN";
                case SystemLanguage.ChineseTraditional: return "zh-TW";
                default: return Fallback;
            }
        }

        // =========================
        // SPRACHDATEIEN
        // =========================

        private static void LoadFiles(string modPath)
        {
            if (string.IsNullOrEmpty(modPath)) return;

            string dir = Path.Combine(modPath, "lang");
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.GetFiles(dir, "*.txt"))
            {
                try
                {
                    string code = Path.GetFileNameWithoutExtension(file);
                    var entries = ParseLines(File.ReadAllLines(file, Encoding.UTF8));
                    entries.Remove("lang.code");

                    Add(code, entries);
                    Main.Log($"Language file loaded: {code} ({entries.Count} entries)");
                }
                catch (Exception e)
                {
                    Main.ModEntry.Logger.Error($"Language file '{file}' could not be read: {e.Message}");
                }
            }
        }

        // "schlüssel = wert" pro Zeile; # = Kommentar; "\n" im Wert = Zeilenumbruch
        private static Dictionary<string, string> ParseLines(IEnumerable<string> lines)
        {
            var entries = new Dictionary<string, string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                entries[key] = value;
            }
            return entries;
        }

        // Eingebauter Sprachblock (Translations.cs); die Sprache steht in "lang.code"
        private static void AddBlock(string block)
        {
            var entries = ParseLines(block.Split('\n'));
            string code;
            if (!entries.TryGetValue("lang.code", out code) || string.IsNullOrEmpty(code)) return;
            entries.Remove("lang.code");
            Add(code, entries);
        }

        private static void Add(string code, Dictionary<string, string> entries)
        {
            Dictionary<string, string> d;
            if (!languages.TryGetValue(code, out d))
            {
                d = new Dictionary<string, string>();
                languages[code] = d;
            }
            foreach (var kv in entries) d[kv.Key] = kv.Value;
        }

        // =========================
        // EINGEBAUTE TEXTE
        // =========================

        private static void AddBuiltIn()
        {
            Add("en", new Dictionary<string, string>
            {
                { "lang.name", "English" },

                // Menü / Reparaturliste
                { "menu.label", "Track Repair" },
                { "repair.title", "Track Repair" },
                { "col.section", "Section" },
                { "col.damage", "Damage" },
                { "col.vmax", "Vmax" },
                { "col.cost", "Cost" },
                { "vmax.none", "---" },
                { "track.road", "Road" },
                { "repair.none", "No track damage nearby." },
                { "repair.radius", "Radius: {0} m" },
                { "repair.stopTrain", "Stop the train to access track repair." },
                { "repair.cabooseOnly", "Track repair is only available in the caboose." },
                { "repair.done", "Repaired {0} - {1}" },
                { "repair.failed", "Repair failed (see log)" },
                { "unknown", "Unknown" },

                // Messfahrt
                { "menu.measure", "Measuring Run" },
                { "measure.header", "Measuring run:" },
                { "measure.active", "ACTIVE" },
                { "measure.braking", "EMERGENCY BRAKE" },
                { "measure.vmax", "V max:" },
                { "measure.v", "V actual:" },
                { "measure.section", "Section:" },
                { "measure.unit", "km/h" },
                { "measure.noCar", "No caboose found." },

                // Bezahlbildschirm
                { "pay.section", "Section:" },
                { "pay.insertWallet", "Insert wallet to pay" },
                { "pay.deposited", "Deposited" },

                // Einstellungen
                { "set.radius", "Radius under the vehicle (m)" },
                { "set.sec.damage", "Deformation" },
                { "set.baseDamage", "Base damage (lateral deformation)" },
                { "set.verticalRatio", "Vertical deformation" },
                { "set.frequency", "Frequency (higher value = shorter deformation)" },
                { "set.roll", "Rail tilt (0 = off)" },
                { "set.sec.explosion", "Explosions" },
                { "set.explosionRadius", "Explosion radius (m)" },
                { "set.explosionMultiplier", "Explosion multiplier (x base damage)" },
                { "set.sec.multipliers", "Multipliers" },
                { "set.perTon", "Multiplier per 1000 kg of vehicle weight" },
                { "set.perKmh", "Multiplier per 1 km/h of speed" },
                { "set.locoBonus", "Multiplier bonus for locomotives" },
                { "set.maxTotal", "Maximum deformation" },
                { "set.sec.repair", "Track repair" },
                { "set.repairRadius", "Repair radius (m)" },
                { "set.sectionLength", "Repair section length (m)" },
                { "set.maxCost", "Repair cost per 100 % damage ($)" },
                { "set.sec.vmax", "Measuring run" },
                { "set.vmaxTolerance", "Emergency brake above Vmax + tolerance (km/h)" },
                { "set.deform", "Track deformations" },
                { "set.deformAtDerail", "One-time deformation on derailment" },
                { "set.deformWhenDragged", "Continuous deformation when dragged" },
                { "set.deformByExplosion", "Large-area deformation by explosion" },
                { "set.measureRun", "Measuring runs" },
                { "set.sec.debug", "Debug" },
                { "set.debugLogs", "Print debug logs" },
                { "set.mode", "Repair mode" },
                { "set.mode.penalty", "Penalty" },
                { "set.mode.reward", "Reward" },
                { "set.mode.license", "License" },
                { "set.mode.needsLicenseMod", "License mode requires the DVCustomLicenses mod." },
                { "set.mode.info.penalty", "You most likely caused the damage, so you pay for the repair. The game's insurance applies: once your deductible is reached, it covers the rest." },
                { "set.mode.info.reward", "Regardless of who caused the damage, you receive a compensation for every repair." },
                { "set.mode.info.license", "Without the track maintenance license you cannot repair track. With it you pay for the repairs. With the track maintenance contractor license you are paid for every repair." },
                { "set.maxReward", "Repair payout per 100 % damage ($)" },
                { "set.licensePrice", "Track maintenance license: price ($)" },
                { "col.payout", "Payout" },
                { "repair.earned", "Repaired {0} - earned {1}" },
                { "repair.insured", "Repaired {0} - paid {1}, insurance {2}" },
                { "pay.insurance", "Insurance covers {0}" },
                { "set.interval", "Damage interval after derailment (s)" },
                { "set.minSpeed", "Minimum speed after derailment (km/h)" },
                { "set.licenseCopay", "Track maintenance license: copay increase ($)" },
                { "set.license2Price", "Track maintenance contractor license: price ($)" },
                { "set.license2Copay", "Track maintenance contractor license: copay increase ($)" },
                { "set.licenseNote", "Copay changes for licenses you already own take effect after loading a save." },
                { "repair.needLicense", "Track repair requires the track maintenance license." },
                { "set.mode.licenseForced", "License mode is set automatically because the DVCustomLicenses mod is active." },
            });

            Add("de", new Dictionary<string, string>
            {
                { "lang.name", "Deutsch" },

                // Menü / Reparaturliste
                { "menu.label", "Gleisreparatur" },
                { "repair.title", "Gleisreparatur" },
                { "col.section", "Abschnitt" },
                { "col.damage", "Schaden" },
                { "col.vmax", "Vmax" },
                { "col.cost", "Kosten" },
                { "vmax.none", "---" },
                { "track.road", "Strecke" },
                { "repair.none", "Keine Gleisschäden in der Nähe." },
                { "repair.radius", "Radius: {0} m" },
                { "repair.stopTrain", "Zug anhalten, um die Gleisreparatur zu öffnen." },
                { "repair.cabooseOnly", "Gleisreparatur gibt es nur im Begleitwagen." },
                { "repair.done", "{0} repariert - {1}" },
                { "repair.failed", "Reparatur fehlgeschlagen (siehe Log)" },
                { "unknown", "Unbekannt" },

                // Messfahrt
                { "menu.measure", "Messfahrt" },
                { "measure.header", "Messfahrt:" },
                { "measure.active", "AKTIV" },
                { "measure.braking", "ZWANGSBREMSUNG" },
                { "measure.vmax", "V max:" },
                { "measure.v", "V ist:" },
                { "measure.section", "Abschnitt:" },
                { "measure.unit", "km/h" },
                { "measure.noCar", "Kein Begleitwagen gefunden." },

                // Bezahlbildschirm
                { "pay.section", "Abschnitt:" },
                { "pay.insertWallet", "Geldbörse einlegen zum Bezahlen" },
                { "pay.deposited", "Eingezahlt" },

                // Einstellungen
                { "set.radius", "Radius unter dem Fahrzeug (m)" },
                { "set.sec.damage", "Verformung" },
                { "set.baseDamage", "Basisschaden = (Seitliche Verformung)" },
                { "set.verticalRatio", "Vertikale Verformung" },
                { "set.frequency", "Frequenz (höherer wert = kürzere Verformung)" },
                { "set.roll", "Schienenneigung (0 = aus)" },
                { "set.sec.explosion", "Explosionen" },
                { "set.explosionRadius", "Explosionsradius (m)" },
                { "set.explosionMultiplier", "Explosions-Multiplikator (x Basisschaden)" },
                { "set.sec.multipliers", "Multiplikatoren" },
                { "set.perTon", "Multiplikator pro 1000 kg Fahrzeuggewicht" },
                { "set.perKmh", "Multiplikator pro 1 km/h Geschwindigkeit" },
                { "set.locoBonus", "Multiplikator-Bonus für Loks" },
                { "set.maxTotal", "Maximale Verformung" },
                { "set.sec.repair", "Gleisreparatur" },
                { "set.repairRadius", "Reparaturradius (m)" },
                { "set.sectionLength", "Länge eines Reparaturabschnitts (m)" },
                { "set.maxCost", "Reparaturkosten pro 100 % Schaden ($)" },
                { "set.sec.vmax", "Messfahrt" },
                { "set.vmaxTolerance", "Zwangsbremsung ab Vmax + Toleranz (km/h)" },
                { "set.deform", "Gleisverformungen" },
                { "set.deformAtDerail", "Einmalige Verformung durch Entgleisung" },
                { "set.deformWhenDragged", "Andauernde Verformung beim Mitschleifen" },
                { "set.deformByExplosion", "Großflächige Verformung durch Explosion" },
                { "set.measureRun", "Messfahrten" },
                { "set.sec.debug", "Debug" },
                { "set.debugLogs", "Debug-Logs ausgeben" },
                { "set.mode", "Reparaturmodus" },
                { "set.mode.penalty", "Bezahlen" },
                { "set.mode.reward", "Verdienen" },
                { "set.mode.license", "Lizenz" },
                { "set.mode.needsLicenseMod", "Der Lizenzmodus benötigt die Mod DVCustomLicenses." },
                { "set.mode.info.penalty", "Du hast den Schaden höchstwahrscheinlich verursacht und zahlst die Reparatur. Die Versicherung des Spiels greift: Ist deine Selbstbeteiligung erreicht, übernimmt sie den Rest." },
                { "set.mode.info.reward", "Egal wer den Schaden verursacht hat, du erhältst für jede Reparatur eine Vergütung." },
                { "set.mode.info.license", "Ohne die Gleisinstandhaltungs-Lizenz kannst du keine Gleise reparieren. Mit ihr bezahlst du die Reparaturen. Mit der Lizenz Gleisbau-Unternehmer wirst du für jede Reparatur bezahlt." },
                { "set.maxReward", "Vergütung pro 100 % Schaden ($)" },
                { "set.licensePrice", "Lizenz Gleisinstandhaltung: Preis ($)" },
                { "col.payout", "Vergütung" },
                { "repair.earned", "{0} repariert - {1} verdient" },
                { "repair.insured", "{0} repariert - bezahlt {1}, Versicherung {2}" },
                { "pay.insurance", "Versicherung übernimmt {0}" },
                { "set.interval", "Intervallschaden nach Entgleisung (s)" },
                { "set.minSpeed", "Mindestgeschwindigkeit nach Entgleisung (km/h)" },
                { "set.licenseCopay", "Lizenz Gleisinstandhaltung: Erhöhung des Eigenanteils ($)" },
                { "set.license2Price", "Lizenz Gleisbau-Unternehmer: Preis ($)" },
                { "set.license2Copay", "Lizenz Gleisbau-Unternehmer: Erhöhung des Eigenanteils ($)" },
                { "set.licenseNote", "Geänderte Eigenanteile bereits gekaufter Lizenzen gelten nach dem Laden eines Spielstands." },
                { "repair.needLicense", "Für die Gleisreparatur brauchst du die Gleisinstandhaltungs-Lizenz." },
                { "set.mode.licenseForced", "Der Lizenzmodus ist automatisch gesetzt, weil die Mod DVCustomLicenses aktiv ist." },
            });

            // Weitere Sprachen (Translations.cs)
            foreach (string block in BuiltInTranslations.Blocks)
                AddBlock(block);
        }
    }
}