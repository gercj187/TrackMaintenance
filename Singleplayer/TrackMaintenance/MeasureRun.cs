// File: MeasureRun.cs
// Namespace: TrackMaintenance
// "Messfahrt" im CareerManager der Caboose: zeigt groß die Vmax (strengste Vmax aller Wagen des
// Zugverbands, aus dem Schadensverzeichnis) und darunter die aktuelle Geschwindigkeit.
// Überschreitet V die Vmax um mehr als die eingestellte Toleranz, wird die Zugbremse voll angelegt
// (Hauptluftleitung wird entlüftet, wie ein Notbremsventil der Caboose). Die Zwangsbremsung löst sich
// automatisch, sobald der Zug steht (< 1 km/h).

using System;
using System.Text;
using HarmonyLib;
using UnityEngine;
using DV.ServicePenalty.UI;
using DV.Simulation.Brake;

namespace TrackMaintenance
{
    // Fahrschalter (Throttle) aller Loks auf 0 setzen, damit bei der Zwangsbremsung nicht gegen
    // die Bremse angefahren wird. Gleicher Weg wie im Spiel (BaseControlsOverrider.SetNeutralState):
    //   car.SimController.controlsOverrider.Throttle.Set(0f)
    internal static class ThrottleCut
    {
        public static void ZeroTrain(TrainCar car)
        {
            if (car == null) return;

            var set = car.trainset;
            if (set == null || set.cars == null)
            {
                Zero(car);
                return;
            }

            foreach (TrainCar c in set.cars)
                if (c != null) Zero(c);
        }

        private static void Zero(TrainCar car)
        {
            var sim = car.SimController;
            if (sim == null) return;                 // Wagen ohne Steuerung (Güterwagen, Caboose)

            var overrider = sim.controlsOverrider;
            if (overrider == null) return;

            var throttle = overrider.Throttle;
            if (throttle == null) return;

            // Nur setzen, wenn noch Leistung anliegt
            if (throttle.Value > 0.001f)
                throttle.Set(0f);
        }
    }

    internal static class MeasureRun
    {
        public static bool Active;
        public static bool Braking;
        public static Brakeset BrakingSet;      // Bremsverband, der gerade entlüftet wird
        public static CareerManagerInfoScreen Info;
        public static CareerManagerMainScreen MainScreen;
        public static TrainCar Car;

        private const float RenderInterval = 0.1f;
        private const string OVERSPEED_COLOR = "FF4040";

        // Schriftgrößen relativ zur Paragraph-Schrift des Info-Bildschirms
        private const string SIZE_LABEL = "100%";
        private const string SIZE_VMAX = "550%";
        private const string SIZE_V = "150%";
        private const int POS_STATUS = 60;    // "AKTIV" in der Titelzeile (% der Titelbreite)

        private static float nextRender;

        public static void Start(CareerManagerMainScreen main, CareerManagerInfoScreen info, TrainCar car)
        {
            TQ_ListState.Stop();

            MainScreen = main;
            Info = info;
            Car = car;
            Braking = false;
            BrakingSet = null;
            nextRender = 0f;
            Active = true;

            TQ_Helpers.SetInfoLines(info, main, Loc.T("measure.header"), new[] { string.Empty });
            main.screenSwitcher?.SetActiveDisplay(info);
            Render(VmaxOfTrain(car), CurrentSpeedKmh(car));
        }

        public static void Stop()
        {
            Active = false;
            Braking = false;
            BrakingSet = null;
            Info = null;
            MainScreen = null;
            Car = null;
        }

        // Abbrechen: zurück zum Hauptmenü (dessen Wechsel ruft Info.Disable -> Stop)
        public static void Exit()
        {
            var main = MainScreen;
            Stop();
            if (main != null && main.screenSwitcher != null)
                main.screenSwitcher.SetActiveDisplay(main);
        }

        // Jeden Frame aus Main.OnUpdate
        public static void Tick()
        {
            if (!Active) return;

            // Messfahrten in den Einstellungen abgeschaltet: beenden (löst auch die Bremse)
            if (!Main.Settings.MeasureRun)
            {
                Exit();
                return;
            }

            // Innenraum entladen oder Wagen weg
            if (Info == null || MainScreen == null || Car == null)
            {
                Stop();
                return;
            }

            float v = CurrentSpeedKmh(Car);
            int vShown = Mathf.RoundToInt(v);
            int vmax = VmaxOfTrain(Car);

            // Zwangsbremsung bei V > Vmax + Toleranz; gelöst wird erst im Stillstand (< 1 km/h)
            if (!Braking)
            {
                if (vmax != SpeedLimits.None && v > vmax + Main.Settings.VmaxTolerance)
                {
                    Braking = true;
                    Main.Log($"Measuring run: {vShown} km/h > Vmax {vmax} + {Main.Settings.VmaxTolerance:0} km/h, penalty brake");
                }
            }
            else if (v < 1f)
            {
                Braking = false;
            }

            BrakingSet = Braking && Car.brakeSystem != null ? Car.brakeSystem.brakeset : null;

            // Während der Zwangsbremsung Leistung aller Loks im Zugverband wegnehmen
            if (Braking)
                ThrottleCut.ZeroTrain(Car);

            if (Time.time >= nextRender)
            {
                nextRender = Time.time + RenderInterval;
                Render(vmax, v);
            }
        }

