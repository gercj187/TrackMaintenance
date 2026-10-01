// File: TrackPersistence.cs
// Namespace: TrackMaintenance
// Speichert die Gleisschäden im Spielstand und stellt sie beim Laden wieder her.
//
// Gespeichert werden nicht die Punkte selbst, sondern der Verlauf aller Eingriffe pro Gleis
// (TrackHistory): jede Verformung mit Zentrum, Radius und allen Kink-Parametern inkl. Seed,
// jede Reparatur mit Abschnitt und Übergang. Beim Laden wird dieser Verlauf auf die frischen,
// unveränderten Gleise nachgespielt; das Ergebnis ist identisch zum Zustand beim Speichern.
// Das Schadensverzeichnis (Prozent, Vmax, Kosten) entsteht dabei automatisch mit.
//
// Format im Spielstand (Schlüssel "TrackMaintenance_TrackDamage"):
// {
//   "version": 1,
//   "tracksHash": "<Kartenversion>",
//   "tracks": [
//     { "i": <Index im Gleisregister>, "n": "<Gleisname>",
//       "ops": [ [0, center, radius, h, v, roll, freq, seed, fade],   // Verformung
//                [1, start, end, blend] ] }                           // Reparatur
//   ]
// }

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using DV.Utils;

namespace TrackMaintenance
{
    public static class TrackPersistence
    {
        public const string SaveKey = "TrackMaintenance_TrackDamage";
        private const int FormatVersion = 1;

        private const int OpDeform = 0;
        private const int OpRepair = 1;

        private static SaveGameData capturedSave;   // zuletzt geladener Spielstand
        private static JObject pending;             // noch nicht angewendete Daten

        private static FieldInfo loadingFinishedField;
        private static bool loadingFinishedSearched;

        // =========================
        // SPEICHERN
        // =========================

        public static void WriteTo(SaveGameData data)
        {
            if (data == null) return;

            // Multiplayer-Client: der Host speichert, der Client nie
            if (TM_Multiplayer.IsClient) return;

            // Noch nicht angewendete Daten eines gerade geladenen Spielstands nicht verlieren
            if (pending != null && !TrackHistory.Any)
            {
                data.SetJObject(SaveKey, pending);
                return;
            }

            var entries = TrackHistory.All();
            if (entries.Count == 0)
            {
                data.RemoveData(SaveKey);
                return;
            }

            List<RailTrack> register = RegisterList();
            var index = new Dictionary<RailTrack, int>();
            for (int i = 0; i < register.Count; i++)
                if (register[i] != null && !index.ContainsKey(register[i])) index[register[i]] = i;

            var tracks = new JArray();
            int opCount = 0;

            foreach (var kv in entries)
            {
                // Weichengleise nie speichern
                if (JunctionTracks.Is(kv.Key)) continue;

                var ops = new JArray();
                foreach (TrackOp op in kv.Value)
                {
                    if (op.IsRepair)
                        ops.Add(new JArray(OpRepair, op.Start, op.End, (double)op.Blend));
                    else
                        ops.Add(new JArray(OpDeform, op.Center, (double)op.Radius,
                            (double)op.Kink.horizontal, (double)op.Kink.vertical, (double)op.Kink.roll,
                            (double)op.Kink.frequency, (double)op.Kink.seed, (double)op.Kink.fade));
                    opCount++;
                }

                int i;
                tracks.Add(new JObject
                {
                    ["i"] = index.TryGetValue(kv.Key, out i) ? i : -1,
                    ["n"] = kv.Key.name,
                    ["ops"] = ops
                });
            }

            if (tracks.Count == 0)
            {
                data.RemoveData(SaveKey);
                return;
            }

            var root = new JObject
            {
                ["version"] = FormatVersion,
                ["tracksHash"] = TracksHash(),
                ["tracks"] = tracks
            };

            data.SetJObject(SaveKey, root);
            Main.Log($"Track damage saved: {tracks.Count} track(s), {opCount} operation(s)");
        }

        // =========================
        // LADEN
        // =========================

        // Beim Laden eines Spielstands: alten Zustand verwerfen, gespeicherte Daten vormerken
        public static void Capture(SaveGameData data)
        {
            if (data == null || ReferenceEquals(data, capturedSave)) return;
            capturedSave = data;

            LiveDeform.ResetState();

            // Multiplayer-Client: den Schadensstand liefert der Host, nicht der Spielstand
            if (TM_Multiplayer.IsClient)
            {
                pending = null;
                return;
            }

            pending = data.GetJObject(SaveKey);

            if (pending != null)
                Main.Log("Track damage found in savegame, waiting for tracks");
        }

