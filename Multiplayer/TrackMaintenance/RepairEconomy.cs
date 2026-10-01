// File: RepairEconomy.cs
// Namespace: TrackMaintenance
// Reparatur-Modi (wie bei der Mod JunctionMaintenance) und Versicherung.
//
//   Penalty  = Spieler zahlt die Reparatur. Die Versicherung des Spiels greift (Selbstbeteiligung).
//   Reward   = Spieler erhält für jede Reparatur eine Vergütung.
//   License  = zwei Lizenzen (DVCustomLicenses):
//              ohne Lizenz:                         keine Reparatur möglich
//              "TrackMaintenance":                  wie Penalty (zahlen, Versicherung greift)
//              "TrackMaintenanceContractor":        wie Reward (Vergütung); setzt die erste Lizenz voraus
//              Preis und Eigenanteil-Erhöhung (Copay) beider Lizenzen sind einstellbar.
//
// Versicherung (Penalty und License mit erster Lizenz), wie im Spiel (CareerManagerDebtController):
//   - Quote (Selbstbeteiligung) = feeQuota.Quota, wächst mit Lizenzen, gedeckelt durch die Schwierigkeit.
//   - Spieleranteil einer Reparatur = min(Kosten, noch offener Teil der Quote); den Rest trägt die Versicherung.
//   - Nach der Zahlung wird der Spieleranteil auf die Quote angerechnet.
//   - Ist die Quote damit erreicht, wird die Versicherungsleistung ausgelöst wie beim Tilgen im CareerManager:
//     alle übrigen offenen Gebühren werden getilgt und der Zähler beginnt wieder bei 0.
//     So gibt es pro erreichter Quote genau einen Versicherungsfall, keine Serie von Gratisreparaturen.

using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using DV;
using DV.Utils;
using DV.ThingTypes;
using DV.ServicePenalty;
using DV.ServicePenalty.UI;
using DV.InventorySystem;

namespace TrackMaintenance
{
    public enum RepairMode
    {
        Penalty = 0,
        Reward = 1,
        License = 2
    }

    // Ergebnis der Preisberechnung für einen Abschnitt
    internal struct RepairQuote
    {
        public bool IsReward;      // true = Spieler bekommt Geld (Payout), sonst zahlt er (Pay)
        public double Payout;      // Vergütung (nur Reward)
        public double FullCost;    // voller Reparaturpreis
        public double Pay;         // Spieleranteil an der Kasse
        public double Covered;     // von der Versicherung übernommen
        public bool Insured;       // Versicherung ist an dieser Reparatur beteiligt (nur Penalty)
    }

    internal static class RepairEconomy
    {
        // ---------- Modus ----------

        // DVCustomLicenses ist installiert (nötig für den License-Modus)
        public static bool HasCustomLicensesMod
        {
            get
            {
                if (!licenseModChecked)
                {
                    licenseModChecked = true;
                    // Nur wenn die Mod installiert UND in UMM aktiviert ist
                    licenseModPresent = UnityModManager.modEntries != null && UnityModManager.modEntries.Any(m =>
                        m.Info != null && m.Enabled &&
                        string.Equals(m.Info.Id, "DVCustomLicenses", StringComparison.OrdinalIgnoreCase));
                }
                return licenseModPresent;
            }
        }
        private static bool licenseModChecked, licenseModPresent;

        // Multiplayer-Client: wirksamer Modus des Hosts (null = eigener Modus)
        public static RepairMode? HostMode;

        // Wirksamer Modus:
        //   DVCustomLicenses aktiv  -> immer License (Penalty/Reward nicht wählbar)
        //   DVCustomLicenses fehlt  -> Penalty oder Reward laut Einstellung (License nicht wählbar)
        public static RepairMode Mode
        {
            get
            {
                if (HostMode.HasValue) return HostMode.Value;
                if (HasCustomLicensesMod) return RepairMode.License;
                var m = Main.Settings.repairMode;
                return m == RepairMode.License ? RepairMode.Penalty : m;
            }
        }

        public static bool IsRewardNow
        {
            get
            {
                switch (Mode)
                {
                    case RepairMode.Reward: return true;
                    case RepairMode.License: return TrackLicenses.Contractor.Has;
                    default: return false;
                }
            }
        }