        private static float CurrentSpeedKmh(TrainCar car)
        {
            return car != null && car.rb != null ? car.rb.velocity.magnitude * 3.6f : 0f;
        }

        // Strengste Vmax aller Wagen im Zugverband
        private static int VmaxOfTrain(TrainCar car)
        {
            if (car == null) return SpeedLimits.None;

            var set = car.trainset;
            if (set == null || set.cars == null) return VmaxLookup.ForCar(car);

            int vmax = SpeedLimits.None;
            foreach (TrainCar c in set.cars)
                if (c != null) vmax = VmaxLookup.Stricter(vmax, VmaxLookup.ForCar(c));
            return vmax;
        }

        private static void Render(int vmax, float v)
        {
            if (Info == null) return;

            Color reg, hl;
            TQ_Helpers.GetMenuColors(MainScreen != null ? MainScreen.screenSwitcher : null, out reg, out hl);
            string hlHex = ColorUtility.ToHtmlStringRGB(hl);

            int vShown = Mathf.RoundToInt(v);
            bool overspeed = vmax != SpeedLimits.None && vShown > vmax;
            string vmaxText = vmax == SpeedLimits.None ? Loc.T("vmax.none") : vmax.ToString();
            string vColor = overspeed ? OVERSPEED_COLOR : hlHex;

            // Layout:
            //   Titel (Originalfeld, einzeilig):
            //      MESSFAHRT:          AKTIV
            //   Textbereich:
            //                 SM B-04-O                  (Gleis unter der Caboose)
            //               ZWANGSBREMSUNG               (rot blinkend, sonst leer)
            //                 V soll:
            //                   60                       (sehr groß)
            //                 V ist:
            //                27 km/h
            string title = Loc.T("measure.header") + "<pos=" + POS_STATUS + "%>" + Loc.T("measure.active");

            string brakeLine = Braking && Mathf.Repeat(Time.time, 1f) < 0.6f
                ? "<color=#" + OVERSPEED_COLOR + ">" + Loc.T("measure.braking") + "</color>"
                : " "; // Leerzeile hält das Layout stabil

            RailTrack track = Car != null && Car.FrontBogie != null ? Car.FrontBogie.track : null;
            string section = track != null ? TrackIds.Get(track) : Loc.T("vmax.none");

            var sb = new StringBuilder();
            sb.Append("<align=center>");
            sb.Append(section).Append('\n');
            sb.Append(brakeLine).Append('\n');
            sb.Append("<size=").Append(SIZE_LABEL).Append('>').Append(Loc.T("measure.vmax")).Append("</size>\n");
            sb.Append("<size=").Append(SIZE_VMAX).Append("><color=#").Append(hlHex).Append('>')
              .Append(vmaxText).Append("</color></size>\n");
            sb.Append("<size=").Append(SIZE_LABEL).Append('>').Append(Loc.T("measure.v")).Append("</size>\n");
            sb.Append("<size=").Append(SIZE_V).Append("><color=#").Append(vColor).Append('>')
              .Append(vShown).Append(' ').Append(Loc.T("measure.unit")).Append("</color></size>");

            try
            {
                if (Info.title != null) Info.title.text = title;
                if (Info.paragraph != null) Info.paragraph.text = sb.ToString();
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Measuring run render failed: " + e);
            }
        }
    }

    // Zugbremse voll anlegen: Hauptluftleitung vor und nach jedem Bremstick unter
    // (höchster Steuerbehälterdruck - 1,6 bar) entlüften. Differenz >= 1,5 bar = voller Bremszylinderdruck
    // bei allen Wagen. Vor UND nach dem Tick, damit das Führerbremsventil der Lok nicht nachspeist.
    [HarmonyPatch(typeof(Brakeset), "Tick")]
    internal static class TQ_Brakeset_MeasureBrake
    {
        private const float VentSpeed = 150f; // wie Führerbremsventil in Schnellbremsstellung

        static void Prefix(Brakeset __instance, float dt) { Vent(__instance, dt); }
        static void Postfix(Brakeset __instance, float dt) { Vent(__instance, dt); }

        private static void Vent(Brakeset set, float dt)
        {
            if (!MeasureRun.Braking || !ReferenceEquals(set, MeasureRun.BrakingSet) || dt <= 0f) return;

            try
            {
                float maxControl = 1f;
                foreach (var car in set.cars)
                    if (car != null) maxControl = Mathf.Max(maxControl, car.controlReservoirPressure);

                float target = Mathf.Max(1f, maxControl - 1.6f);
                if (set.pipePressure > target)
                    BrakeSystem.VentToAtmosphere(dt, ref set.pipePressure, set.pipeVolume, VentSpeed, target);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Measuring run brake failed: " + e);
                MeasureRun.Braking = false;
            }
        }
    }
}