// File: TrackRepair.cs
// Namespace: TrackMaintenance
// Contains: TrackSnapshots (Originalzustand), DamageLedger (Schaden pro Meter),
//           Reparatur-Menü im CareerManager der Caboose inkl. Bezahlung an der Kasse.
//           Menü und Kasse sind nach dem Vorbild der Mod JunctionMaintenance gebaut.
//           Alle Texte laufen über Loc (Localization.cs).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using DV;
using DV.Utils;
using DV.InventorySystem;
using DV.ServicePenalty.UI;
using DV.PointSet;

namespace TrackMaintenance
{
    // =========================
    // ORIGINALZUSTAND
    // =========================

    // Merkt sich den Zustand der Punktesätze, bevor sie zum ersten Mal verformt werden.
    public static class TrackSnapshots
    {
        private static readonly Dictionary<RailTrack, EquiPointSet.Point[]> tracks =
            new Dictionary<RailTrack, EquiPointSet.Point[]>();

        private static ConditionalWeakTable<EquiPointSet, EquiPointSet.Point[]> sleepers =
            new ConditionalWeakTable<EquiPointSet, EquiPointSet.Point[]>();

        // Beim Laden eines Spielstands: alte Gleis-Objekte existieren nicht mehr
        public static void Reset()
        {
            tracks.Clear();
            sleepers = new ConditionalWeakTable<EquiPointSet, EquiPointSet.Point[]>();
        }

        public static void EnsureTrack(RailTrack track, EquiPointSet set)
        {
            if (track == null || set == null || set.points == null) return;
            if (tracks.ContainsKey(track)) return;
            tracks[track] = (EquiPointSet.Point[])set.points.Clone();
        }

        public static void EnsureSleeper(EquiPointSet set)
        {
            if (set == null || set.points == null) return;
            EquiPointSet.Point[] existing;
            if (sleepers.TryGetValue(set, out existing)) return;
            sleepers.Add(set, (EquiPointSet.Point[])set.points.Clone());
        }

        public static bool RestoreTrack(RailTrack track, EquiPointSet set, double a, double b, float blend)
        {
            EquiPointSet.Point[] snap;
            if (track == null || !tracks.TryGetValue(track, out snap)) return false;
            RestoreSet(set, snap, a, b, blend);
            return true;
        }

        public static void RestoreSleeper(EquiPointSet set, double a, double b, float blend)
        {
            EquiPointSet.Point[] snap;
            if (set == null || !sleepers.TryGetValue(set, out snap)) return;
            RestoreSet(set, snap, a, b, blend);
        }

        // Gewicht der Wiederherstellung außerhalb des Abschnitts (1 am Rand -> 0 nach 'blend' Metern)
        public static float Falloff(float distance, float blend)
        {
            return 0.5f * (1f + Mathf.Cos(Mathf.Clamp01(distance / blend) * Mathf.PI));
        }

        private static void RestoreSet(EquiPointSet set, EquiPointSet.Point[] snap, double a, double b, float blend)
        {
            if (set == null || set.points == null || snap == null || snap.Length != set.points.Length) return;

            for (int i = 0; i < snap.Length; i++)
            {
                double s = snap[i].span;
                float w;

                if (s >= a && s <= b) w = 1f;
                else if (blend > 0.0001f && s < a && a - s < blend) w = Falloff((float)(a - s), blend);
                else if (blend > 0.0001f && s > b && s - b < blend) w = Falloff((float)(s - b), blend);
                else continue;

                if (w >= 0.9999f)
                {
                    set.points[i] = snap[i];
                }
                else
                {
                    Vector3d cur = set.points[i].position;
                    Vector3d orig = snap[i].position;
                    set.points[i].position = cur + (orig - cur) * (double)w;
                    set.points[i].up = Vector3.Lerp(set.points[i].up, snap[i].up, w).normalized;
                }
            }

            set.RecalculateSpans();
        }
    }

    // =========================
    // SCHADENSVERZEICHNIS
    // =========================

    // Schaden pro Meter Gleis: jede Verformung addiert 'damage * Fenstergewicht' auf die Meter,
    // die sie betrifft. Der Schaden eines Abschnitts ist der Mittelwert seiner Meter, bezogen auf
    // "Damage that counts as 100 %".
    public static class DamageLedger
    {
        private const float BinSize = 1f;

        private static readonly Dictionary<RailTrack, float[]> bins = new Dictionary<RailTrack, float[]>();

        public static void Reset() { bins.Clear(); }

        public static void RemoveTrack(RailTrack track)
        {
            if (track != null) bins.Remove(track);
        }