        // Früher Zeitpunkt: kurz bevor die Wagen gesetzt werden (Gleise existieren dann)
        public static void TryApplyPendingEarly()
        {
            if (pending != null && !TM_Multiplayer.IsClient) TryApply();
        }

        // Rückfall aus Main.OnUpdate, falls der frühe Zeitpunkt nicht kam (z. B. keine Wagen im Spielstand)
        public static void TryApplyPendingLate()
        {
            if (pending == null || TM_Multiplayer.IsClient || !LoadingFinished()) return;
            TryApply();
        }

        private static void TryApply()
        {
            List<RailTrack> register = RegisterList();
            if (register.Count == 0) return; // Gleise noch nicht da, später erneut

            JObject root = pending;
            pending = null;

            try
            {
                string savedHash = (string)root["tracksHash"];
                string currentHash = TracksHash();
                if (!string.IsNullOrEmpty(savedHash) && !string.IsNullOrEmpty(currentHash) && savedHash != currentHash)
                {
                    Main.Warn($"Track damage not restored: map changed (saved {savedHash}, now {currentHash})");
                    return;
                }

                var byName = new Dictionary<string, List<RailTrack>>();
                foreach (RailTrack t in register)
                {
                    if (t == null) continue;
                    List<RailTrack> l;
                    if (!byName.TryGetValue(t.name, out l)) byName[t.name] = l = new List<RailTrack>();
                    l.Add(t);
                }

                var restored = new HashSet<RailTrack>();
                int opCount = 0, missing = 0, junctions = 0;

                foreach (JObject entry in (root["tracks"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    RailTrack track = FindTrack(entry, register, byName);
                    if (track == null)
                    {
                        // Weichengleise aus älteren Spielständen sind nicht eindeutig benannt und werden ohnehin ignoriert
                        if (IsJunctionName((string)entry["n"])) junctions++;
                        else missing++;
                        continue;
                    }

                    if (JunctionTracks.Is(track))
                    {
                        junctions++;
                        continue;
                    }

                    List<TrackOp> ops = ParseOps(entry["ops"] as JArray);
                    if (ops.Count == 0) continue;

                    if (LiveDeform.ReplayHistory(track, ops))
                    {
                        restored.Add(track);
                        opCount += ops.Count;
                    }
                }

                LiveDeform.RefreshAfterLoad(restored);

                Main.Log($"Track damage restored: {restored.Count} track(s), {opCount} operation(s)"
                                         + (missing > 0 ? $", {missing} track(s) not found" : "")
                                         + (junctions > 0 ? $", {junctions} switch track(s) ignored" : ""));
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Restoring track damage failed: " + e);
            }
        }

        // Gleis über Index im Register finden (Name muss passen), sonst über eindeutigen Namen
        private static RailTrack FindTrack(JObject entry, List<RailTrack> register,
                                           Dictionary<string, List<RailTrack>> byName)
        {
            string name = (string)entry["n"] ?? string.Empty;
            int i = entry["i"] != null ? (int)entry["i"] : -1;

            if (i >= 0 && i < register.Count && register[i] != null && register[i].name == name)
                return register[i];

            List<RailTrack> candidates;
            if (byName.TryGetValue(name, out candidates) && candidates.Count == 1)
                return candidates[0];

            return null;
        }

        private static List<TrackOp> ParseOps(JArray arr)
        {
            var list = new List<TrackOp>();
            if (arr == null) return list;

            foreach (JArray o in arr.OfType<JArray>())
            {
                if (o.Count < 1) continue;
                int type = (int)o[0];

                if (type == OpRepair && o.Count >= 4)
                {
                    list.Add(TrackOp.Repair((double)o[1], (double)o[2], (float)(double)o[3]));
                }
                else if (type == OpDeform && o.Count >= 9)
                {
                    var kink = new KinkParams
                    {
                        horizontal = (float)(double)o[3],
                        vertical = (float)(double)o[4],
                        roll = (float)(double)o[5],
                        frequency = (float)(double)o[6],
                        seed = (float)(double)o[7],
                        fade = (float)(double)o[8]
                    };
                    list.Add(TrackOp.Deformation((double)o[1], (float)(double)o[2], kink));
                }
            }
            return list;
        }

        // =========================
        // HILFEN
        // =========================

        private static bool IsJunctionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("[track through]", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("[track diverging]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<RailTrack> RegisterList()
        {
            var list = new List<RailTrack>();
            var tracks = RailTrackRegistryBase.RailTracks;
            if (tracks == null) return list;
            foreach (RailTrack t in tracks) list.Add(t);
            return list;
        }

        private static string TracksHash()
        {
            try
            {
                var reg = SingletonBehaviour<RailTrackRegistryBase>.Instance;
                return reg != null ? reg.TracksHash : null;
            }
            catch { return null; }
        }

        // AStartGameData.carsAndJobsLoadingFinished (per Reflection, Sichtbarkeit unbekannt)
        internal static bool LoadingFinished()
        {
            if (!loadingFinishedSearched)
            {
                loadingFinishedSearched = true;
                Type t = AccessTools.TypeByName("AStartGameData");
                if (t != null) loadingFinishedField = AccessTools.Field(t, "carsAndJobsLoadingFinished");
            }
            if (loadingFinishedField == null) return true;
            try { return (bool)loadingFinishedField.GetValue(null); }
            catch { return true; }
        }
    }

    // =========================
    // GLEIS-ZUORDNUNG (Index im Gleisregister + Name)
    // =========================
    // Für Multiplayer-Pakete: Host und Client haben dieselbe Karte, Gleise werden über ihren Index
    // im Register gefunden (mit Namensprüfung), sonst über einen eindeutigen Namen.
    internal static class TrackRefs
    {
        private static List<RailTrack> register;
        private static Dictionary<RailTrack, int> index;
        private static Dictionary<string, List<RailTrack>> byName;

        public static void Reset()
        {
            register = null;
            index = null;
            byName = null;
        }

        private static bool Ensure()
        {
            // Neu aufbauen, wenn noch leer oder die Gleise einer alten Szene zerstört sind
            if (register != null && register.Count > 0 && register[0] != null) return true;

            register = new List<RailTrack>();
            index = new Dictionary<RailTrack, int>();
            byName = new Dictionary<string, List<RailTrack>>();

            var tracks = RailTrackRegistryBase.RailTracks;
            if (tracks == null) return false;

            foreach (RailTrack t in tracks)
            {
                int i = register.Count;
                register.Add(t);
                if (t == null) continue;
                if (!index.ContainsKey(t)) index[t] = i;

                string name = t.name ?? string.Empty;
                List<RailTrack> l;
                if (!byName.TryGetValue(name, out l)) byName[name] = l = new List<RailTrack>();
                l.Add(t);
            }
            return register.Count > 0;
        }

        public static int IndexOf(RailTrack track)
        {
            if (track == null || !Ensure()) return -1;
            int i;
            return index.TryGetValue(track, out i) ? i : -1;
        }

        public static RailTrack Find(int i, string name)
        {
            if (!Ensure()) return null;
            name = name ?? string.Empty;

            if (i >= 0 && i < register.Count && register[i] != null && register[i].name == name)
                return register[i];

            List<RailTrack> candidates;
            if (byName.TryGetValue(name, out candidates) && candidates.Count == 1 && candidates[0] != null)
                return candidates[0];

            return null;
        }
    }

    // =========================
    // HARMONY-PATCHES
    // =========================

    // Laden: SaveGameManager.FindStartGameData übernimmt bei jedem Spielstart die Spielstand-Daten
    // (geladener Spielstand, Fortsetzen, neues Spiel) -> alten Zustand verwerfen, Daten vormerken
    [HarmonyPatch(typeof(SaveGameManager), nameof(SaveGameManager.FindStartGameData))]
    internal static class TQ_Save_Capture
    {
        static void Postfix(SaveGameManager __instance)
        {
            try { TrackPersistence.Capture(__instance.data); }
            catch (Exception e) { Main.ModEntry.Logger.Error("Capturing savegame failed: " + e); }
        }
    }

    // Laden: kurz bevor die Wagen gesetzt werden, Gleisschäden anwenden
    [HarmonyPatch]
    internal static class TQ_Save_ApplyBeforeCars
    {
        static MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("CarsSaveManager");
            return t != null ? AccessTools.Method(t, "Load", new[] { typeof(JObject) }) : null;
        }

        static bool Prepare()
        {
            bool ok = TargetMethod() != null;
            if (!ok) Main.Warn("CarsSaveManager.Load not found, track damage is applied after loading instead");
            return ok;
        }

        static void Prefix()
        {
            try { TrackPersistence.TryApplyPendingEarly(); }
            catch (Exception e) { Main.ModEntry.Logger.Error("Applying track damage failed: " + e); }
        }
    }

    // Speichern: DoSaveIO schreibt die Daten in die Datei (alle Speicherarten laufen hier durch:
    // manuell, Auto-Save, beim Pausieren, beim Beenden) -> vorher die Gleisschäden hineinlegen
    [HarmonyPatch(typeof(SaveGameManager), "DoSaveIO")]
    internal static class TQ_Save_Write
    {
        static void Prefix(SaveGameData data)
        {
            try { TrackPersistence.WriteTo(data); }
            catch (Exception e) { Main.ModEntry.Logger.Error("Saving track damage failed: " + e); }
        }
    }
}