        // Versicherung beim Bezahlen: Penalty und License (erste Lizenz)
        public static bool InsuranceApplies
        {
            get { return Mode == RepairMode.Penalty || Mode == RepairMode.License; }
        }

        // Im Lizenzmodus darf nur mit Lizenz repariert werden
        public static bool CanRepair
        {
            get
            {
                if (Mode != RepairMode.License) return true;
                return TrackLicenses.Basic.Has || TrackLicenses.Contractor.Has;
            }
        }

        // ---------- Preise ----------

        public static double CostFor(int percent)
        {
            return Math.Round(Math.Max(0.0, Main.Settings.maxRepairCost) / 100.0 * percent, 2);
        }

        public static double PayoutFor(int percent)
        {
            return Math.Round(Math.Max(0.0, Main.Settings.maxRepairReward) / 100.0 * percent, 2);
        }

        public static RepairQuote Quote(int percent)
        {
            var q = new RepairQuote();

            if (IsRewardNow)
            {
                q.IsReward = true;
                q.Payout = PayoutFor(percent);
                return q;
            }

            q.FullCost = CostFor(percent);
            q.Pay = q.FullCost;

            InsuranceFeeQuota fq = InsuranceApplies && q.FullCost > 0.0 ? Quota() : null;
            if (fq != null)
            {
                double left = Math.Max(0.0, fq.LeftToReachQuota);
                q.Insured = true;
                q.Pay = Math.Round(Math.Min(q.FullCost, left), 2);
                q.Covered = Math.Round(q.FullCost - q.Pay, 2);
            }
            return q;
        }

        // ---------- Versicherung ----------

        // Gecacht: FindObjectOfType durchsucht die ganze Szene und lief vorher für jede Listenzeile bei
        // jedem Neuzeichnen (Ursache für das träge Scrollen in den Modi Penalty und License).
        private static CareerManagerDebtController controller;
        private static float nextControllerSearch;

        private static CareerManagerDebtController Controller()
        {
            if (controller != null) return controller;              // Unity-Null nach Szenenwechsel beachtet
            if (Time.realtimeSinceStartup < nextControllerSearch) return null;
            nextControllerSearch = Time.realtimeSinceStartup + 2f;

            // Nicht über SingletonBehaviour<>.Instance: das würde den Controller ggf. erst anlegen
            controller = UnityEngine.Object.FindObjectOfType<CareerManagerDebtController>();
            return controller;
        }

        // Quote der Versicherung, falls aktiv und sinnvoll (sonst null)
        private static InsuranceFeeQuota Quota()
        {
            try
            {
                var ctrl = Controller();
                if (ctrl == null || ctrl.feeQuota == null) return null;
                var fq = ctrl.feeQuota;
                if (!fq.InsuranceUsed || fq.Quota <= 0f) return null;
                return fq;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Reading insurance quota failed: " + e);
                return null;
            }
        }

        // Nach der Reparatur: Spieleranteil anrechnen; bei erreichter Quote Versicherungsleistung auslösen.
        // Rückgabe: true, wenn die Versicherungsleistung ausgelöst wurde.
        public static bool AfterPaidRepair(RepairQuote q)
        {
            if (!q.Insured) return false;

            try
            {
                var ctrl = Controller();
                if (ctrl == null || ctrl.feeQuota == null || !ctrl.feeQuota.InsuranceUsed) return false;

                if (q.Pay > 0.0)
                    ctrl.UpdateInsuranceFeePaidAmount((float)q.Pay);

                if (!ctrl.feeQuota.QuotaReached) return false;

                ctrl.RefreshExistingDebtsState();
                ctrl.ClearDebtsViaInsuranceQuotaReached();
                Main.Log($"Insurance quota reached by track repair: covered {q.Covered:F2}, other fees cleared, quota reset");
                return true;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Insurance handling failed: " + e);
                return false;
            }
        }

        // ---------- Vergütung ----------