        // true, wenn kein Meter des Gleises mehr als 'threshold01' (bezogen auf 100 %) Schaden hat
        public static bool IsHealed(RailTrack track, float threshold01)
        {
            float[] arr;
            if (track == null || !bins.TryGetValue(track, out arr)) return true;

            float limit = threshold01 * Main.Settings.FullDamage;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] > limit) return false;
            return true;
        }

        // true, wenn für das Gleis überhaupt Schaden verzeichnet ist (schneller Vorab-Check)
        public static bool Has(RailTrack track)
        {
            return track != null && bins.ContainsKey(track);
        }

        public static List<RailTrack> Tracks()
        {
            var list = new List<RailTrack>(bins.Count);
            foreach (var t in bins.Keys)
                if (t != null) list.Add(t);
            return list;
        }

        public static void Add(RailTrack track, double centerSpan, float radius, float fade, float damage)
        {
            if (track == null || damage <= 0f) return;
            EquiPointSet set = track.GetKinkedPointSet();
            if (set == null) return;

            int n = Mathf.Max(1, Mathf.CeilToInt((float)set.span / BinSize) + 1);
            float[] arr;
            if (!bins.TryGetValue(track, out arr) || arr.Length < n)
            {
                var bigger = new float[n];
                if (arr != null) Array.Copy(arr, bigger, arr.Length);
                arr = bigger;
                bins[track] = arr;
            }

            int from = Mathf.Max(0, Mathf.FloorToInt((float)(centerSpan - radius)));
            int to = Mathf.Min(arr.Length - 1, Mathf.CeilToInt((float)(centerSpan + radius)));
            for (int i = from; i <= to; i++)
            {
                float d = Mathf.Abs((float)((i + 0.5) * BinSize - centerSpan));
                if (d > radius) continue;
                arr[i] += damage * LiveDeform.Window(d, radius, fade);
            }
        }

        // Schaden des Abschnitts, 0..1
        public static float Section01(RailTrack track, double a, double b)
        {
            float[] arr;
            if (track == null || !bins.TryGetValue(track, out arr)) return 0f;

            int from = Mathf.Max(0, Mathf.FloorToInt((float)(a / BinSize)));
            int to = Mathf.Max(from, Mathf.CeilToInt((float)(b / BinSize)) - 1);

            float sum = 0f;
            for (int i = from; i <= to && i < arr.Length; i++)
                sum += arr[i];

            int count = Mathf.Max(1, to - from + 1);
            return Mathf.Clamp01(sum / count / Main.Settings.FullDamage);
        }

        // Nach einer Reparatur: Abschnitt auf 0, Übergangszone entsprechend der Wiederherstellung verringert
        public static void Clear(RailTrack track, double a, double b, float blend)
        {
            float[] arr;
            if (track == null || !bins.TryGetValue(track, out arr)) return;

            for (int i = 0; i < arr.Length; i++)
            {
                double c = (i + 0.5) * BinSize;
                if (c >= a && c <= b)
                {
                    arr[i] = 0f;
                }
                else if (blend > 0.0001f)
                {
                    float dist = (float)(c < a ? a - c : c - b);
                    if (dist < blend)
                        arr[i] *= 1f - TrackSnapshots.Falloff(dist, blend);
                }
            }
        }
    }

    // =========================
    // HÖCHSTGESCHWINDIGKEIT
    // =========================

    // Vmax nach Schaden, in 5-%-Stufen:
    //   < 5 %   -> keine Begrenzung
    //   5-9 %   -> 60 km/h, 10-14 % -> 50, 15-19 % -> 40, 20-24 % -> 30,
    //   25-29 % -> 20, 30-34 % -> 10, ab 35 % -> 0 km/h (Gleis gesperrt)
    internal static class SpeedLimits
    {
        public const int None = -1;

        private const int FirstStepPercent = 5;
        private const int StepPercent = 5;
        private const int FirstStepKmh = 60;
        private const int StepKmh = 10;

        public static int ForPercent(int percent)
        {
            if (percent < FirstStepPercent) return None;
            int steps = (percent - FirstStepPercent) / StepPercent;
            return Math.Max(0, FirstStepKmh - steps * StepKmh);
        }

        // Nur der Wert, immer 2 Zeichen: "60", " 0", "--"
        public static string Format(int kmh)
        {
            string number = kmh == None ? Loc.T("vmax.none") : kmh.ToString();
            return number.PadLeft(2);
        }
    }

    // =========================
    // GLEIS-ID
    // =========================

    // Anzeige-ID des Gleises aus dem GameObject-Namen:
    //   "[Y]_[SM]_[B-04-O]"  -> "SM B-04-O"   (Betriebsstelle + Gleis, Markierung [Y] entfällt)
    //   "... Road 39 ..."    -> "Strecke 39"  (übersetzt über "track.road")
    // Die Zerlegung wird pro Gleis gecacht, die Übersetzung passiert bei jeder Anzeige.
    internal static class TrackIds
    {
        private sealed class Parsed
        {
            public bool IsRoad;
            public string Text;
        }

        private static readonly Regex BracketRx = new Regex(@"\[([^\]]*)\]");
        private static readonly Regex RoadRx = new Regex(@"road[\s_]*(\d+)", RegexOptions.IgnoreCase);

        private static readonly ConditionalWeakTable<RailTrack, Parsed> cache =
            new ConditionalWeakTable<RailTrack, Parsed>();

        public static string Get(RailTrack track)
        {
            if (track == null) return "?";
            Parsed p = cache.GetValue(track, Parse);
            return p.IsRoad ? Loc.T("track.road") + " " + p.Text : p.Text;
        }

        private static Parsed Parse(RailTrack track)
        {
            // Weichen-Fahrweg ("[track through]" / "[track diverging]"): ID der Weiche anzeigen, wie das
            // Funkgerät (Junction.junctionData.junctionIdLong, z. B. "S-0014-SM"). Beide Stränge heißen gleich.
            if (JunctionTracks.Is(track))
            {
                Junction j = FindJunction(track);
                if (j != null && !string.IsNullOrEmpty(j.junctionData.junctionIdLong))
                    return new Parsed { IsRoad = false, Text = j.junctionData.junctionIdLong };
            }

            return ParseName(track.name);
        }

        // ---------- Weiche zu einem Fahrweg finden ----------

        private static Dictionary<RailTrack, Junction> junctionByTrack;

        private static Junction FindJunction(RailTrack track)
        {
            // 1. Weiche als übergeordnetes Objekt
            Junction j = track.GetComponentInParent<Junction>();
            if (j != null) return j;

            // 2. Weiche, bei der dieser Fahrweg ein Abzweig ist (einmalig für alle Weichen aufgebaut)
            if (junctionByTrack == null)
            {
                junctionByTrack = new Dictionary<RailTrack, Junction>();
                foreach (Junction jj in UnityEngine.Object.FindObjectsOfType<Junction>())
                {
                    if (jj == null) continue;
                    if (jj.inBranch != null && jj.inBranch.track != null) junctionByTrack[jj.inBranch.track] = jj;
                    if (jj.outBranches == null) continue;
                    foreach (Junction.Branch b in jj.outBranches)
                        if (b != null && b.track != null && !junctionByTrack.ContainsKey(b.track))
                            junctionByTrack[b.track] = jj;
                }
            }

            junctionByTrack.TryGetValue(track, out j);
            return j;
        }

        // Nach dem Laden eines Spielstands (neue Szene): Zuordnung neu aufbauen
        public static void ResetJunctionCache()
        {
            junctionByTrack = null;
        }

        private static Parsed ParseName(string name)
        {
            name = name ?? string.Empty;

            Match road = RoadRx.Match(name);
            if (road.Success)
                return new Parsed { IsRoad = true, Text = road.Groups[1].Value };

            var parts = new List<string>();
            foreach (Match m in BracketRx.Matches(name))
            {
                string v = m.Groups[1].Value.Trim();
                if (v.Length == 0 || v.Equals("Y", StringComparison.OrdinalIgnoreCase)) continue;
                parts.Add(v);
            }

            string rest = BracketRx.Replace(name, "").Replace('_', ' ').Trim();
            if (rest.Length > 0) parts.Add(rest);

            return new Parsed { IsRoad = false, Text = parts.Count > 0 ? string.Join(" ", parts) : name };
        }
    }

    // =========================
    // REPARATUR-EINTRÄGE
    // =========================

    internal sealed class RepairEntry
    {
        public RailTrack Track;
        public double Start;
        public double End;
        public float Damage01;
        public float Sort;

        public int Percent { get { return Mathf.RoundToInt(Damage01 * 100f); } }

        public int VmaxKmh { get { return SpeedLimits.ForPercent(Percent); } }

        // Preis je nach Modus: Kosten (ggf. mit Versicherung) bzw. Vergütung; pro 100 %: Max / 100 * Prozent.
        // Wird pro Eintrag nur einmal berechnet (die Liste wird nach jeder Reparatur neu aufgebaut);
        // vorher lief die Berechnung bei jedem Neuzeichnen für jede Zeile und machte das Scrollen träge.
        private RepairQuote? quote;
        public RepairQuote Quote
        {
            get
            {
                if (!quote.HasValue) quote = RepairEconomy.Quote(Percent);
                return quote.Value;
            }
        }

        public string TrackId { get { return TrackIds.Get(Track); } }

        public string Range { get { return Start.ToString("0") + "-" + End.ToString("0") + " m"; } }
    }

    internal static class RepairEntries
    {
        // Alle beschädigten Abschnitte im Radius um 'center' (Raster: Abschnittslänge ab Gleisanfang)
        public static List<RepairEntry> Build(Vector3 center)
        {
            var list = new List<RepairEntry>();
            float radius = Main.Settings.RepairRadius;
            float len = Main.Settings.RepairSectionLength;

            foreach (RailTrack track in DamageLedger.Tracks())
            {
                // GetClosestPoint liefert die QUADRIERTE Distanz
                var (point, sqrDist) = RailTrack.GetClosestPoint(track, center);
                if (point == null || sqrDist > radius * radius) continue;

                EquiPointSet set = track.GetKinkedPointSet();
                if (set == null) continue;

                double trackLen = set.span;
                double cs = point.Value.span;
                float half = Mathf.Sqrt(Mathf.Max(0f, radius * radius - sqrDist));

                int k0 = Mathf.Max(0, Mathf.FloorToInt((float)((cs - half) / len)));
                int k1 = Mathf.FloorToInt((float)((cs + half) / len));

                for (int k = k0; k <= k1; k++)
                {
                    double a = k * (double)len;
                    if (a >= trackLen) break;
                    double b = Math.Min(trackLen, a + len);

                    var e = new RepairEntry
                    {
                        Track = track,
                        Start = a,
                        End = b,
                        Damage01 = DamageLedger.Section01(track, a, b)
                    };
                    if (e.Percent < 1) continue;

                    // Gleis wird gerade (von jemand anderem) repariert: erst danach wieder wählbar
                    if (RepairAnimation.IsRunning(track)) continue;

                    // Multiplayer-Client: Abschnitt wartet auf die Antwort des Hosts
                    if (TM_Multiplayer.IsClient && TM_Multiplayer.IsRepairPending(track, a)) continue;

                    e.Sort = Mathf.Sqrt(sqrDist) + Mathf.Abs((float)((a + b) * 0.5 - cs));
                    list.Add(e);
                }
            }

            list.Sort((x, y) => x.Sort.CompareTo(y.Sort));
            return list;
        }
    }

    // =========================
    // MENÜ-HILFEN
    // =========================

    internal static class TQ_Helpers
    {
        private static readonly MethodInfo _miSetInfo =
            AccessTools.Method(typeof(CareerManagerInfoScreen), "SetInfoData",
                new[] { typeof(IDisplayScreen), typeof(string), typeof(string),
                        typeof(string), typeof(string), typeof(string), typeof(string), typeof(Action) });

        // Name des CareerManagers im Innenraum der Caboose
        private const string CABOOSE_CM_NAME = "CareerManagerTrainInterior";

        // Fahrzeug, zu dessen Innenraum ein Objekt gehört (null = kein Fahrzeug, z. B. Station)
        public static TrainCar CarOf(Transform host)
        {
            if (host == null) return null;
            var interior = host.GetComponentInParent<TrainCarInteriorObject>();
            if (interior != null && interior.actualTrainCar != null) return interior.actualTrainCar;
            return host.GetComponentInParent<TrainCar>();
        }

        // Ist das Fahrzeug eine Caboose? (Fahrzeugtyp, z. B. "CabooseRed")
        public static bool IsCaboose(TrainCar car)
        {
            return car != null && car.carType.ToString().IndexOf("Caboose", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // true, wenn der CareerManager im Innenraum einer Caboose sitzt.
        // CareerManager anderer Fahrzeuge (gleiches Prefab) bekommen KEINE Einträge.
        public static bool IsCabooseCareerManager(Transform host)
        {
            TrainCar car = CarOf(host);
            if (car != null) return IsCaboose(car);

            // Rückfall, falls das Fahrzeug nicht zu ermitteln ist: Objektname + nächste Caboose
            for (Transform t = host; t != null; t = t.parent)
                if (t.name.StartsWith(CABOOSE_CM_NAME, StringComparison.OrdinalIgnoreCase))
                    return IsCaboose(ResolveSelfCar(host));
            return false;
        }

        public static Transform HostOf(CareerManagerMainScreen screen)
        {
            return screen.screenSwitcher != null ? screen.screenSwitcher.transform : screen.transform;
        }

        // Beliebig viele Zeilen im Paragraph-Feld (L1..L4 bleiben leer)
        public static void SetInfoLines(CareerManagerInfoScreen info, IDisplayScreen returnScreen,
                                        string title, IList<string> lines)
        {
            try
            {
                string paragraph = (lines == null || lines.Count == 0) ? string.Empty : string.Join("\n", lines);
                _miSetInfo?.Invoke(info, new object[] { returnScreen, title, paragraph, "", "", "", "", null });
                info.Activate(returnScreen);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("SetInfoLines failed: " + e);
            }
        }

        public static void GetMenuColors(DisplayScreenSwitcher sw, out Color regular, out Color highlighted)
        {
            regular = Color.white;
            highlighted = new Color(1f, 0.8f, 0.3f);
            if (sw == null) return;
            try
            {
                var t = sw.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var fReg = t.GetField("REGULAR_COLOR", flags);
                var fHL = t.GetField("HIGHLIGHTED_COLOR", flags);
                if (fReg != null)
                {
                    var v = fReg.IsStatic ? fReg.GetValue(null) : fReg.GetValue(sw);
                    if (v is Color) regular = (Color)v;
                }
                if (fHL != null)
                {
                    var v = fHL.IsStatic ? fHL.GetValue(null) : fHL.GetValue(sw);
                    if (v is Color) highlighted = (Color)v;
                }
            }
            catch { }
        }

        // true = Fahrzeug steht (oder keins gefunden); false = in Bewegung (max. 1 km/h)
        public static bool IsStandingSelf(TrainCar selfCar)
        {
            if (selfCar == null || selfCar.rb == null) return true;
            return selfCar.rb.velocity.magnitude * 3.6f <= 1f;
        }

        // Parent-TrainCar des Host-Transforms, sonst nächster Caboose innerhalb 12 m
        public static TrainCar ResolveSelfCar(Transform hostTf)
        {
            if (hostTf == null) return null;

            var tc = CarOf(hostTf);
            if (tc != null) return tc;

            var all = CarSpawner.Instance?.AllCars;
            if (all == null) return null;

            Vector3 p = hostTf.position;
            float bestD2 = 12f * 12f;
            TrainCar best = null;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null) continue;
                string nm = c.name ?? "";
                if (nm.IndexOf("caboose", StringComparison.OrdinalIgnoreCase) < 0) continue;
                float d2 = (c.transform.position - p).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = c; }
            }
            return best;
        }

        // ---------- Tabellenlayout ----------
        // Die Schrift des Bildschirms ist proportional, Leerzeichen allein richten nichts aus.
        // Deshalb TextMeshPro-Rich-Text:
        //   <pos=NN%>  setzt den Anfang jeder Spalte fest (unabhängig von der Wortlänge davor)
        //   <mspace=X> macht die Zahlenspalten zur Festbreitenschrift; dort wird links mit
        //              Leerzeichen aufgefüllt, damit Zahlen rechtsbündig untereinander stehen.
        // Die Überschriften stehen ohne mspace am Spaltenanfang.
        //
        // Section (Gleis-ID, z. B. "IME A-07-L") | Damage "100 %" | Vmax "60" | Cost rechtsbündig am rechten Rand
        //
        // Kosten rechtsbündig: TMP kann pro Zeile nur eine Ausrichtung. Deshalb nach dem linken Teil
        // ein Zeilenumbruch mit Zeilenhöhe 0 (bleibt in derselben Höhe) und dann rechtsbündig weiter.

        // Spaltenanfänge in % der Bildschirmbreite. Abstand zwischen zwei Spalten muss größer sein als
        // die breiteste Überschrift bzw. der breiteste Inhalt dieser Spalte.
        private const int POS_DAMAGE = 32;
        private const int POS_VMAX = 52;
        private const string RIGHT_BEGIN = "<line-height=0>\n<align=right>";
        private const string RIGHT_END = "<line-height=100%><align=left>";
        private const string MONO = "<mspace=0.5em>";   // Zeichenbreite der Zahlenspalten
        private const string MONO_END = "</mspace>";
        private const string HEADER_SIZE = "<size=85%>"; // Überschriften etwas kleiner (lange Übersetzungen)
        private const string HEADER_SIZE_END = "</size>";

        private const int SECTION_MAX_CHARS = 12;  // "IME A-07-L" = 10, "STRECKE 123" = 11
        private const int DAMAGE_WIDTH = 5;  // "100 %"
        private const int VMAX_WIDTH = 2;    // "60"

        private static string Fit(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length > max ? text.Substring(0, max) : text;
        }

        private static string Pos(int percent)
        {
            return "<pos=" + percent + "%>";
        }

        private static string Mono(string text, int width)
        {
            return MONO + (text ?? string.Empty).PadLeft(width) + MONO_END;
        }

        public static string HeaderLine()
        {
            return "<align=left>" + HEADER_SIZE
                 + Fit(Loc.T("col.section"), 16)
                 + Pos(POS_DAMAGE) + Loc.T("col.damage")
                 + Pos(POS_VMAX) + Loc.T("col.vmax")
                 + RIGHT_BEGIN + Loc.T(RepairEconomy.IsRewardNow ? "col.payout" : "col.cost") + RIGHT_END
                 + HEADER_SIZE_END;
        }

        public static string FormatLine(RepairEntry e)
        {
            return "<align=left>"
                 + Fit(e.TrackId, SECTION_MAX_CHARS)
                 + Pos(POS_DAMAGE) + Mono(e.Percent + " %", DAMAGE_WIDTH)
                 + Pos(POS_VMAX) + Mono(SpeedLimits.Format(e.VmaxKmh), VMAX_WIDTH)
                 + RIGHT_BEGIN + Mono(PriceText(e.Quote), 0) + RIGHT_END;
        }

        // Kosten = Spieleranteil (nach Versicherung), Vergütung mit "+"
        public static string PriceText(RepairQuote q)
        {
            return q.IsReward ? "+" + Loc.Money(q.Payout) : Loc.Money(q.Pay);
        }
    }

    // =========================
    // LISTEN-ZUSTAND
    // =========================

    internal static class TQ_ListState
    {
        public static bool Active;
        public static bool MessageActive;   // "Zug anhalten" / "nur in der Caboose"
        public static List<RepairEntry> Items = new List<RepairEntry>();
        public static int Selected;
        public static int Top;
        public static string Status;
        public static CareerManagerInfoScreen Info;
        public static CareerManagerMainScreen MainScreen;
        public static Vector3 Center;
        public static TrainCar SelfCar;     // Caboose, die stehen muss, solange die Reparatur offen ist
        public static string WorkingSection; // Abschnitt der laufenden Reparatur (Fortschrittsanzeige)

        // Reparatursperre: die eigene Reparatur (auch während auf den Host gewartet wird) oder eine
        // Reparatur in der Nähe (Fahrzeuge näher als max(Reparaturradius, Abschnittslänge)).
        public static bool RepairLocked
        {
            get { return RepairHold.Locked || RepairAnimation.BlockingTrack(SelfCar) != null; }
        }

        private static bool wasBusy;
        private static float nextBusyRender;
        private const float BusyRenderInterval = 0.1f;
        private const int BarLength = 20;

        // Ab dieser Geschwindigkeit wird die offene Reparatur (Liste oder Kasse) zwangsweise verlassen.
        // Etwas über der Öffnungsgrenze (1 km/h), damit Physik-Zittern im Stand nicht auslöst.
        private const float KickOutKmh = 1.5f;

        internal const int VISIBLE = 9;

        private static string Title { get { return Loc.T("repair.title"); } }

        public static void Start(CareerManagerMainScreen main, CareerManagerInfoScreen info, Vector3 center)
        {
            MainScreen = main;
            Info = info;
            Center = center;
            Status = null;
            MessageActive = false;

            Items = RepairEntries.Build(center);
            Selected = 0;
            Top = 0;
            Active = true;

            Render();
            MainScreen.screenSwitcher.SetActiveDisplay(Info);
        }

        public static void ShowMessage(CareerManagerMainScreen main, CareerManagerInfoScreen info, string message)
        {
            Info = info;
            MainScreen = main;
            MessageActive = true;
            TQ_Helpers.SetInfoLines(info, main, Title, new[] { message });
            main.screenSwitcher?.SetActiveDisplay(info);
        }

        public static void Stop()
        {
            Active = false;
            MessageActive = false;
            Items.Clear();
            Info = null;
            MainScreen = null;
            SelfCar = null;
            Selected = Top = 0;
            Status = null;
        }

        // Jeden Frame aus Main.OnUpdate: setzt sich die Caboose in Bewegung, während die Liste
        // oder die Kasse offen ist, wird beides geschlossen und der Hinweis "Zug anhalten" gezeigt.
        public static void Tick()
        {
            if (!Active && !TQ_Payment.Active) return;

            if (!RepairHold.Locked && SelfCar != null && SelfCar.rb != null && SelfCar.rb.velocity.magnitude * 3.6f > KickOutKmh)
            {
                KickOut();
                return;
            }

            // Während an der Kasse bezahlt wird, startet ein anderer Spieler eine Reparatur:
            // Bezahlung abbrechen (nichts wird gebucht) und zur Fortschrittsanzeige wechseln
            if (TQ_Payment.Active)
            {
                if (RepairLocked) CancelPaymentForLock();
                return;
            }

            // Reparatursperre: Fortschritt anzeigen, danach wieder die Liste
            if (Active && Info != null)
            {
                bool busy = RepairLocked;
                if (busy)
                {
                    if (Time.time >= nextBusyRender)
                    {
                        nextBusyRender = Time.time + BusyRenderInterval;
                        Render();
                    }
                }
                else if (wasBusy)
                {
                    Rebuild();
                    Render();
                }
                wasBusy = busy;
            }
        }

        private static void CancelPaymentForLock()
        {
            try
            {
                var licensePay = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                if (licensePay != null && licensePay.cashReg != null)
                    licensePay.cashReg.ClearCurrentTransaction();

                Main.Log("Payment cancelled: another repair started");

                if (Info != null && MainScreen != null && MainScreen.screenSwitcher != null)
                {
                    // Wechsel löst LicensePay.Disable aus (räumt die Kasse auf, solange Payment aktiv ist)
                    Active = true;
                    MainScreen.screenSwitcher.SetActiveDisplay(Info);
                    TQ_Payment.Clear();
                    Render();
                }
                else
                {
                    TQ_Payment.Clear();
                }
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Cancelling payment failed: " + e);
                TQ_Payment.Clear();
            }
        }

        public static void KickOut()
        {
            var main = MainScreen;
            var info = Info;

            // Offene Bezahlung abbrechen (eingeworfenes Geld bleibt in der Kasse wie beim Abbrechen)
            if (TQ_Payment.Active)
            {
                var licensePay = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                if (licensePay != null && licensePay.cashReg != null)
                    licensePay.cashReg.ClearCurrentTransaction();
            }

            Main.Log("Repair screen closed: caboose is moving");

            if (main != null && main.screenSwitcher != null)
            {
                // Zurück zum Hauptmenü (räumt Liste und Kassenbildschirm über deren Disable auf) ...
                main.screenSwitcher.SetActiveDisplay(main);
                TQ_Payment.Clear();
                Stop();

                // ... und den Grund anzeigen
                if (info != null)
                    ShowMessage(main, info, Loc.T("repair.stopTrain"));
            }
            else
            {
                TQ_Payment.Clear();
                Stop();
            }
        }

        public static void Rebuild()
        {
            Items = RepairEntries.Build(Center);
            Selected = Mathf.Clamp(Selected, 0, Math.Max(0, Items.Count - 1));
            Top = Mathf.Clamp(Top, 0, Math.Max(0, Items.Count - VISIBLE));
        }

        // Multiplayer: neuer Gleiszustand oder neue Einstellungen vom Host -> Liste neu aufbauen.
        // Während des Bezahlbildschirms nur die Einträge aktualisieren, nicht neu zeichnen.
        public static void RefreshFromNetwork()
        {
            if (!Active || Info == null) return;
            Rebuild();
            if (!TQ_Payment.Active) Render();
        }

        public static void MoveSelection(int delta)
        {
            if (!Active || Items.Count == 0 || RepairLocked) return;

            Status = null;
            Selected = Mathf.Clamp(Selected + delta, 0, Items.Count - 1);
            if (Selected < Top) Top = Selected;
            if (Selected > Top + (VISIBLE - 1)) Top = Selected - (VISIBLE - 1);
            Top = Mathf.Clamp(Top, 0, Math.Max(0, Items.Count - VISIBLE));

            Render();
        }

        public static void Render()
        {
            if (!Active || Info == null) return;

            if (RepairLocked)
            {
                RenderBusy();
                return;
            }

            Color reg, hl;
            TQ_Helpers.GetMenuColors(MainScreen?.screenSwitcher, out reg, out hl);

            var lines = new List<string>(VISIBLE + 1);

            if (Items.Count == 0)
            {
                lines.Add(string.IsNullOrEmpty(Status) ? Loc.T("repair.none") : Status);
                lines.Add(Loc.Format("repair.radius", Main.Settings.RepairRadius.ToString("0")));
            }
            else
            {
                lines.Add(string.IsNullOrEmpty(Status) ? TQ_Helpers.HeaderLine() : Status);
                for (int i = 0; i < VISIBLE; i++)
                {
                    int idx = Top + i;
                    if (idx >= Items.Count) { lines.Add(""); continue; }

                    string line = TQ_Helpers.FormatLine(Items[idx]);
                    Color col = (idx == Selected) ? hl : reg;
                    lines.Add("<color=#" + ColorUtility.ToHtmlStringRGB(col) + ">" + line + "</color>");
                }
            }

            TQ_Helpers.SetInfoLines(Info, MainScreen, Title, lines);
        }

        // Fortschrittsanzeige der laufenden Reparatur (0-100 %)
        private static void RenderBusy()
        {
            wasBusy = true;

            Color reg, hl;
            TQ_Helpers.GetMenuColors(MainScreen?.screenSwitcher, out reg, out hl);
            string hlHex = ColorUtility.ToHtmlStringRGB(hl);
            string dimHex = ColorUtility.ToHtmlStringRGB(Color.Lerp(reg, Color.black, 0.6f));

            // Eigene Reparatur oder die eines anderen Spielers
            bool own = RepairHold.Locked;
            RailTrack other = own ? null : RepairAnimation.BlockingTrack(SelfCar);
            float progress = own ? RepairHold.Progress : RepairAnimation.ProgressOf(other);
            float remainingSec = own ? RepairHold.Remaining : RepairAnimation.RemainingOf(other);
            string section = own ? WorkingSection : TrackIds.Get(other);

            int pct = Mathf.Clamp(Mathf.FloorToInt(progress * 100f), 0, 100);
            int filled = Mathf.Clamp(pct * BarLength / 100, 0, BarLength);
            int remaining = Mathf.CeilToInt(remainingSec);

            string bar = "<mspace=0.6em><color=#" + hlHex + ">" + new string('|', filled) + "</color>"
                       + "<color=#" + dimHex + ">" + new string('|', BarLength - filled) + "</color></mspace>";

            var lines = new List<string>
            {
                "<align=center>" + Loc.T("repair.working"),
                string.IsNullOrEmpty(section) ? " " : section,
                " ",
                "<size=200%>" + bar + "</size>",
                "<size=200%><color=#" + hlHex + ">" + pct + " %</color></size>",
                " ",
                Loc.Format("repair.wait", remaining.ToString())
            };

            TQ_Helpers.SetInfoLines(Info, MainScreen, Title, lines);
        }
    }

    // =========================
    // BEZAHLUNG
    // =========================

    internal static class TQ_Payment
    {
        public static bool Active;
        public static RepairEntry Entry;
        public static RepairQuote Quote;
        public static double Cost { get { return Quote.Pay; } }   // an der Kasse zu zahlen

        public static void Start(RepairEntry entry, RepairQuote quote)
        {
            Active = true;
            Entry = entry;
            Quote = quote;
        }

        public static void Clear()
        {
            Active = false;
            Entry = null;
            Quote = default(RepairQuote);
        }

        // Reparatur ausführen, Geld verrechnen (Versicherung bzw. Vergütung) und Liste aktualisieren
        public static bool ApplyRepair(RepairEntry entry, RepairQuote q)
        {
            TQ_ListState.WorkingSection = entry.TrackId;

            // Multiplayer-Client: Reparatur beim Host anfragen. Der Host prüft, bucht das Geld auf
            // seiner Wallet (Vergütung bzw. Kosten inkl. Versicherung) und verteilt den neuen Gleiszustand.
            if (TM_Multiplayer.IsClient)
            {
                bool sent = TM_Multiplayer.RequestRepair(entry, q);
                if (!sent)
                    TQ_ListState.Status = Loc.T("repair.failed");
                else
                {
                    // Sperre + Festhalten bis zum Ende der Animation (beginnt, wenn der Host antwortet)
                    RepairAnimation.SetOwner(entry.Track, TQ_ListState.SelfCar);
                    RepairHold.Start(TQ_ListState.SelfCar, entry.Track);

                    if (q.IsReward)
                        TQ_ListState.Status = Loc.Format("repair.earned", entry.TrackId, Loc.Money(q.Payout));
                    else
                        TQ_ListState.Status = q.Covered > 0.0
                            ? Loc.Format("repair.insured", entry.TrackId, Loc.Money(q.Pay), Loc.Money(q.Covered))
                            : Loc.Format("repair.done", entry.TrackId, Loc.Money(q.Pay));
                }

                TQ_ListState.Rebuild();   // angefragter Abschnitt verschwindet bis zur Antwort
                return sent;
            }

            // Auslöser merken (Sicherheitsabstand für weitere Reparaturen) und den Clients melden
            RepairAnimation.SetOwner(entry.Track, TQ_ListState.SelfCar);
            bool ok = LiveDeform.RepairSection(entry.Track, entry.Start, entry.End);
            if (ok && RepairAnimation.IsRunning(entry.Track)) TM_Multiplayer.BroadcastRepairOwner(entry.Track, TQ_ListState.SelfCar);

            // Läuft die Reparatur schrittweise: Liste sperren und Zug festhalten, bis sie fertig ist
            if (ok && RepairAnimation.IsRunning(entry.Track))
                RepairHold.Start(TQ_ListState.SelfCar, entry.Track);

            if (!ok)
                TQ_ListState.Status = Loc.T("repair.failed");
            else if (q.IsReward)
            {
                RepairEconomy.PrintMoney(q.Payout);
                TQ_ListState.Status = Loc.Format("repair.earned", entry.TrackId, Loc.Money(q.Payout));
            }
            else
            {
                RepairEconomy.AfterPaidRepair(q);
                TQ_ListState.Status = q.Covered > 0.0
                    ? Loc.Format("repair.insured", entry.TrackId, Loc.Money(q.Pay), Loc.Money(q.Covered))
                    : Loc.Format("repair.done", entry.TrackId, Loc.Money(q.Pay));
            }

            TQ_ListState.Rebuild();
            return ok;
        }
    }

    // =========================
    // MENÜEINTRAG (Hauptbildschirm, nur Caboose)
    // =========================

    internal sealed class TQ_IndexBox
    {
        public int RepairIndex = -1;           // -1 = Eintrag ausgeblendet
        public int MeasureIndex = -1;

        public Array BaseArray;                // Einträge ohne unsere (Spiel + andere Mods)
        public Type ElementType;
        public Component RepairComp;
        public Component MeasureComp;
        public Vector3 StatsPos;
        public Vector3 LineStep;

        public bool Built;
        public bool ShowRepair;
        public bool ShowMeasure;
    }

    internal static class TQ_Menu
    {
        public static readonly ConditionalWeakTable<CareerManagerMainScreen, TQ_IndexBox> Boxes =
            new ConditionalWeakTable<CareerManagerMainScreen, TQ_IndexBox>();

        public static string RepairLabel { get { return Loc.T("menu.label"); } }
        public static string MeasureLabel { get { return Loc.T("menu.measure"); } }

        // Sichtbare Einträge passend zu den Einstellungen zusammenstellen:
        //   "Gleisreparatur" nur bei aktiver Verformung, "Messfahrt" nur bei aktiven Messfahrten.
        // Wird nur neu aufgebaut, wenn sich die Sichtbarkeit geändert hat.
        public static void ApplyVisibility(CareerManagerMainScreen screen, TQ_IndexBox box)
        {
            bool showRepair = Main.Settings.TrackRepair;
            bool showMeasure = Main.Settings.MeasureRun;
            if (box.Built && box.ShowRepair == showRepair && box.ShowMeasure == showMeasure) return;

            int baseLen = box.BaseArray.Length;
            int count = baseLen + (showRepair ? 1 : 0) + (showMeasure ? 1 : 0);
            var arr = Array.CreateInstance(box.ElementType, count);
            for (int i = 0; i < baseLen; i++) arr.SetValue(box.BaseArray.GetValue(i), i);

            int next = baseLen;
            box.RepairIndex = Place(box, box.RepairComp, showRepair, arr, ref next);
            box.MeasureIndex = Place(box, box.MeasureComp, showMeasure, arr, ref next);

            AccessTools.Field(typeof(CareerManagerMainScreen), "selectableText").SetValue(screen, arr);
            AccessTools.Field(typeof(ScrollableDisplayScreen), "activeSlotCount").SetValue(screen, count);
            AccessTools.Field(typeof(ScrollableDisplayScreen), "selector")
                .SetValue(screen, new IntIterator(0, count, isWrappable: true));

            box.ShowRepair = showRepair;
            box.ShowMeasure = showMeasure;
            box.Built = true;
        }

        // Eintrag an die nächste freie Zeile setzen bzw. ausblenden; liefert den Index oder -1
        private static int Place(TQ_IndexBox box, Component comp, bool show, Array arr, ref int next)
        {
            if (comp == null) return -1;

            if (!show)
            {
                SetText(comp, string.Empty);
                comp.gameObject.SetActive(false);
                return -1;
            }

            comp.gameObject.SetActive(true);
            comp.transform.localPosition = box.StatsPos + box.LineStep * (next - 3);
            arr.SetValue(comp, next);
            return next++;
        }

        // Beschriftungen setzen (clear = leeren beim Ausschalten des Bildschirms)
        public static void SetLabels(CareerManagerMainScreen screen, bool clear)
        {
            TQ_IndexBox box;
            if (!Boxes.TryGetValue(screen, out box)) return;
            if (box.RepairIndex >= 0) SetText(box.RepairComp, clear ? string.Empty : RepairLabel);
            if (box.MeasureIndex >= 0) SetText(box.MeasureComp, clear ? string.Empty : MeasureLabel);
        }

        public static void SetText(Component c, string text)
        {
            if (c == null) return;
            try
            {
                var prop = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null) prop.SetValue(c, text);
            }
            catch { }
        }
    }

    // Läuft NACH JunctionMaintenance, damit dessen Eintrag (Platz 5) zuerst angelegt wird
    // und unsere Einträge darunter erscheinen.
    // Die Einträge werden NUR im CareerManager der Caboose angelegt; Stations-CareerManager bleiben unverändert.
    [HarmonyPatch(typeof(CareerManagerMainScreen), "Awake")]
    [HarmonyAfter("JunctionMaintenance")]
    internal static class TQ_Awake_AddMenuItem
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                TQ_IndexBox existing;
                if (TQ_Menu.Boxes.TryGetValue(__instance, out existing)) return;

                // CareerManagerAPI baut das Menü selbst neu auf: dann tragen wir uns dort ein
                if (CmApiCompat.Present)
                {
                    CmApiCompat.EnsureHooked();
                    return;
                }

                if (!TQ_Helpers.IsCabooseCareerManager(TQ_Helpers.HostOf(__instance))) return;

                var fSelectable = AccessTools.Field(typeof(CareerManagerMainScreen), "selectableText");
                var arrObj = fSelectable.GetValue(__instance) as Array;
                if (arrObj == null || arrObj.Length < 4) return; // unerwartetes Layout

                var statsComp = arrObj.GetValue(3) as Component;
                var ownedComp = arrObj.GetValue(2) as Component;
                if (statsComp == null || ownedComp == null) return;

                var parent = statsComp.transform.parent;
                var elementType = arrObj.GetType().GetElementType();

                var box = new TQ_IndexBox
                {
                    BaseArray = (Array)arrObj.Clone(),
                    ElementType = elementType,
                    StatsPos = statsComp.transform.localPosition,
                    LineStep = statsComp.transform.localPosition - ownedComp.transform.localPosition
                };

                box.RepairComp = Clone(statsComp, parent, elementType, "TrackRepairEntry");
                box.MeasureComp = Clone(statsComp, parent, elementType, "MeasureRunEntry");

                TQ_Menu.ApplyVisibility(__instance, box);
                TQ_Menu.Boxes.Add(__instance, box);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("CM Awake inject failed: " + e);
            }
        }

        private static Component Clone(Component template, Transform parent, Type elementType, string name)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
            go.name = name;
            var comp = go.GetComponent(elementType) as Component;
            TQ_Menu.SetText(comp, string.Empty);
            return comp;
        }
    }

    // Vor dem Anzeigen: Einträge passend zu den aktuellen Einstellungen ein-/ausblenden
    // (läuft vor dem Original, das die Auswahl zurücksetzt und hervorhebt)
    [HarmonyPatch(typeof(CareerManagerMainScreen), "Activate")]
    internal static class TQ_Activate_Label
    {
        static void Prefix(CareerManagerMainScreen __instance)
        {
            try
            {
                TQ_IndexBox box;
                if (TQ_Menu.Boxes.TryGetValue(__instance, out box))
                    TQ_Menu.ApplyVisibility(__instance, box);
            }
            catch (Exception e) { Main.ModEntry.Logger.Error("CM Activate visibility failed: " + e); }
        }

        static void Postfix(CareerManagerMainScreen __instance)
        {
            try { TQ_Menu.SetLabels(__instance, clear: false); }
            catch (Exception e) { Main.ModEntry.Logger.Error("CM Activate label failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), "Disable")]
    internal static class TQ_Disable_ClearLabel
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try { TQ_Menu.SetLabels(__instance, clear: true); }
            catch (Exception e) { Main.ModEntry.Logger.Error("CM Disable clear failed: " + e); }
        }
    }

    // =========================
    // KOMPATIBILITÄT: CareerManagerAPI (optional, ohne Abhängigkeit)
    // =========================
    // CareerManagerAPI (wwwDayDream) baut beim Start jedes CareerManagers die Menüeinträge komplett neu
    // auf (eigene Textobjekte, fremde werden gelöscht) und übernimmt die Eingaben des Hauptmenüs.
    // Unser eigener Menüeintrag wäre dann verschwunden. Ist die Mod aktiv, tragen wir uns deshalb
    // über ihr Ereignis "CareerManagerAwake" und "TryAddToMainScreen" ein – alles per Reflection,
    // es wird also keine Referenz auf CareerManagerAPI.dll benötigt. Ohne die Mod läuft alles wie bisher.
    internal static class CmApiCompat
    {
        private const string ModId = "CareerManagerAPI";

        private static bool checkedPresent;
        private static bool present;
        private static bool hooked;
        private static Type apiType;

        // Bildschirm, der im nächsten Frame geöffnet werden soll (nicht innerhalb eines Bildschirmwechsels)
        private static CareerManagerMainScreen pendingMain;
        private static bool pendingRepair;
        private static bool pendingMeasure;

        public static bool Present
        {
            get
            {
                if (!checkedPresent)
                {
                    checkedPresent = true;
                    present = Detect();
                }
                return present;
            }
        }

        private static bool Detect()
        {
            try
            {
                var mod = UnityModManagerNet.UnityModManager.FindMod(ModId);
                if (mod == null || !mod.Enabled) return false;

                apiType = AccessTools.TypeByName("CareerManagerAPI.CareerManagerAPI");
                if (apiType == null || apiType.GetEvent("CareerManagerAwake") == null)
                {
                    Main.ModEntry.Logger.Warning("CareerManagerAPI found, but its API is unknown – using own menu entries");
                    return false;
                }

                Main.ModEntry.Logger.Log("CareerManagerAPI detected – menu entries are added through it");
                return true;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("CareerManagerAPI detection failed: " + e);
                return false;
            }
        }

        // Einmalig beim ersten CareerManager (vor dem Start() des API-Trackers)
        public static void EnsureHooked()
        {
            if (hooked || !Present) return;
            hooked = true;

            try
            {
                EventInfo ev = apiType.GetEvent("CareerManagerAwake");
                MethodInfo handler = typeof(CmApiCompat).GetMethod(nameof(OnCareerManagerAwake),
                    BindingFlags.NonPublic | BindingFlags.Static);

                // Delegate-Parameter (CareerManagerScreenTracker) ist spezieller als unser 'object': erlaubt
                Delegate d = Delegate.CreateDelegate(ev.EventHandlerType, handler);
                ev.AddEventHandler(null, d);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("CareerManagerAPI hook failed: " + e);
            }
        }

        // Signatur passend zu CareerManagerAPI.CareerManagerInitialized
        private static void OnCareerManagerAwake(object tracker, string locationName, StationController station, TrainCar trainCar)
        {
            try
            {
                if (trainCar == null) return;   // nur CareerManager in Fahrzeugen

                var comp = tracker as Component;
                var main = comp != null ? comp.GetComponent<CareerManagerMainScreen>() : null;
                if (main == null) return;
                if (!TQ_Helpers.IsCabooseCareerManager(TQ_Helpers.HostOf(main))) return;

                MethodInfo tryAdd = tracker.GetType().GetMethod("TryAddToMainScreen");
                if (tryAdd == null) return;

                if (Main.Settings.TrackRepair)
                    Add(tracker, tryAdd, main, typeof(TQ_ApiRepairScreen), TQ_Menu.RepairLabel);
                if (Main.Settings.MeasureRun)
                    Add(tracker, tryAdd, main, typeof(TQ_ApiMeasureScreen), TQ_Menu.MeasureLabel);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("CareerManagerAPI menu entry failed: " + e);
            }
        }

        private static void Add(object tracker, MethodInfo tryAdd, CareerManagerMainScreen main, Type screenType, string label)
        {
            // TryAddToMainScreen<T>(string text, OnCanConfirm canConfirm = null, string after = null, string before = null)
            var generic = tryAdd.MakeGenericMethod(screenType);
            var args = new object[generic.GetParameters().Length];
            args[0] = label;
            bool ok = (bool)generic.Invoke(tracker, args);
            if (!ok) return;

            // Die API legt dafür ein GameObject mit dem Namen des Eintrags an: Hauptbildschirm merken
            var parent = main.feesScreen != null ? main.feesScreen.transform.parent : null;
            var go = parent != null ? parent.Find(label) : null;
            var screen = go != null ? go.GetComponent(screenType) as TQ_ApiScreenBase : null;
            if (screen != null) screen.MainScreen = main;
        }

        public static void Request(CareerManagerMainScreen main, bool repair, bool measure)
        {
            pendingMain = main;
            pendingRepair = repair;
            pendingMeasure = measure;
        }

        // Aus Main.OnUpdate
        public static void Tick()
        {
            if (pendingMain == null) return;

            var main = pendingMain;
            bool repair = pendingRepair, measure = pendingMeasure;
            pendingMain = null;

            // In den Einstellungen abgeschaltet: zurück zum Hauptmenü
            if ((repair && !Main.Settings.TrackRepair) || (measure && !Main.Settings.MeasureRun))
            {
                main.screenSwitcher?.SetActiveDisplay(main);
                return;
            }

            TQ_Open.Open(main, repair, measure);
        }
    }

    // Platzhalter-Bildschirme für CareerManagerAPI: leiten im nächsten Frame auf unsere Liste
    // bzw. die Messfahrt (beide nutzen den Info-Bildschirm des CareerManagers) weiter.
    internal abstract class TQ_ApiScreenBase : DisplayScreen
    {
        public CareerManagerMainScreen MainScreen;

        protected abstract bool IsRepair { get; }

        public override void Activate(IDisplayScreen previousScreen)
        {
            var main = MainScreen ?? previousScreen as CareerManagerMainScreen;
            if (main == null) return;
            CmApiCompat.Request(main, IsRepair, !IsRepair);
        }

        public override void Disable() { }

        public override void HandleInputAction(InputAction input)
        {
            var main = MainScreen;
            if (input == InputAction.Cancel && main != null && main.screenSwitcher != null)
                main.screenSwitcher.SetActiveDisplay(main);
        }
    }

    internal sealed class TQ_ApiRepairScreen : TQ_ApiScreenBase
    {
        protected override bool IsRepair { get { return true; } }
    }

    internal sealed class TQ_ApiMeasureScreen : TQ_ApiScreenBase
    {
        protected override bool IsRepair { get { return false; } }
    }

    // =========================
    // FESTHALTEN WÄHREND DER REPARATUR / REPARATURSPERRE
    // =========================
    // Die eigene Reparatur sperrt die Reparaturliste (Fortschrittsanzeige) und hält den Zugverband der
    // Caboose fest: Bremskraft wie voll angezogene Handbremsen (ohne Luft, ohne Zischen) und Fahrschalter aller Loks auf 0.
    //   - Bremskraft wie voll angezogene Handbremsen (TQ_BrakeSystem_RepairHold), ohne Luft
    //   - Multiplayer: Host simuliert die Bremsen; ein Client fordert das Festhalten per Heartbeat an
    //   - der CareerManager zeigt bis zum Abschluss den Ladebalken (kein Zurück ins Hauptmenü)
    // Hängt nur an der laufenden Reparatur, nicht am Bildschirm: Verlassen der Caboose oder Zurücksetzen
    // des CareerManagers ändert nichts. Reparaturen anderer Spieler sperren die eigene Liste nicht.
    // Danach löst die Bremse von selbst; die Hauptluftleitung bleibt unberührt.
    internal static class RepairHold
    {
        // Client: so lange auf den Beginn der Animation warten (Antwort des Hosts), sonst freigeben
        private const float NetworkGrace = 10f;

        private static TrainCar car;
        private static RailTrack track;
        private static bool started;        // Animation dieses Gleises hat begonnen
        private static float waitUntil;

        // CareerManager, an dem die Reparatur gestartet wurde: zeigt bis zum Ende den Ladebalken
        private static CareerManagerMainScreen lockMain;
        private static CareerManagerInfoScreen lockInfo;

        public static TrainCar Car { get { return car; } }
        public static RailTrack Track { get { return track; } }

        // Sperre aktiv (eigene Reparatur läuft oder wartet auf den Host)
        public static bool Locked { get { return track != null; } }

        // Zug wird gehalten
        public static bool Holding { get { return track != null && car != null; } }

        public static float Progress { get { return started ? RepairAnimation.ProgressOf(track) : 0f; } }

        public static float Remaining
        {
            get { return started ? RepairAnimation.RemainingOf(track) : Main.Settings.RepairMaxSeconds; }
        }

        // Einzelspieler/Host: nach RepairSection; Client: nach dem Senden der Anfrage
        public static void Start(TrainCar caboose, RailTrack repairedTrack)
        {
            if (repairedTrack == null) return;
            car = caboose;
            track = repairedTrack;
            started = RepairAnimation.IsRunning(repairedTrack);
            waitUntil = Time.time + NetworkGrace;
            lockMain = TQ_ListState.MainScreen;
            lockInfo = TQ_ListState.Info;
            Main.Log("Repair running: list locked" + (caboose != null ? ", train is held" : ""));
        }

        public static void Release()
        {
            if (track != null) Main.Log("Repair finished: list unlocked" + (car != null ? ", train released" : ""));
            car = null;
            track = null;
            started = false;
            lockMain = null;
            lockInfo = null;
        }

        // Bremsverband des gehaltenen Zuges? (Einzelspieler: Bremspatch; Multiplayer: PenaltyBrake)
        public static bool Holds(DV.Simulation.Brake.Brakeset s)
        {
            return s != null && car != null && track != null && car.brakeSystem != null
                   && ReferenceEquals(car.brakeSystem.brakeset, s);
        }

        // Client: Host hat abgelehnt
        public static void ReleaseFor(RailTrack t)
        {
            if (t != null && ReferenceEquals(t, track)) Release();
        }

        // Client: neuer Zustand des Gleises vom Host übernommen. Ohne Animation (kleiner Schaden) sofort frei.
        public static void OnRemoteApplied(RailTrack t)
        {
            if (t == null || !ReferenceEquals(t, track)) return;
            if (RepairAnimation.IsRunning(t)) started = true;
            else Release();
        }

        // Aus Main.OnUpdate
        public static void Tick()
        {
            // Fahrzeuge mit laufender Reparatur für andere Mods als "beschäftigt" melden
            MaintenanceBusy.Publish(BusyCars());

            if (track == null) return;

            if (RepairAnimation.IsRunning(track))
                started = true;
            else if (started || Time.time > waitUntil)
            {
                Release();
                return;
            }

            if (car != null) ThrottleCut.ZeroTrain(car);
            KeepProgressScreen();
        }

        // Eigene Reparatur: der CareerManager zeigt bis zum Abschluss den Ladebalken (kein Zurück ins
        // Hauptmenü, auch nicht nach Zurücksetzen des Bildschirms oder einem Wechsel durch andere Mods)
        private static void KeepProgressScreen()
        {
            if (lockMain == null || lockInfo == null || lockMain.screenSwitcher == null) return;

            var sw = lockMain.screenSwitcher;
            bool onInfo = ReferenceEquals(sw.CurrentScreen, lockInfo);
            if (onInfo && TQ_ListState.Active && ReferenceEquals(TQ_ListState.Info, lockInfo)) return;

            if (MeasureRun.Active) MeasureRun.Stop();

            if (!onInfo) sw.SetActiveDisplay(lockInfo);   // Disable des alten Bildschirms räumt dessen Zustand auf

            TQ_ListState.MainScreen = lockMain;
            TQ_ListState.Info = lockInfo;
            TQ_ListState.SelfCar = car;
            TQ_ListState.MessageActive = false;
            TQ_ListState.Active = true;
            TQ_ListState.Render();
        }

        // Alle Fahrzeuge, an denen gerade eine Gleisreparatur läuft (eigene + laufende Animationen)
        private static IEnumerable<TrainCar> BusyCars()
        {
            if (car != null && track != null) yield return car;
            foreach (TrainCar owner in RepairAnimation.Owners())
                if (owner != null) yield return owner;
        }
    }

    // =========================
    // FAHRZEUG BESCHÄFTIGT (MODÜBERGREIFEND)
    // =========================
    // Gemeinsames Kennzeichen für mehrere Wartungs-Mods (TrackMaintenance, JunctionMaintenance, ...),
    // OHNE gegenseitige Abhängigkeit: ein Dictionary<string, string> (Wagen-ID -> Mod-ID) in den
    // AppDomain-Daten unter "CJ187.Maintenance.BusyVehicles". Jede Mod trägt nur ihre eigenen Einträge
    // ein und aus und prüft vor einer Reparatur, ob das Fahrzeug für eine andere Mod arbeitet.
    // Im Multiplayer trägt jede Mod auf jedem Rechner die Fahrzeuge aller laufenden Reparaturen ein
    // (bei TM: Auslöser der Animationen, vom Host gemeldet).
    // Dieselbe Klasse (mit eigener ModId) gehört in JunctionMaintenance.
    internal static class MaintenanceBusy
    {
        public const string DataKey = "CJ187.Maintenance.BusyVehicles";
        public const string ModId = "TrackMaintenance";

        private static readonly List<string> own = new List<string>();
        private static readonly HashSet<string> next = new HashSet<string>();

        private static Dictionary<string, string> Map
        {
            get
            {
                var map = AppDomain.CurrentDomain.GetData(DataKey) as Dictionary<string, string>;
                if (map == null)
                {
                    map = new Dictionary<string, string>();
                    AppDomain.CurrentDomain.SetData(DataKey, map);
                }
                return map;
            }
        }

        // Mod-ID, für die das Fahrzeug gerade arbeitet (null = frei)
        public static string BusyBy(TrainCar car)
        {
            if (car == null || string.IsNullOrEmpty(car.ID)) return null;
            string mod;
            return Map.TryGetValue(car.ID, out mod) ? mod : null;
        }

        public static bool BusyByOther(TrainCar car)
        {
            string mod = BusyBy(car);
            return mod != null && mod != ModId;
        }

        // Eigene Einträge auf genau diese Fahrzeuge setzen (jeden Frame)
        public static void Publish(IEnumerable<TrainCar> cars)
        {
            next.Clear();
            if (cars != null)
                foreach (TrainCar c in cars)
                    if (c != null && !string.IsNullOrEmpty(c.ID)) next.Add(c.ID);

            if (next.Count == 0 && own.Count == 0) return;

            var map = Map;
            for (int i = own.Count - 1; i >= 0; i--)
            {
                if (next.Contains(own[i])) continue;
                string mod;
                if (map.TryGetValue(own[i], out mod) && mod == ModId) map.Remove(own[i]);
                own.RemoveAt(i);
            }
            foreach (string id in next)
            {
                if (own.Contains(id)) continue;
                if (map.ContainsKey(id)) continue;   // gehört schon einer anderen Mod
                map[id] = ModId;
                own.Add(id);
            }
        }
    }

    // =========================
    // CAREERMANAGER IN FAHRZEUGEN BLEIBT AKTIV
    // =========================
    // Das Spiel prüft alle 5 s, ob der Spieler noch am Bildschirm ist (DisplayScreenSwitcher.
    // CheckIfPlayerNearScreen: sqrMagnitude > 5, also nur ca. 2,2 m) und springt sonst auf den
    // Startbildschirm zurück. Für CareerManager im Innenraum eines Fahrzeugs (Caboose und jedes
    // andere Fahrzeug mit CareerManager) gilt stattdessen: aktiv, solange der Spieler im/auf diesem
    // Fahrzeug ist. Außerhalb des Fahrzeugs gilt wieder die Original-Prüfung.
    // CareerManager an Stationen bleiben unverändert.
    [HarmonyPatch(typeof(DisplayScreenSwitcher), "CheckIfPlayerNearScreen")]
    internal static class TQ_Switcher_StayActive
    {
        private const float CheckPeriod = 5f;          // wie im Spiel
        private const float NearSqrDistance = 5f;      // wie im Spiel (quadrierter Abstand)

        private static readonly FieldInfo fIdle = AccessTools.Field(typeof(DisplayScreenSwitcher), "idleScreen");
        private static readonly FieldInfo fCoro = AccessTools.Field(typeof(DisplayScreenSwitcher), "checkIfPlayerNearScreenCoro");

        static bool Prefix(DisplayScreenSwitcher __instance, ref IEnumerator __result)
        {
            try
            {
                if (fIdle == null || fCoro == null) return true;

                TrainCar car = TQ_Helpers.CarOf(__instance.transform);
                if (car == null) return true;   // Station: Original

                __result = VehicleCheck(__instance, car);
                return false;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("CM stay-active patch failed: " + e);
                return true;
            }
        }

        private static IEnumerator VehicleCheck(DisplayScreenSwitcher sw, TrainCar car)
        {
            while (PlayerManager.PlayerTransform == null)
                yield return new WaitForSeconds(CheckPeriod);

            do
            {
                yield return new WaitForSeconds(CheckPeriod);
                if (sw == null) yield break;
            }
            while (PlayerStillThere(sw, car));

            var idle = fIdle.GetValue(sw) as IDisplayScreen;
            if (idle != null) sw.SetActiveDisplay(idle);
            fCoro.SetValue(sw, null);
        }

        private static bool PlayerStillThere(DisplayScreenSwitcher sw, TrainCar car)
        {
            Transform player = PlayerManager.PlayerTransform;
            if (player == null) return true;

            // Im Fahrzeug: immer aktiv
            if (car != null && PlayerManager.Car == car) return true;

            // Sonst wie im Original
            return (sw.transform.position - player.position).sqrMagnitude <= NearSqrDistance;
        }
    }

    // Öffnen von Gleisreparatur bzw. Messfahrt (aus dem eigenen Menüeintrag oder über CareerManagerAPI)
    internal static class TQ_Open
    {
        public static void Open(CareerManagerMainScreen main, bool repair, bool measure)
        {
            if (main == null) return;

            var host = TQ_Helpers.HostOf(main);
            var info = main.infoScreen;
            if (info == null) return;

            // Zusätzliche Absicherung: nur im CareerManager der Caboose
            if (!TQ_Helpers.IsCabooseCareerManager(host))
            {
                TQ_ListState.ShowMessage(main, info, Loc.T("repair.cabooseOnly"));
                return;
            }

            var selfCar = TQ_Helpers.ResolveSelfCar(host);

            // Messfahrt: auch während der Fahrt
            if (measure)
            {
                if (selfCar == null)
                {
                    TQ_ListState.ShowMessage(main, info, Loc.T("measure.noCar"));
                    return;
                }
                MeasureRun.Start(main, info, selfCar);
                return;
            }

            // Lizenzmodus: ohne Lizenz keine Reparatur
            if (!RepairEconomy.CanRepair)
            {
                TQ_ListState.ShowMessage(main, info, Loc.T("repair.needLicense"));
                return;
            }

            // Fahrzeug arbeitet gerade für eine andere Mod (z. B. Weichenreparatur)
            if (MaintenanceBusy.BusyByOther(selfCar))
            {
                TQ_ListState.ShowMessage(main, info, Loc.T("repair.vehicleBusy"));
                return;
            }

            // Reparatur: nur bei stehendem Wagen
            if (!TQ_Helpers.IsStandingSelf(selfCar))
            {
                TQ_ListState.ShowMessage(main, info, Loc.T("repair.stopTrain"));
                return;
            }

            TQ_ListState.Start(main, info, host.position);
            TQ_ListState.SelfCar = selfCar;
        }
    }

    // Bestätigen auf unserem Eintrag -> Liste öffnen
    [HarmonyPatch(typeof(CareerManagerMainScreen), "HandleInputAction")]
    internal static class TQ_HandleInput_Open
    {
        static bool Prefix(CareerManagerMainScreen __instance, InputAction input)
        {
            try
            {
                if (input != InputAction.Confirm) return true;

                TQ_IndexBox box;
                if (!TQ_Menu.Boxes.TryGetValue(__instance, out box)) return true;

                var sel = (IntIterator)AccessTools.Field(typeof(ScrollableDisplayScreen), "selector").GetValue(__instance);
                if (sel == null) return true;

                bool repair = sel.Current == box.RepairIndex;
                bool measure = sel.Current == box.MeasureIndex;
                if (!repair && !measure) return true;

                // Einstellungen wurden geändert, während das Menü offen war
                if ((repair && !Main.Settings.TrackRepair) || (measure && !Main.Settings.MeasureRun)) return false;

                TQ_Open.Open(__instance, repair, measure);
                return false;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("CM HandleInput (open) failed: " + e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(CareerManagerInfoScreen), "Disable")]
    internal static class TQ_Info_Disable
    {
        static void Postfix()
        {
            // Messfahrt endet, sobald der Bildschirm gewechselt wird (Bremse wird dadurch gelöst)
            MeasureRun.Stop();

            // Beim Wechsel zum Bezahlbildschirm bleibt der Listenzustand erhalten
            if (!TQ_Payment.Active)
                TQ_ListState.Stop();
        }
    }

    // Listeneingaben
    [HarmonyPatch(typeof(CareerManagerInfoScreen), "HandleInputAction")]
    internal static class TQ_Info_HandleInput
    {
        static bool Prefix(CareerManagerInfoScreen __instance, InputAction input)
        {
            // Messfahrt: nur Abbrechen beendet sie, alles andere wird geschluckt
            if (MeasureRun.Active && ReferenceEquals(__instance, MeasureRun.Info))
            {
                if (input == InputAction.Cancel)
                    MeasureRun.Exit();
                return false;
            }

            // Hinweis-Bildschirm: alles außer Abbrechen schlucken
            if (TQ_ListState.MessageActive && ReferenceEquals(__instance, TQ_ListState.Info))
            {
                if (input == InputAction.Cancel)
                {
                    TQ_ListState.MessageActive = false;
                    var target = TQ_ListState.MainScreen as IDisplayScreen ?? __instance;
                    __instance.screenSwitcher?.SetActiveDisplay(target);
                }
                return false;
            }

            if (!TQ_ListState.Active || !ReferenceEquals(__instance, TQ_ListState.Info))
                return true;

            switch (input)
            {
                case InputAction.Up:
                    TQ_ListState.MoveSelection(-1);
                    return false;

                case InputAction.Down:
                    TQ_ListState.MoveSelection(+1);
                    return false;

                case InputAction.Confirm:
                {
                    if (TQ_ListState.Items.Count == 0) return false;

                    // Reparatursperre: während eine Reparatur läuft, keine weitere beginnen
                    if (TQ_ListState.RepairLocked) return false;

                    // Fahrzeug arbeitet gerade für eine andere Mod (z. B. Weichenreparatur)
                    if (MaintenanceBusy.BusyByOther(TQ_ListState.SelfCar))
                    {
                        TQ_ListState.Status = Loc.T("repair.vehicleBusy");
                        TQ_ListState.Render();
                        return false;
                    }

                    RepairEntry entry = TQ_ListState.Items[TQ_ListState.Selected];
                    if (entry == null || entry.Track == null) return false;

                    // Nur im Stand reparieren
                    if (!TQ_Helpers.IsStandingSelf(TQ_ListState.SelfCar))
                    {
                        TQ_ListState.KickOut();
                        return false;
                    }

                    RepairQuote quote = entry.Quote;

                    // Vergütung oder nichts zu zahlen (kostenlos eingestellt / Versicherung trägt alles): sofort reparieren
                    if (quote.IsReward || quote.Pay <= 0.0)
                    {
                        TQ_Payment.ApplyRepair(entry, quote);
                        TQ_ListState.Render();
                        return false;
                    }

                    var licensePay = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                    if (licensePay == null || licensePay.cashReg == null)
                    {
                        Main.ModEntry.Logger.Error("Payment screen not found");
                        return false;
                    }

                    licensePay.cashReg.ClearCurrentTransaction();
                    licensePay.cashReg.SetTotalCost(quote.Pay);

                    TQ_Payment.Start(entry, quote);
                    licensePay.screenSwitcher?.SetActiveDisplay(licensePay);
                    return false;
                }

                case InputAction.Cancel:
                    // Eigene Reparatur läuft: Ladebalken bleibt bis zum Abschluss stehen
                    if (RepairHold.Locked) return false;
                    if (TQ_ListState.MainScreen != null)
                        __instance.screenSwitcher?.SetActiveDisplay(TQ_ListState.MainScreen);
                    return false;

                default:
                    return true;
            }
        }
    }

    // =========================
    // BEZAHLBILDSCHIRM (wie bei JunctionMaintenance)
    // =========================

    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "Activate")]
    internal static class TQ_LicensePay_Activate
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance)
        {
            if (!TQ_Payment.Active) return true;

            try
            {
                var cashReg = __instance.cashReg;
                if (cashReg == null) return true;

                cashReg.ClearCurrentTransaction();
                cashReg.SetTotalCost(TQ_Payment.Cost);

                var entry = TQ_Payment.Entry;
                string name = entry != null ? entry.TrackId : Loc.T("unknown");

                if (__instance.title1 != null) __instance.title1.text = Loc.T("repair.title");
                if (__instance.title2 != null) __instance.title2.text = Loc.T("pay.section");
                if (__instance.licenseNameText != null) __instance.licenseNameText.text = name;
                if (__instance.licensePriceText != null) __instance.licensePriceText.text = Loc.Money(TQ_Payment.Cost);
                if (__instance.insertWallet != null)
                    __instance.insertWallet.text = TQ_Payment.Quote.Covered > 0.0
                        ? Loc.Format("pay.insurance", Loc.Money(TQ_Payment.Quote.Covered)) + "\n" + Loc.T("pay.insertWallet")
                        : Loc.T("pay.insertWallet");
                if (__instance.depositedText != null) __instance.depositedText.text = Loc.T("pay.deposited");
                if (__instance.depositedValue != null) __instance.depositedValue.text = Loc.Money(cashReg.DepositedCash);

                cashReg.CashAdded -= OnCashAdded;
                cashReg.CashAdded += OnCashAdded;
                return false;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("LicensePay.Activate override error: " + e);
                return true;
            }
        }

        internal static void OnCashAdded()
        {
            try
            {
                var screen = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                if (screen == null || screen.cashReg == null || screen.depositedValue == null) return;
                screen.depositedValue.text = Loc.Money(screen.cashReg.DepositedCash);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "Disable")]
    internal static class TQ_LicensePay_Disable
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance)
        {
            if (!TQ_Payment.Active) return true;

            try
            {
                var cashReg = __instance.cashReg;
                if (cashReg != null)
                {
                    cashReg.CashAdded -= TQ_LicensePay_Activate.OnCashAdded;
                    cashReg.ClearCurrentTransaction();
                }

                Clear(__instance.title1);
                Clear(__instance.title2);
                Clear(__instance.licenseNameText);
                Clear(__instance.licensePriceText);
                Clear(__instance.insertWallet);
                Clear(__instance.depositedText);
                Clear(__instance.depositedValue);
                return false;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("LicensePay.Disable override error: " + e);
                return true;
            }
        }

        private static void Clear(TextMeshPro tmp) { if (tmp != null) tmp.text = string.Empty; }
    }

    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "HandleInputAction")]
    internal static class TQ_LicensePay_HandleInput
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance, InputAction input)
        {
            if (!TQ_Payment.Active) return true;

            switch (input)
            {
                case InputAction.Cancel:
                    try
                    {
                        __instance.cashReg?.ClearCurrentTransaction();

                        TQ_ListState.Active = true;
                        TQ_ListState.Rebuild();
                        TQ_ListState.Render();

                        IDisplayScreen target = (TQ_ListState.Info as IDisplayScreen)
                                                ?? (TQ_ListState.MainScreen as IDisplayScreen)
                                                ?? __instance;
                        __instance.screenSwitcher?.SetActiveDisplay(target);
                    }
                    catch (Exception e)
                    {
                        Main.ModEntry.Logger.Error("LicensePay.Cancel error: " + e);
                        TQ_ListState.MainScreen?.screenSwitcher?.SetActiveDisplay(TQ_ListState.MainScreen);
                    }
                    finally
                    {
                        TQ_Payment.Clear();
                    }
                    return false;

                case InputAction.Confirm:
                    try
                    {
                        // Die Reparatur selbst passiert im Postfix von Buy()
                        if (__instance.cashReg != null) __instance.cashReg.Buy();
                    }
                    catch (Exception e)
                    {
                        Main.ModEntry.Logger.Error("LicensePay.Confirm error: " + e);
                    }
                    return false;

                default:
                    return true;
            }
        }
    }

    // ---------- Kasse ----------

    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "GetTotalCost")]
    internal static class TQ_CashReg_GetTotalCost
    {
        static bool Prefix(ref double __result)
        {
            if (!TQ_Payment.Active) return true;
            __result = Math.Max(0.0, TQ_Payment.Cost);
            return false;
        }
    }

    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "TotalUnitsInBasket")]
    internal static class TQ_CashReg_TotalUnits
    {
        static bool Prefix(ref float __result)
        {
            if (!TQ_Payment.Active) return true;
            __result = 1f; // mindestens eine "Einheit", sonst bricht Buy() ab
            return false;
        }
    }

    // Erfolgreich bezahlt -> Abschnitt reparieren, zurück zur Liste
    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "Buy")]
    internal static class TQ_Payment_OnBuy
    {
        static void Postfix(bool __result)
        {
            if (!__result || !TQ_Payment.Active) return;
            // (Reparatursperre: die Kasse wird nur geöffnet, wenn keine Reparatur läuft)
            // (Bewegung wird in TQ_ListState.Tick abgefangen, bevor bezahlt werden kann)

            try
            {
                RepairEntry entry = TQ_Payment.Entry;
                RepairQuote quote = TQ_Payment.Quote;

                if (entry != null)
                    TQ_Payment.ApplyRepair(entry, quote);

                if (TQ_ListState.Info != null && TQ_ListState.MainScreen != null)
                {
                    TQ_ListState.Active = true;
                    TQ_ListState.Render();
                    TQ_ListState.MainScreen.screenSwitcher.SetActiveDisplay(TQ_ListState.Info);
                }
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Repair after payment failed: " + e);
            }
            finally
            {
                TQ_Payment.Clear();
            }
        }
    }
}