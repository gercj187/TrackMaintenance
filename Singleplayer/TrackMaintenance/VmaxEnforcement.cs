// File: VmaxEnforcement.cs
// Namespace: TrackMaintenance
// Vmax-Ermittlung für Wagen auf beschädigten Abschnitten (genutzt von der Messfahrt).
// Es wird NICHT in die Entgleisungslogik des Spiels eingegriffen.

using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using DV.PointSet;

namespace TrackMaintenance
{
    internal static class VmaxLookup
    {
        private sealed class CacheEntry
        {
            public float NextUpdate;
            public int Vmax = SpeedLimits.None;
        }

        private const float RefreshSeconds = 0.25f;

        private static readonly ConditionalWeakTable<TrainCar, CacheEntry> cache =
            new ConditionalWeakTable<TrainCar, CacheEntry>();

        // Strengste Vmax unter beiden Drehgestellen (gecacht, 4x pro Sekunde neu berechnet)
        public static int ForCar(TrainCar car)
        {
            CacheEntry e = cache.GetOrCreateValue(car);
            float now = Time.time;
            if (now < e.NextUpdate) return e.Vmax;
            e.NextUpdate = now + RefreshSeconds;

            int vmax = SpeedLimits.None;
            vmax = Stricter(vmax, ForBogie(car.FrontBogie));
            vmax = Stricter(vmax, ForBogie(car.RearBogie));
            e.Vmax = vmax;
            return vmax;
        }

        public static int Stricter(int a, int b)
        {
            if (a == SpeedLimits.None) return b;
            if (b == SpeedLimits.None) return a;
            return Math.Min(a, b);
        }

        private static int ForBogie(Bogie bogie)
        {
            // Entgleiste Drehgestelle haben kein Gleis mehr (track == null)
            if (bogie == null || bogie.track == null) return SpeedLimits.None;
            return At(bogie.track, bogie.transform.position);
        }

        // Vmax an einer Position auf dem Gleis, im selben Abschnittsraster wie die Reparaturliste
        public static int At(RailTrack track, Vector3 position)
        {
            if (!DamageLedger.Has(track)) return SpeedLimits.None;

            EquiPointSet set = track.GetKinkedPointSet();
            if (set == null) return SpeedLimits.None;

            var (point, _) = RailTrack.GetClosestPoint(track, position);
            if (point == null) return SpeedLimits.None;

            double len = Main.Settings.RepairSectionLength;
            int k = Math.Max(0, (int)Math.Floor(point.Value.span / len));
            double a = k * len;
            double b = Math.Min(set.span, a + len);

            int percent = Mathf.RoundToInt(DamageLedger.Section01(track, a, b) * 100f);
            return SpeedLimits.ForPercent(percent);
        }
    }
}