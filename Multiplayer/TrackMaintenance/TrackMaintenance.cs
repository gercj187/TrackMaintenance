using UnityModManagerNet;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using DV.PointSet;

namespace TrackMaintenance
{
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry;

        // Eigene, gespeicherte Einstellungen
        public static Settings LocalSettings;
        // Wirksame Einstellungen: im Einzelspieler und als Host = LocalSettings,
        // als Multiplayer-Client eine temporäre Kopie mit den Werten des Hosts
        public static Settings Settings;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            ModEntry = entry;
            LocalSettings = UnityModManager.ModSettings.Load<Settings>(entry) ?? new Settings();
            Settings = LocalSettings;

            // Sprachen laden (eingebaut + <Mod>/lang/*.txt)
            Loc.Init(entry.Path);

            entry.OnGUI = OnGUI;
            entry.OnSaveGUI = OnSaveGUI;
            entry.OnUpdate = OnUpdate;

            var harmony = new Harmony(entry.Info.Id);
            harmony.PatchAll();

            // Multiplayer (dv-multiplayer)
            try { TM_Multiplayer.Initialize(); }
            catch (Exception e) { ModEntry.Logger.Error("Multiplayer initialization failed: " + e); }

            Log("TrackMaintenance loaded");

            return true;
        }

        // ---------- Einstellungen / Multiplayer ----------

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            // Als Client legt der Host die Einstellungen fest
            if (TM_Multiplayer.IsClient)
            {
                var style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true,
                    fontStyle = FontStyle.Bold
                };
                GUILayout.Box(Loc.T("set.mpClient"), style, GUILayout.ExpandWidth(true));
                return;
            }

            Settings.Draw(entry);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry entry)
        {
            if (TM_Multiplayer.IsClient) return;   // Client speichert nicht, Host gibt vor

            LocalSettings.Save(entry);
            TM_Multiplayer.BroadcastHostSettings();
        }

        // Client: Einstellungen des Hosts vorübergehend verwenden (lokale bleiben unverändert)
        internal static void UseHostSettings(Settings hostSettings, RepairMode hostMode)
        {
            if (hostSettings == null) return;
            Settings = hostSettings;
            RepairEconomy.HostMode = hostMode;
            TrackLicenses.ApplyValues();
        }

        // Verbindung beendet: eigene Einstellungen wieder verwenden
        internal static void RestoreLocalSettings()
        {
            bool changed = !ReferenceEquals(Settings, LocalSettings) || RepairEconomy.HostMode.HasValue;
            Settings = LocalSettings;
            RepairEconomy.HostMode = null;
            if (!changed) return;

            TrackLicenses.ApplyValues();
            Log("[MP] Local settings restored");
        }

        // Normale Meldungen und Warnungen nur mit "Print debug logs".
        // Fehler (Logger.Error) werden immer ausgegeben, sonst sind Fehlerberichte unmöglich.
        public static void Log(string message)
        {
            if (Settings != null && Settings.printDebugLogs) ModEntry.Logger.Log(message);
        }

        public static void Warn(string message)
        {
            if (Settings != null && Settings.printDebugLogs) ModEntry.Logger.Warning(message);
        }

        // Wird von UMM jeden Frame aufgerufen
        private static void OnUpdate(UnityModManager.ModEntry entry, float dt)
        {
            try
            {
                MeasureRun.Tick();
            }
            catch (Exception e)
            {
                ModEntry.Logger.Error("Measuring run failed: " + e);
                MeasureRun.Stop();
            }

            try
            {
                // Zu entlüftende Bremsverbände bestimmen (eigene Messfahrt + Anfragen von Clients)
                PenaltyBrake.Tick();
            }
            catch (Exception e)
            {
                ModEntry.Logger.Error("Penalty brake failed: " + e);
            }

            try
            {
                // Preis und Copay der Lizenzen aus den Einstellungen übernehmen
                TrackLicenses.Tick();

                // Gespeicherte Gleisschäden anwenden (falls beim Laden noch nicht geschehen)
                TrackPersistence.TryApplyPendingLate();

                // Schwellen neu geladener Mesh-Zellen auf den Stand des Verlaufs bringen
                LiveDeform.SyncLoadedSleepers();
            }
            catch (Exception e)
            {
                ModEntry.Logger.Error("Track state sync failed: " + e);
            }

            // Verformungen erzeugt nur der Host (bzw. der Einzelspieler)
            if (!Settings.DeformWhenDragged || TM_Multiplayer.IsClient) return;

            try
            {
                DerailTracker.Tick(dt);
            }
            catch (Exception e)
            {
                ModEntry.Logger.Error("Derail tracker failed: " + e);
            }
        }
    }

    // =========================
    // AUSLÖSER: ENTGLEISTE WAGEN
    // =========================

    // "Deform at derailment": einmalige Verformung im Moment der Entgleisung (auch im Stand).
    // "Deform when dragged": entgleiste Wagen werden verfolgt; solange sie schneller als die
    // Mindestgeschwindigkeit sind, wird in festem Intervall das Gleis unter ihnen verformt.
    public static class DerailTracker
    {
        private static readonly Dictionary<TrainCar, float> cars = new Dictionary<TrainCar, float>();

        public static void Register(TrainCar car)
        {
            if (car == null || cars.ContainsKey(car)) return;

            // Multiplayer-Client: Entgleisungen kommen vom Host, Verformungen ebenso
            if (TM_Multiplayer.IsClient) return;

            if (Main.Settings.DeformAtDerail)
                DeformForCar(car, "derailment");

            if (Main.Settings.DeformWhenDragged)
            {
                Main.Log($"Tracking derailed car '{car.name}'");
                // Nach einer Verformung bei der Entgleisung erst nach einem Intervall weiter,
                // sonst sofort, sobald der Wagen schnell genug ist
                cars[car] = Main.Settings.DeformAtDerail ? 0f : Main.Settings.IntervalSeconds;
            }
        }

        // Berechnet Schaden aus Gewicht/Tempo/Lok und verformt das Gleis unter dem Wagen
        private static void DeformForCar(TrainCar car, string trigger)
        {
            float speedKmh = car.rb != null ? car.rb.velocity.magnitude * 3.6f : 0f;
            float massKg = car.rb != null ? car.rb.mass : 0f;
            bool isLoco = car.IsLoco;
            float multiplier;
            float damage = Main.Settings.CalculateDamage(massKg, speedKmh, isLoco, out multiplier);

            LiveDeform.DeformTrackNear(car.transform.position, Main.Settings.Radius, damage,
                $"{trigger}: car '{car.name}' {massKg / 1000f:F1} t at {speedKmh:F1} km/h, loco={isLoco}, x{multiplier:F2}");
        }

        public static void Tick(float dt)
        {
            if (cars.Count == 0) return;

            float interval = Main.Settings.IntervalSeconds;
            float minSpeed = Main.Settings.MinSpeedKmh;

            foreach (TrainCar car in cars.Keys.ToList())
            {
                // Zerstört, in den Pool zurückgegeben oder wieder aufgegleist
                if (car == null || !car.derailed)
                {
                    cars.Remove(car);
                    continue;
                }

                float timer = cars[car] + dt;
                if (timer < interval)
                {
                    cars[car] = timer;
                    continue;
                }

                float speedKmh = car.rb != null ? car.rb.velocity.magnitude * 3.6f : 0f;
                if (speedKmh <= minSpeed)
                {
                    // Zu langsam: warten, bis der Wagen wieder schnell genug ist
                    cars[car] = interval;
                    continue;
                }

                cars[car] = 0f;
                DeformForCar(car, "dragged");
            }
        }
    }

    [HarmonyPatch(typeof(TrainCar), nameof(TrainCar.Derail))]
    public static class TrainCarDerailPatch
    {
        public static void Postfix(TrainCar __instance)
        {
            if (!Main.Settings.DeformAtDerail && !Main.Settings.DeformWhenDragged) return;
            if (__instance.derailed)
                DerailTracker.Register(__instance);
        }
    }

    // =========================
    // AUSLÖSER: EXPLOSIONEN
    // =========================

    // Jede Explosion (z. B. explodierender Tank) verformt alle Gleise im eingestellten Radius
    // mit Basisschaden * Explosions-Multiplikator.
    [HarmonyPatch(typeof(TrainCarExplosion), nameof(TrainCarExplosion.CreateExplosion))]
    public static class TrainCarExplosionPatch
    {
        public static void Postfix(Vector3 explosionPosition)
        {
            if (!Main.Settings.DeformByExplosion || TM_Multiplayer.IsClient) return;

            try
            {
                float damage = Main.Settings.CalculateExplosionDamage();
                LiveDeform.DeformAllTracksNear(explosionPosition, Main.Settings.ExplosionRadius, damage,
                    $"explosion (damage {damage:F2} m)");
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Explosion deformation failed: " + e);
            }
        }
    }

    // =========================
    // LIVE DEFORMATION
    // =========================

    // Parameter einer einzelnen Verformung (entsprechen den Argumenten von KinkPointSet im Spiel)
    public struct KinkParams
    {
        public float horizontal;  // kinkScale
        public float vertical;    // verticalKinkScale
        public float roll;        // rotationKinkScale (Grad)
        public float frequency;   // kinkFrequency
        public float seed;        // Verschiebung des Noise-Musters
        public float fade;        // Länge des Ausblendens am Rand (m)
    }

    // Ein Eingriff am Gleis: Verformung (mit allen Parametern inkl. Seed) oder Reparatur.
    // Aus der Folge dieser Eingriffe lässt sich der Zustand eines Gleises exakt wiederherstellen.
    public sealed class TrackOp
    {
        public bool IsRepair;

        // Verformung
        public double Center;
        public float Radius;
        public KinkParams Kink;

        // Reparatur
        public double Start;
        public double End;
        public float Blend;

        public static TrackOp Deformation(double center, float radius, KinkParams kink)
        {
            return new TrackOp { IsRepair = false, Center = center, Radius = radius, Kink = kink };
        }

        public static TrackOp Repair(double start, double end, float blend)
        {
            return new TrackOp { IsRepair = true, Start = start, End = end, Blend = blend };
        }
    }

    // Verlauf aller Eingriffe pro Gleis (wird im Spielstand gespeichert).
    // Die Generation zählt hoch, wenn ein Verlauf aufgeräumt wird; Schwellen-Kopien mit älterer
    // Generation werden dann vom Original aus neu aufgebaut.
    public static class TrackHistory
    {
        private static readonly Dictionary<RailTrack, List<TrackOp>> ops = new Dictionary<RailTrack, List<TrackOp>>();
        private static readonly Dictionary<RailTrack, int> generations = new Dictionary<RailTrack, int>();

        // Multiplayer: seit dem letzten Senden geänderte Gleise (der Host verteilt sie an die Clients)
        private static readonly HashSet<RailTrack> dirty = new HashSet<RailTrack>();

        public static List<RailTrack> TakeDirty()
        {
            var list = new List<RailTrack>(dirty.Count);
            foreach (RailTrack t in dirty) if (t != null) list.Add(t);
            dirty.Clear();
            return list;
        }

        public static void ClearDirty() { dirty.Clear(); }

        public static bool Any { get { return ops.Count > 0; } }

        // Auch Gleise mit leerem Verlauf zählen (ihre Schwellen müssen evtl. noch zurückgesetzt werden)
        public static bool Has(RailTrack track) { return track != null && ops.ContainsKey(track); }

        public static List<TrackOp> Get(RailTrack track)
        {
            List<TrackOp> list;
            return track != null && ops.TryGetValue(track, out list) ? list : null;
        }

        public static int Generation(RailTrack track)
        {
            int g;
            return track != null && generations.TryGetValue(track, out g) ? g : 0;
        }

        public static void Add(RailTrack track, TrackOp op)
        {
            if (track == null || op == null) return;
            List<TrackOp> list;
            if (!ops.TryGetValue(track, out list))
            {
                list = new List<TrackOp>();
                ops[track] = list;
            }
            list.Add(op);
            dirty.Add(track);
        }

        // Verlauf ersetzen (nach dem Aufräumen); leere Liste = Gleis ist wieder im Originalzustand
        public static void Replace(RailTrack track, List<TrackOp> newOps)
        {
            if (track == null) return;
            ops[track] = newOps ?? new List<TrackOp>();
            generations[track] = Generation(track) + 1;
            dirty.Add(track);
        }

        // Nur noch existierende Gleise mit Einträgen (Unity-Null nach Szenenwechsel wird ausgefiltert)
        public static List<KeyValuePair<RailTrack, List<TrackOp>>> All()
        {
            var result = new List<KeyValuePair<RailTrack, List<TrackOp>>>();
            foreach (var kv in ops)
                if (kv.Key != null && kv.Value.Count > 0) result.Add(kv);
            return result;
        }

        public static void Reset()
        {
            ops.Clear();
            generations.Clear();
            dirty.Clear();
        }
    }

    // =========================
    // WEICHENGLEISE
    // =========================
    // Weichen sind feste Modelle, an die Gleis-Splines andocken. Ihre beiden Fahrwege
    // ("[track through]" gerade, "[track diverging]" abzweigend) werden nie verformt und nie
    // gespeichert, sonst passt der Fahrweg nicht mehr zum unveränderten Weichenmodell.
    // Die angrenzenden Gleise bleiben an ihren Enden ohnehin unverändert (EndBuffer),
    // der Anschluss an die Weiche bleibt also sauber.
    public static class JunctionTracks
    {
        private sealed class Flag { public bool Value; }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RailTrack, Flag> cache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<RailTrack, Flag>();

        private static bool resolved;
        private static System.Reflection.FieldInfo isJunctionField;
        private static System.Reflection.PropertyInfo isJunctionProp;
        private static Type junctionType;

        public static bool Is(RailTrack track)
        {
            if (track == null) return false;
            return cache.GetValue(track, t => new Flag { Value = Detect(t) }).Value;
        }

        private static bool Detect(RailTrack track)
        {
            Resolve();

            // 1. Kennzeichen am Gleis (falls das Spiel eines hat)
            try
            {
                if (isJunctionField != null && (bool)isJunctionField.GetValue(track)) return true;
                if (isJunctionProp != null && (bool)isJunctionProp.GetValue(track, null)) return true;
            }
            catch { }

            // 2. Weiche als übergeordnetes Objekt
            if (junctionType != null && track.GetComponentInParent(junctionType) != null) return true;

            // 3. Namen der Weichen-Fahrwege
            string n = track.name ?? string.Empty;
            return n.IndexOf("[track through]", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("[track diverging]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance;
            isJunctionField = typeof(RailTrack).GetField("isJunctionTrack", flags);
            if (isJunctionField != null && isJunctionField.FieldType != typeof(bool)) isJunctionField = null;
            isJunctionProp = typeof(RailTrack).GetProperty("IsJunctionTrack", flags);
            if (isJunctionProp != null && isJunctionProp.PropertyType != typeof(bool)) isJunctionProp = null;

            junctionType = AccessTools.TypeByName("Junction");
            if (junctionType != null && !typeof(Component).IsAssignableFrom(junctionType)) junctionType = null;
        }
    }

    public static class LiveDeform
    {
        // Punkte am Gleisanfang/-ende bleiben unverändert (wie im Original)
        private const int EndBuffer = 4;

        private static RailwayMeshGenerator meshGen;

        // Sucht das Gleis nächst der Position und verformt es dort, wenn es
        // höchstens 'radius' entfernt ist. Der Radius bestimmt auch die Länge
        // des verformten Abschnitts (radius m in jede Richtung).
        // 'damage' ist die maximale Verschiebung in Metern (seitlich); die
        // vertikale Verschiebung ist damage * verticalRatio.
        public static void DeformTrackNear(Vector3 position, float radius, float damage, string reason)
        {
            var closest = RailTrack.GetClosest(position);
            RailTrack track = closest.track;
            if (track == null || closest.point == null) return;

            // Weichengleis: stattdessen das nächste normale Gleis im Radius nehmen
            if (JunctionTracks.Is(track))
            {
                var alt = ClosestNonJunctionTrack(position, radius);
                if (alt.track == null) return;
                DeformAt(alt.track, alt.span, alt.sqrDist, radius, damage, reason);
                return;
            }

            // GetClosestPoint liefert die QUADRIERTE Distanz
            var (_, sqrDist) = RailTrack.GetClosestPoint(track, position);
            if (sqrDist > radius * radius) return;

            DeformAt(track, closest.point.Value.span, sqrDist, radius, damage, reason);
        }

        private static (RailTrack track, double span, float sqrDist) ClosestNonJunctionTrack(Vector3 position, float radius)
        {
            RailTrack best = null;
            double bestSpan = 0;
            float bestSqr = radius * radius;

            var tracks = RailTrackRegistryBase.RailTracks;
            if (tracks == null) return (null, 0, 0);

            foreach (var t in tracks)
            {
                if (t == null || JunctionTracks.Is(t)) continue;
                var (point, sqrDist) = RailTrack.GetClosestPoint(t, position);
                if (point == null || sqrDist > bestSqr) continue;
                best = t;
                bestSpan = point.Value.span;
                bestSqr = sqrDist;
            }
            return (best, bestSpan, bestSqr);
        }

        // Verformt ALLE Gleise, die höchstens 'radius' von der Position entfernt sind
        // (z. B. bei einer Explosion, die mehrere Gleise gleichzeitig trifft).
        // Wichtig: Erst die Schwellen ALLER Gleise einsammeln, dann alles verformen und die Meshes
        // nur einmal am Ende neu bauen. RefreshMeshes wirft die Chunks ganzer Zellen weg, also auch
        // die Schwellen der Nachbargleise; einzeln nacheinander würden deren Schwellen sonst fehlen.
        public static void DeformAllTracksNear(Vector3 position, float radius, float damage, string reason)
        {
            var tracks = RailTrackRegistryBase.RailTracks;
            if (tracks == null) return;

            var hits = new List<(RailTrack track, double span, float sqrDist)>();
            foreach (var track in tracks)
            {
                if (track == null || JunctionTracks.Is(track)) continue;

                // GetClosestPoint liefert die QUADRIERTE Distanz
                var (point, sqrDist) = RailTrack.GetClosestPoint(track, position);
                if (point == null || sqrDist > radius * radius) continue;

                hits.Add((track, point.Value.span, sqrDist));
            }
            if (hits.Count == 0) return;

            var gen = GetMeshGenerator();
            var activeChunks = GetActiveChunks(gen);

            // 1. Schwellen aller getroffenen Gleise einsammeln, solange die Chunks noch existieren
            var sleepersByTrack = new Dictionary<RailTrack, HashSet<EquiPointSet>>();
            foreach (var hit in hits)
                sleepersByTrack[hit.track] = CollectSleeperSets(activeChunks, hit.track);

            // 2. Alle Gleise verformen
            var deformed = new HashSet<RailTrack>();
            foreach (var hit in hits)
            {
                var kink = MakeKink(damage, radius);
                if (!DeformPoints(hit.track, hit.span, radius, kink, sleepersByTrack[hit.track])) continue;

                DamageLedger.Add(hit.track, hit.span, radius, kink.fade, kink.horizontal);
                deformed.Add(hit.track);
                LogDeform(hit.track, hit.span, kink, reason, hit.sqrDist);
            }

            // 3. Meshes einmal für alle neu bauen
            RefreshMeshes(gen, activeChunks, deformed);

            Main.Log($"{reason}: {deformed.Count} track(s) deformed within {radius:F0} m");
        }

        // Verformt ein einzelnes Gleis um centerSpan.
        private static void DeformAt(RailTrack track, double centerSpan, float sqrDist,
                                     float radius, float damage, string reason)
        {
            var kink = MakeKink(damage, radius);

            if (!Deform(track, centerSpan, radius, kink)) return;

            DamageLedger.Add(track, centerSpan, radius, kink.fade, kink.horizontal);
            LogDeform(track, centerSpan, kink, reason, sqrDist);
        }

        // Wie RailTrack.KinkPointSet im Spiel: 'damage' entspricht kinkScale, die echte
        // Verschiebung ist (Perlin - 0.5) * Scale, also maximal +-Scale/2.
        // Der Seed verschiebt das Noise-Muster, damit jede Verformung anders aussieht.
        private static KinkParams MakeKink(float damage, float radius)
        {
            return new KinkParams
            {
                horizontal = damage,
                vertical = damage * Main.Settings.VerticalRatio,
                roll = damage * Main.Settings.RollPerMeter,
                frequency = Main.Settings.Frequency,
                seed = UnityEngine.Random.Range(0f, 1000f),
                fade = Mathf.Min(Main.Settings.EdgeFade, radius)
            };
        }

        private static void LogDeform(RailTrack track, double centerSpan, KinkParams kink, string reason, float sqrDist)
        {
            Main.Log(
                $"Deformed '{track.name}' at span {centerSpan:F1} m, kink scale h {kink.horizontal:F3} / v {kink.vertical:F3} / roll {kink.roll:F2} deg, freq {kink.frequency:F2} ({reason}, distance {Mathf.Sqrt(sqrDist):F1} m)");
        }

        // Verformt Fahrpfad, Schwellen-Punktesatz und baut das Mesh des Gleises neu.
        public static bool Deform(RailTrack track, double centerSpan, float radius, KinkParams kink)
        {
            var gen = GetMeshGenerator();
            var activeChunks = GetActiveChunks(gen);

            var sleeperSets = CollectSleeperSets(activeChunks, track);
            if (!DeformPoints(track, centerSpan, radius, kink, sleeperSets)) return false;

            // Schienen-, Schotter- und Schwellen-Mesh neu erzeugen lassen
            RefreshMeshes(gen, activeChunks, new HashSet<RailTrack> { track });
            return true;
        }

        // Schwellen-Punktesätze liegen als eigene Kopie in den Chunks (vor der Verformung einsammeln)
        private static HashSet<EquiPointSet> CollectSleeperSets(
            Dictionary<Vector2Int, List<TrackChunk>> activeChunks, RailTrack track)
        {
            var sleeperSets = new HashSet<EquiPointSet>();
            EquiPointSet trackSet = track != null ? track.GetKinkedPointSet() : null;
            if (activeChunks == null || trackSet == null) return sleeperSets;

            foreach (var list in activeChunks.Values)
                foreach (var chunk in list)
                    if (chunk.isSleepers && chunk.track == track && chunk.pointSet != trackSet)
                        sleeperSets.Add(chunk.pointSet);
            return sleeperSets;
        }

        // Verschiebt Fahrpfad und Schwellen, ohne Meshes neu zu bauen.
        // Jede Verformung landet im Verlauf des Gleises (TrackHistory, wird gespeichert).
        // Schwellen-Kopien werden über SyncSleeper auf den Stand des Verlaufs gebracht.
        private static bool DeformPoints(RailTrack track, double centerSpan, float radius, KinkParams kink,
                                         HashSet<EquiPointSet> sleeperSets)
        {
            EquiPointSet trackSet = track != null ? track.GetKinkedPointSet() : null;
            if (trackSet == null || trackSet.points == null || radius <= 0f) return false;
            if (JunctionTracks.Is(track)) return false; // Weichen nie verformen

            // Originalzustand merken (für die Reparatur), bevor zum ersten Mal verformt wird
            TrackSnapshots.EnsureTrack(track, trackSet);
            ApplyOffsets(trackSet, centerSpan, radius, kink);

            TrackHistory.Add(track, TrackOp.Deformation(centerSpan, radius, kink));
            foreach (var s in sleeperSets)
                SyncSleeper(track, s);

            // Fahrwerke informieren
            track.TrackPointsUpdated_Invoke();
            return true;
        }

        // =========================
        // SCHWELLEN NACHZIEHEN
        // =========================
        // Schwellen liegen als eigene Punktesatz-Kopien in den Mesh-Chunks. Kopien für Zellen, die bei
        // einer Verformung nicht geladen waren (oder nach dem Laden eines Spielstands), entstehen erst
        // später und wären dann gerade. Deshalb zählt jede Kopie mit, wie viele Schritte des
        // Gleis-Verlaufs sie schon enthält, und bekommt fehlende Schritte nachgespielt.

        private sealed class AppliedCount { public int N; public int Gen; }

        private static System.Runtime.CompilerServices.ConditionalWeakTable<EquiPointSet, AppliedCount> sleeperApplied =
            new System.Runtime.CompilerServices.ConditionalWeakTable<EquiPointSet, AppliedCount>();

        private static float nextSleeperSync;
        private const float SleeperSyncInterval = 0.5f;

        // Grenzen für "ganzer Punktesatz" beim vollständigen Zurücksetzen
        private const double WholeFrom = -1e9;
        private const double WholeTo = 1e9;

        // Bringt eine Schwellen-Kopie auf den Stand des Verlaufs. true = es wurde etwas geändert.
        private static bool SyncSleeper(RailTrack track, EquiPointSet set)
        {
            if (set == null || set.points == null || track == null) return false;
            List<TrackOp> ops = TrackHistory.Get(track) ?? new List<TrackOp>();

            AppliedCount c = sleeperApplied.GetOrCreateValue(set);
            bool changed = false;

            // Verlauf wurde aufgeräumt: Kopie zurück auf Original, dann neu aufbauen
            int gen = TrackHistory.Generation(track);
            if (c.Gen != gen)
            {
                if (c.N > 0)
                {
                    TrackSnapshots.RestoreSleeper(set, WholeFrom, WholeTo, 0f);
                    changed = true;
                }
                c.N = 0;
                c.Gen = gen;
            }

            if (c.N >= ops.Count) return changed;

            // Vor der ersten Änderung den Originalzustand merken
            if (c.N == 0) TrackSnapshots.EnsureSleeper(set);

            for (int i = c.N; i < ops.Count; i++)
            {
                TrackOp op = ops[i];
                if (op.IsRepair)
                    TrackSnapshots.RestoreSleeper(set, op.Start, op.End, op.Blend);
                else
                    ApplyOffsets(set, op.Center, op.Radius, op.Kink);
            }

            c.N = ops.Count;
            return true;
        }

        // Aus Main.OnUpdate: neu geladene Schwellen-Kopien beschädigter Gleise nachziehen
        public static void SyncLoadedSleepers()
        {
            if (Time.time < nextSleeperSync) return;
            nextSleeperSync = Time.time + SleeperSyncInterval;
            if (!TrackHistory.Any) return;

            var gen = GetMeshGenerator();
            var activeChunks = GetActiveChunks(gen);
            if (activeChunks == null) return;

            // Erst einsammeln, dann ändern (RefreshMeshes verändert activeChunks)
            var pending = new List<(RailTrack track, EquiPointSet set)>();
            foreach (var list in activeChunks.Values)
                foreach (var chunk in list)
                {
                    if (!chunk.isSleepers || chunk.track == null || chunk.pointSet == null) continue;
                    if (!TrackHistory.Has(chunk.track)) continue;
                    if (chunk.pointSet == chunk.track.GetKinkedPointSet()) continue;
                    pending.Add((chunk.track, chunk.pointSet));
                }

            var changed = new HashSet<RailTrack>();
            foreach (var p in pending)
                if (SyncSleeper(p.track, p.set))
                    changed.Add(p.track);

            if (changed.Count > 0)
                RefreshMeshes(gen, activeChunks, changed);
        }

        // =========================
        // SPIELSTAND: VERLAUF NACHSPIELEN
        // =========================

        // Alles vergessen (neuer Spielstand wird geladen, alte Gleis-Objekte existieren nicht mehr)
        public static void ResetState()
        {
            TrackHistory.Reset();
            TrackSnapshots.Reset();
            DamageLedger.Reset();
            TrackIds.ResetJunctionCache();
            TrackRefs.Reset();
            sleeperApplied = new System.Runtime.CompilerServices.ConditionalWeakTable<EquiPointSet, AppliedCount>();
            meshGen = null;
        }

        // Spielt den gespeicherten Verlauf auf das unveränderte Gleis nach (Fahrpfad + Schadensverzeichnis).
        // Schwellen folgen über SyncLoadedSleepers, sobald ihre Meshes entstehen.
        public static bool ReplayHistory(RailTrack track, List<TrackOp> ops)
        {
            EquiPointSet trackSet = track != null ? track.GetKinkedPointSet() : null;
            if (trackSet == null || trackSet.points == null || ops == null || ops.Count == 0) return false;
            if (JunctionTracks.Is(track)) return false; // alte Spielstände: Weichen nicht verformen

            TrackSnapshots.EnsureTrack(track, trackSet);

            foreach (TrackOp op in ops)
            {
                ApplyOpToTrack(track, trackSet, op);
                TrackHistory.Add(track, op);
            }

            track.TrackPointsUpdated_Invoke();
            return true;
        }

        // Einen Eingriff auf den Fahrpfad und das Schadensverzeichnis anwenden
        private static void ApplyOpToTrack(RailTrack track, EquiPointSet trackSet, TrackOp op)
        {
            if (op.IsRepair)
            {
                TrackSnapshots.RestoreTrack(track, trackSet, op.Start, op.End, op.Blend);
                DamageLedger.Clear(track, op.Start, op.End, op.Blend);
            }
            else
            {
                ApplyOffsets(trackSet, op.Center, op.Radius, op.Kink);
                DamageLedger.Add(track, op.Center, op.Radius, op.Kink.fade, op.Kink.horizontal);
            }
        }

        // =========================
        // VERLAUF AUFRÄUMEN (nach jeder Reparatur)
        // =========================
        // 1. Verformungen, deren Wirkungsbereich komplett in einem SPÄTER reparierten Abschnitt liegt,
        //    fallen weg (die Reparatur hebt sie vollständig auf).
        // 2. Reparaturen am Anfang des Verlaufs (ohne Verformung davor) fallen weg (nichts zu reparieren).
        // 3. Ist auf dem ganzen Gleis kein Schaden mehr (alle Abschnitte 0 %), fällt der ganze Verlauf weg.
        // Danach wird das Gleis auf das Original gesetzt und der bereinigte Verlauf neu abgespielt,
        // damit der Zustand im Spiel exakt dem entspricht, was nach dem Laden entstehen würde.

        private const float HealedThreshold01 = 0.005f; // unter 0,5 % = als heil gerundet

        private static void CompactHistory(RailTrack track, HashSet<EquiPointSet> sleeperSets)
        {
            List<TrackOp> ops = TrackHistory.Get(track);
            if (ops == null || ops.Count == 0) return;

            var kept = new List<TrackOp>();

            if (!DamageLedger.IsHealed(track, HealedThreshold01))
            {
                // 1. Verformungen, die eine spätere Reparatur komplett abdeckt, entfernen
                for (int i = 0; i < ops.Count; i++)
                {
                    TrackOp op = ops[i];
                    if (!op.IsRepair && CoveredByLaterRepair(ops, i)) continue;
                    kept.Add(op);
                }

                // 2. Führende Reparaturen entfernen
                while (kept.Count > 0 && kept[0].IsRepair)
                    kept.RemoveAt(0);
            }
            // 3. sonst: Gleis ist heil -> kept bleibt leer

            if (kept.Count == ops.Count) return; // nichts zu entfernen

            RebuildTrack(track, kept, sleeperSets);

            Main.Log(kept.Count == 0
                ? $"Track '{track.name}' fully repaired, history removed ({ops.Count} operation(s))"
                : $"History of '{track.name}' compacted: {ops.Count} -> {kept.Count} operation(s)");
        }

        private static bool CoveredByLaterRepair(List<TrackOp> ops, int index)
        {
            TrackOp d = ops[index];
            double from = d.Center - d.Radius;
            double to = d.Center + d.Radius;

            for (int j = index + 1; j < ops.Count; j++)
            {
                TrackOp r = ops[j];
                if (r.IsRepair && from >= r.Start && to <= r.End) return true;
            }
            return false;
        }

        // Gleis auf Original zurücksetzen und den neuen Verlauf abspielen (Fahrpfad, Verzeichnis, Schwellen)
        private static void RebuildTrack(RailTrack track, List<TrackOp> newOps, HashSet<EquiPointSet> sleeperSets)
        {
            EquiPointSet trackSet = track.GetKinkedPointSet();
            if (trackSet == null || trackSet.points == null) return;

            TrackSnapshots.RestoreTrack(track, trackSet, WholeFrom, WholeTo, 0f);
            DamageLedger.RemoveTrack(track);

            foreach (TrackOp op in newOps)
                ApplyOpToTrack(track, trackSet, op);

            TrackHistory.Replace(track, newOps);

            // Geladene Schwellen sofort neu aufbauen, alle anderen beim nächsten Nachziehen
            if (sleeperSets != null)
                foreach (var s in sleeperSets)
                    SyncSleeper(track, s);
        }

        // =========================
        // MULTIPLAYER: VERLAUF VOM HOST ÜBERNEHMEN
        // =========================
        // Jedes Gleis wird auf den Originalzustand gesetzt und mit dem Verlauf des Hosts neu aufgebaut
        // (gleiche Seeds -> exakt gleiche Geometrie). fullSnapshot = true: Gleise, die der Host nicht
        // mitschickt, sind beim Host unbeschädigt und werden zurückgesetzt.
        public static void ApplyRemoteHistories(List<(RailTrack track, List<TrackOp> ops)> items, bool fullSnapshot)
        {
            if (items == null) return;

            if (fullSnapshot)
            {
                var incoming = new HashSet<RailTrack>();
                foreach (var it in items) if (it.track != null) incoming.Add(it.track);
                foreach (var kv in TrackHistory.All())
                    if (!incoming.Contains(kv.Key)) items.Add((kv.Key, new List<TrackOp>()));
            }
            if (items.Count == 0) return;

            var gen = GetMeshGenerator();
            var activeChunks = GetActiveChunks(gen);

            // Erst alle Schwellen einsammeln, dann ändern, Meshes am Ende einmal neu bauen
            var sleepers = new Dictionary<RailTrack, HashSet<EquiPointSet>>();
            foreach (var it in items)
                if (it.track != null && !sleepers.ContainsKey(it.track))
                    sleepers[it.track] = CollectSleeperSets(activeChunks, it.track);

            var changed = new HashSet<RailTrack>();
            foreach (var it in items)
            {
                RailTrack track = it.track;
                if (track == null || JunctionTracks.Is(track)) continue;

                EquiPointSet trackSet = track.GetKinkedPointSet();
                if (trackSet == null || trackSet.points == null) continue;

                // Nicht verformte Gleise ohne neuen Schaden: nichts zu tun
                if (!TrackHistory.Has(track) && (it.ops == null || it.ops.Count == 0)) continue;

                TrackSnapshots.EnsureTrack(track, trackSet);   // erstes Mal: aktueller Zustand = Original
                RebuildTrack(track, it.ops != null ? new List<TrackOp>(it.ops) : new List<TrackOp>(), sleepers[track]);
                track.TrackPointsUpdated_Invoke();
                changed.Add(track);
            }

            // Vom Host übernommene Änderungen nicht erneut verschicken
            TrackHistory.ClearDirty();

            if (changed.Count > 0 && gen != null && activeChunks != null)
                RefreshMeshes(gen, activeChunks, changed);
        }

        // Nach dem Nachspielen: bereits gebaute Meshes der Gleise neu erzeugen lassen
        public static void RefreshAfterLoad(HashSet<RailTrack> tracks)
        {
            var gen = GetMeshGenerator();
            var activeChunks = GetActiveChunks(gen);
            if (gen == null || activeChunks == null) return; // Meshes entstehen später ohnehin aus den neuen Punkten
            RefreshMeshes(gen, activeChunks, tracks);
        }

        // Verschiebt Punkte um centerSpan +- radius nach der Formel aus RailTrack.KinkPointSet
        // (gleiche Perlin-Zeilen 234.34 / 145.54 / 4761, Offset (Perlin - 0.5) * Scale),
        // plus Roll um die Fahrtrichtung. Volle Stärke im Plateau, sanftes Ausblenden
        // (Cosinus) über die letzten 'fade' Meter des Radius.
        private static void ApplyOffsets(EquiPointSet set, double centerSpan, float radius, KinkParams kink)
        {
            int last = set.points.Length - EndBuffer;
            for (int i = EndBuffer; i < last; i++)
            {
                double span = set.points[i].span;
                double dSigned = span - centerSpan;
                float d = (float)Math.Abs(dSigned);
                if (d > radius) continue;

                float w = Window(d, radius, kink.fade);

                // Noise relativ zum Zentrum (kleine Zahlen -> gute Float-Genauigkeit), Seed verschiebt das Muster
                float x = (float)dSigned * kink.frequency + kink.seed;
                float nSide = (-0.5f + Mathf.PerlinNoise(x, 234.34f)) * kink.horizontal;
                float nVert = (-0.5f + Mathf.PerlinNoise(145.54f, x)) * kink.vertical;
                float nRoll = (-0.5f + Mathf.PerlinNoise(4761f, x)) * kink.roll;

                Vector3 forward = set.points[i].forward.normalized;
                Vector3 right = Vector3.Cross(forward, set.points[i].up.normalized);

                Vector3 offset = (right * nSide + Vector3.up * nVert) * w;
                set.points[i].position = set.points[i].position + new Vector3d(offset);

                // Roll (Schienenneigung) um die Fahrtrichtung, in Grad
                set.points[i].up = Quaternion.AngleAxis(nRoll * w, forward) * set.points[i].up;
            }

            set.RecalculateSpans();
        }

        // Gewicht 0..1 über den Abstand d vom Zentrum: volle Stärke im Plateau,
        // Cosinus-Ausblendung über die letzten 'fade' Meter des Radius.
        public static float Window(float d, float radius, float fade)
        {
            float plateau = radius - fade;
            if (d <= plateau || fade <= 0.0001f) return 1f;
            return 0.5f * (1f + Mathf.Cos(((d - plateau) / fade) * Mathf.PI));
        }

        // Stellt den Originalzustand des Abschnitts [startSpan, endSpan] wieder her.
        // Über 'repairBlend' Meter wird sanft in die Nachbarabschnitte übergeblendet,
        // damit keine Stufe entsteht (deren Schaden im Verzeichnis sinkt entsprechend mit).
        public static bool RepairSection(RailTrack track, double startSpan, double endSpan)
        {
            EquiPointSet trackSet = track != null ? track.GetKinkedPointSet() : null;
            if (trackSet == null || trackSet.points == null) return false;

            float blend = Main.Settings.RepairBlend;

            var gen = GetMeshGenerator();
            var activeChunks = GetActiveChunks(gen);
            var sleeperSets = CollectSleeperSets(activeChunks, track);

            if (!TrackSnapshots.RestoreTrack(track, trackSet, startSpan, endSpan, blend))
            {
                Main.Warn($"No original state stored for '{track.name}', nothing to restore");
                return false;
            }

            TrackHistory.Add(track, TrackOp.Repair(startSpan, endSpan, blend));
            foreach (var s in sleeperSets)
                SyncSleeper(track, s);

            DamageLedger.Clear(track, startSpan, endSpan, blend);

            // Verlauf aufräumen (entfernt aufgehobene Verformungen bzw. den ganzen Verlauf bei heilem Gleis)
            CompactHistory(track, sleeperSets);

            track.TrackPointsUpdated_Invoke();
            RefreshMeshes(gen, activeChunks, new HashSet<RailTrack> { track });

            Main.Log($"Repaired '{track.name}' from {startSpan:F1} to {endSpan:F1} m");
            return true;
        }

        private static RailwayMeshGenerator GetMeshGenerator()
        {
            if (meshGen == null)
                meshGen = UnityEngine.Object.FindObjectOfType<RailwayMeshGenerator>();
            return meshGen;
        }

        private static Dictionary<Vector2Int, List<TrackChunk>> GetActiveChunks(RailwayMeshGenerator gen)
        {
            if (gen == null) return null;
            return Traverse.Create(gen).Field("activeChunks")
                .GetValue<Dictionary<Vector2Int, List<TrackChunk>>>();
        }

        // Den Zellen, in denen eines der Gleise liegt, die aktiven Chunks wegnehmen und die
        // Zell-ID zurücksetzen. Beim nächsten Update() baut der Generator diese Chunks
        // (Schienen, Schotter) und die Schwellen aus den geänderten Punktesätzen neu.
        private static void RefreshMeshes(RailwayMeshGenerator gen,
                                          Dictionary<Vector2Int, List<TrackChunk>> activeChunks,
                                          HashSet<RailTrack> tracks)
        {
            if (tracks == null || tracks.Count == 0) return;

            if (gen == null || activeChunks == null)
            {
                Main.Warn("RailwayMeshGenerator not found, mesh not refreshed");
                return;
            }

            foreach (var list in activeChunks.Values)
            {
                if (list.Count == 0) continue;
                if (!list.Any(c => tracks.Contains(c.track))) continue;

                foreach (var chunk in list)
                    chunk.ReleasePoolObjects();
                list.Clear();
            }

            Traverse.Create(gen).Field("prevCellId")
                .SetValue(new Vector2Int(int.MinValue, int.MaxValue));
        }
    }

    // =========================
    // SETTINGS
    // =========================

    // Die Einstellungsseite wird selbst gezeichnet (statt [Draw]-Attributen),
    // damit alle Beschriftungen übersetzt und abhängige Bereiche ausgeblendet werden können.
    //
    // Aufbau:
    //   [x] Track deformations                      (Hauptschalter)
    //       [x] Deform at derailment     (Dad)
    //       [x] Deform when dragged      (Dwd)
    //       [x] Deform by explosion      (Dbe)
    //       [x] Measuring runs
    //     Verformung      (Dad || Dwd || Dbe): Radius, Basisschaden, vertikal, Frequenz, Neigung,
    //                     [Dwd: Intervall, Mindestgeschwindigkeit], Max
    //     Multiplikatoren (Dad || Dwd):        Gewicht, Geschwindigkeit, Lok-Bonus
    //     Explosionen     (Dbe):               Radius, Multiplikator
    //     Gleisreparatur  (Dad || Dwd || Dbe): Modus (Bezahlen/Verdienen ohne DVCustomLicenses,
    //                     nur Lizenz mit DVCustomLicenses), Radius, Abschnittslänge,
    //                     Kosten (Bezahlen, Lizenz), Vergütung (Verdienen, Lizenz),
    //                     Lizenz: Preis + Copay der Lizenzen "Gleisinstandhaltung" und "Gleisbau-Unternehmer"
    //     Messfahrt       (Measuring runs):    Toleranz
    //   Debug: [ ] Print debug logs
    //
    // Ist ein Schalter aus, gelten alle davon abhängigen Funktionen als aus (die gespeicherten
    // Werte der Unterschalter bleiben erhalten und greifen wieder, wenn er eingeschaltet wird).
    [Serializable]
    public class Settings : UnityModManager.ModSettings
    {
        // ---- Schalter ----
        public bool deformEnabled = true;       // Hauptschalter "Track deformations"
        public bool deformAtDerail = true;      // Dad
        public bool deformWhenDragged = true;   // Dwd
        public bool deformByExplosion = true;   // Dbe
        public bool measureRunEnabled = true;   // Messfahrten
        public bool printDebugLogs = false;

        // ---- Verformung ----
        public float radius = 5f;
        public float baseDamage = 0.2f;
        public float verticalRatio = 0.1f;
        public float frequency = 0.1f;
        public float rollPerMeter = 25f;
        public float maxTotalDamage = 2f;

        // ---- Mitschleifen (Dwd) ----
        public float intervalSeconds = 1f;
        public float minSpeedKmh = 3f;

        // ---- Multiplikatoren ----
        public float multiplierPerTon = 0.01f;
        public float multiplierPerKmh = 0.01f;
        public float locoBonus = 1f;

        // ---- Explosionen ----
        public float explosionRadius = 25f;
        public float explosionMultiplier = 5f;

        // ---- Reparatur ----
        public float repairRadius = 25f;
        public float repairSectionLength = 10f;
        public float maxRepairCost = 10000f;
        public RepairMode repairMode = RepairMode.Penalty;
        public float maxRepairReward = 1000f;
        public float licensePrice = 50000f;           // Lizenz "Gleisinstandhaltung" (zahlen, Versicherung)
        public float licenseCopay = 25000f;           //   Erhöhung des Eigenanteils
        public float license2Price = 100000f;         // Lizenz "Gleisbau-Unternehmer" (Vergütung)
        public float license2Copay = 50000f;          //   Erhöhung des Eigenanteils

        // ---- Messfahrt ----
        public float vmaxToleranceKmh = 5f;

        // ---- Feste Werte (bewusst nicht in den Einstellungen) ----
        public float EdgeFade => 4f;             // Ausblenden am Rand der Verformung (m)
        public float FullDamage => 1f;           // Schaden, der 100 % entspricht (m)
        public float RepairBlend => 1f;          // Übergang der Reparatur ins Nachbargleis (m)

        // ---- Wirksame Schalter (berücksichtigen den Hauptschalter) ----
        public bool DeformAtDerail => deformEnabled && deformAtDerail;
        public bool DeformWhenDragged => deformEnabled && deformWhenDragged;
        public bool DeformByExplosion => deformEnabled && deformByExplosion;
        public bool MeasureRun => deformEnabled && measureRunEnabled;
        public bool AnyDeform => DeformAtDerail || DeformWhenDragged || DeformByExplosion;
        public bool TrackRepair => AnyDeform;

        // Sicherheitsnetz, falls ein gespeicherter Wert außerhalb des Bereichs liegt
        public float Radius => Mathf.Clamp(radius, 5f, 20f);
        public float IntervalSeconds => Mathf.Clamp(intervalSeconds, 1f, 10f);
        public float MinSpeedKmh => Mathf.Max(0f, minSpeedKmh);
        public float BaseDamage => Mathf.Clamp(baseDamage, 0.1f, 0.5f);
        public float VerticalRatio => Mathf.Clamp01(verticalRatio);
        public float Frequency => Mathf.Clamp(frequency, 0.02f, 0.5f);
        public float RollPerMeter => Mathf.Clamp(rollPerMeter, 0f, 100f);
        public float RepairRadius => Mathf.Clamp(repairRadius, 5f, 100f);
        public float RepairSectionLength => Mathf.Clamp(repairSectionLength, 5f, 50f);
        public float ExplosionRadius => Mathf.Clamp(explosionRadius, 10f, 25f);
        public float ExplosionMultiplier => Mathf.Clamp(explosionMultiplier, 2f, 10f);
        public float VmaxTolerance => Mathf.Clamp(vmaxToleranceKmh, 0f, 20f);

        // Explosionsschaden = Basisschaden * Explosions-Multiplikator, begrenzt auf maxTotalDamage
        public float CalculateExplosionDamage()
        {
            return Mathf.Min(BaseDamage * ExplosionMultiplier, Mathf.Max(0f, maxTotalDamage));
        }

        // Multiplikator = Gewicht + Geschwindigkeit (+ Lok-Bonus)
        // Beispiel (Standardwerte): 100 t, 100 km/h -> 1.0 + 1.0 = x2
        //                            als Lok        -> 1.0 + 1.0 + 1 = x3
        // Schaden = Basisschaden * Multiplikator, begrenzt auf maxTotalDamage
        public float CalculateDamage(float massKg, float speedKmh, bool isLoco, out float multiplier)
        {
            multiplier = (massKg / 1000f) * multiplierPerTon
                         + speedKmh * multiplierPerKmh
                         + (isLoco ? locoBonus : 0f);
            multiplier = Mathf.Max(0f, multiplier);

            float damage = BaseDamage * multiplier;
            return Mathf.Min(damage, Mathf.Max(0f, maxTotalDamage));
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            if (TM_Multiplayer.IsClient) return;   // Client: Host-Einstellungen nie speichern
            Save(this, modEntry);
        }

        // Kopie aller Einstellungen (Multiplayer: Host-Werte vorübergehend verwenden)
        public Settings CloneConfiguration()
        {
            var copy = (Settings)MemberwiseClone();
            copy.boldStyle = null;
            copy.fieldBuffers = null;
            return copy;
        }

        // =========================
        // EINSTELLUNGSSEITE
        // =========================

        private const float LabelWidth = 460f;
        private const float Indent = 24f;

        [NonSerialized] private GUIStyle boldStyle;
        [NonSerialized] private Dictionary<string, string> fieldBuffers;

        public void Draw(UnityModManager.ModEntry entry)
        {
            if (boldStyle == null) boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            if (fieldBuffers == null) fieldBuffers = new Dictionary<string, string>();

            // Hauptschalter und Unterschalter
            deformEnabled = GUILayout.Toggle(deformEnabled, " " + Loc.T("set.deform"));
            if (deformEnabled)
            {
                deformAtDerail = Toggle("set.deformAtDerail", deformAtDerail, 1);
                deformWhenDragged = Toggle("set.deformWhenDragged", deformWhenDragged, 1);
                deformByExplosion = Toggle("set.deformByExplosion", deformByExplosion, 1);
                measureRunEnabled = Toggle("set.measureRun", measureRunEnabled, 1);

                if (AnyDeform)
                {
                    Header("set.sec.damage");
                    radius = Slider("set.radius", radius, 5f, 20f, 0);
                    baseDamage = Slider("set.baseDamage", baseDamage, 0.1f, 0.5f, 2);
                    verticalRatio = Slider("set.verticalRatio", verticalRatio, 0f, 1f, 2);
                    frequency = Slider("set.frequency", frequency, 0.02f, 0.5f, 2);
                    rollPerMeter = Slider("set.roll", rollPerMeter, 0f, 100f, 1);
                    if (DeformWhenDragged)
                    {
                        intervalSeconds = Slider("set.interval", intervalSeconds, 1f, 10f, 1);
                        minSpeedKmh = Field("set.minSpeed", minSpeedKmh);
                    }
                    maxTotalDamage = Field("set.maxTotal", maxTotalDamage);
                }

                if (DeformAtDerail || DeformWhenDragged)
                {
                    Header("set.sec.multipliers");
                    multiplierPerTon = Field("set.perTon", multiplierPerTon);
                    multiplierPerKmh = Field("set.perKmh", multiplierPerKmh);
                    locoBonus = Field("set.locoBonus", locoBonus);
                }

                if (DeformByExplosion)
                {
                    Header("set.sec.explosion");
                    explosionRadius = Slider("set.explosionRadius", explosionRadius, 10f, 25f, 0);
                    explosionMultiplier = Slider("set.explosionMultiplier", explosionMultiplier, 2f, 10f, 1);
                }

                if (TrackRepair)
                {
                    Header("set.sec.repair");
                    DrawRepairMode();
                    repairRadius = Slider("set.repairRadius", repairRadius, 5f, 100f, 0);
                    repairSectionLength = Slider("set.sectionLength", repairSectionLength, 5f, 50f, 0);

                    RepairMode mode = RepairEconomy.Mode;
                    if (mode == RepairMode.Penalty || mode == RepairMode.License)
                        maxRepairCost = Field("set.maxCost", maxRepairCost);
                    if (mode == RepairMode.Reward || mode == RepairMode.License)
                        maxRepairReward = Field("set.maxReward", maxRepairReward);
                    if (mode == RepairMode.License)
                    {
                        licensePrice = Field("set.licensePrice", licensePrice);
                        licenseCopay = Field("set.licenseCopay", licenseCopay);
                        license2Price = Field("set.license2Price", license2Price);
                        license2Copay = Field("set.license2Copay", license2Copay);
                        Note("set.licenseNote");
                    }
                }

                if (MeasureRun)
                {
                    Header("set.sec.vmax");
                    vmaxToleranceKmh = Slider("set.vmaxTolerance", vmaxToleranceKmh, 0f, 20f, 0);
                }
            }

            Header("set.sec.debug");
            printDebugLogs = Toggle("set.debugLogs", printDebugLogs, 0);
        }

        // Modus-Auswahl wie bei JunctionMaintenance: Bezahlen / Verdienen / Lizenz
        private void DrawRepairMode()
        {
            bool licenseMod = RepairEconomy.HasCustomLicensesMod;
            RepairMode mode = RepairEconomy.Mode;

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("set.mode"), GUILayout.Width(LabelWidth));
            // Mit DVCustomLicenses nur Lizenz wählbar, ohne die Mod nur Bezahlen/Verdienen
            if (ModeButton("set.mode.penalty", mode == RepairMode.Penalty, !licenseMod)) repairMode = RepairMode.Penalty;
            if (ModeButton("set.mode.reward", mode == RepairMode.Reward, !licenseMod)) repairMode = RepairMode.Reward;
            if (ModeButton("set.mode.license", mode == RepairMode.License, licenseMod)) repairMode = RepairMode.License;
            GUILayout.EndHorizontal();

            Note(licenseMod ? "set.mode.licenseForced" : "set.mode.needsLicenseMod");

            string info = mode == RepairMode.Reward ? "set.mode.info.reward"
                        : mode == RepairMode.License ? "set.mode.info.license"
                        : "set.mode.info.penalty";
            var box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = true };
            GUILayout.Box(Loc.T(info), box, GUILayout.ExpandWidth(true));
        }

        private static bool ModeButton(string key, bool active, bool enabled)
        {
            var style = new GUIStyle(GUI.skin.button);
            if (active)
            {
                style.normal.textColor = Color.green;
                style.fontStyle = FontStyle.Bold;
            }
            bool prev = GUI.enabled;
            GUI.enabled = enabled;
            bool clicked = GUILayout.Button(Loc.T(key), style, GUILayout.Width(150f));
            GUI.enabled = prev;
            return clicked && enabled;
        }

        private static void Note(string key)
        {
            var gray = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, wordWrap = true };
            gray.normal.textColor = Color.gray;
            GUILayout.Label(Loc.T(key), gray);
        }

        private void Header(string key)
        {
            GUILayout.Space(10f);
            GUILayout.Label(Loc.T(key), boldStyle);
        }

        private bool Toggle(string key, bool value, int indentLevel)
        {
            GUILayout.BeginHorizontal();
            if (indentLevel > 0) GUILayout.Space(Indent * indentLevel);
            value = GUILayout.Toggle(value, " " + Loc.T(key));
            GUILayout.EndHorizontal();
            return value;
        }

        private float Slider(string key, float value, float min, float max, int precision)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(key), GUILayout.Width(LabelWidth));
            float v = GUILayout.HorizontalSlider(Mathf.Clamp(value, min, max), min, max, GUILayout.Width(200f));
            v = (float)Math.Round(v, precision);
            GUILayout.Label(" " + v.ToString("F" + precision, Loc.Culture), GUILayout.Width(80f));
            GUILayout.EndHorizontal();
            return v;
        }

        // Zahlenfeld; Komma und Punkt werden als Dezimaltrenner akzeptiert
        private float Field(string key, float value)
        {
            string buffer;
            if (!fieldBuffers.TryGetValue(key, out buffer))
                buffer = value.ToString(CultureInfo.InvariantCulture);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(key), GUILayout.Width(LabelWidth));
            buffer = GUILayout.TextField(buffer, GUILayout.Width(120f));
            GUILayout.EndHorizontal();

            fieldBuffers[key] = buffer;

            float parsed;
            if (float.TryParse(buffer.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                value = parsed;
            return value;
        }
    }
}