        public static bool PrintMoney(double amount)
        {
            if (amount <= 0.0) return true;
            try
            {
                var printer = UnityEngine.Object.FindObjectOfType<MoneyPrinter>();
                if (printer == null)
                {
                    Main.ModEntry.Logger.Error("MoneyPrinter not found, payout not printed");
                    return false;
                }
                printer.PrintMoney(amount);
                return true;
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Printing payout failed: " + e);
                return false;
            }
        }
    }

    // =========================
    // LIZENZEN (DVCustomLicenses)
    // =========================

    internal sealed class TrackLicense
    {
        // Muss EXAKT dem "Identifier" in der jeweiligen license.json entsprechen
        public readonly string ID;

        private GeneralLicenseType_v2 cached;
        private float nextSearch;

        public TrackLicense(string id) { ID = id; }

        public GeneralLicenseType_v2 Get()
        {
            if (cached != null) return cached;

            // Nicht bei jedem Aufruf suchen (wird pro Listenzeile abgefragt), höchstens alle 5 s
            if (Time.realtimeSinceStartup < nextSearch) return null;
            nextSearch = Time.realtimeSinceStartup + 5f;

            try
            {
                var list = Globals.G.Types.generalLicenses;
                if (list == null || list.Count == 0) return null; // Liste noch nicht geladen

                cached = list.FirstOrDefault(l => l != null && l.id == ID);
                if (cached == null)
                    Main.Warn("License not found: " + ID + " (DVCustomLicenses JSON missing?)");
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("License lookup failed (" + ID + "): " + e);
            }
            return cached;
        }

        public bool Has
        {
            get
            {
                var lic = Get();
                if (lic == null) return false;
                try
                {
                    var mgr = SingletonBehaviour<LicenseManager>.Instance;
                    return mgr != null && mgr.IsGeneralLicenseAcquired(lic);
                }
                catch (Exception e)
                {
                    Main.ModEntry.Logger.Error("License check failed (" + ID + "): " + e);
                    return false;
                }
            }
        }

        // Preis und Eigenanteil-Erhöhung (Copay) aus den Einstellungen übernehmen
        public void ApplyValues(float price, float copay)
        {
            var lic = Get();
            if (lic == null) return;

            if (price > 0f && Math.Abs(lic.price - price) > 0.5f)
            {
                lic.price = price;
                Main.Log($"License {ID}: price set to {price:F0}");
            }
            if (copay >= 0f && Math.Abs(lic.insuranceFeeQuotaIncrease - copay) > 0.5f)
            {
                lic.insuranceFeeQuotaIncrease = copay;
                Main.Log($"License {ID}: copay increase set to {copay:F0}");
            }
        }
    }

    internal static class TrackLicenses
    {
        // Erste Lizenz: darf reparieren, zahlt (mit Versicherung)
        public static readonly TrackLicense Basic = new TrackLicense("TrackMaintenance");
        // Zweite Lizenz (setzt die erste voraus): wird für Reparaturen bezahlt
        public static readonly TrackLicense Contractor = new TrackLicense("TrackMaintenanceContractor");

        private static float nextApply;

        public static void ApplyValues()
        {
            if (Main.Settings == null || !RepairEconomy.HasCustomLicensesMod) return;
            try
            {
                Basic.ApplyValues(Main.Settings.licensePrice, Main.Settings.licenseCopay);
                Contractor.ApplyValues(Main.Settings.license2Price, Main.Settings.license2Copay);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("Applying license values failed: " + e);
            }
        }

        // Aus Main.OnUpdate: Änderungen in den Einstellungen übernehmen (Preis sofort,
        // Eigenanteil-Erhöhung bereits gekaufter Lizenzen spätestens nach dem nächsten Laden)
        public static void Tick()
        {
            if (Time.realtimeSinceStartup < nextApply) return;
            nextApply = Time.realtimeSinceStartup + 2f;
            ApplyValues();
        }
    }

    // Preise und Copay setzen, bevor bzw. nachdem die Lizenzen des Spielstands geladen werden
    // (vorher, damit der Eigenanteil gekaufter Lizenzen schon mit den eingestellten Werten berechnet wird)
    [HarmonyPatch(typeof(LicenseManager), "LoadData")]
    internal static class TQ_LicenseValues
    {
        static void Prefix() { TrackLicenses.ApplyValues(); }
        static void Postfix() { TrackLicenses.ApplyValues(); }
    }
}