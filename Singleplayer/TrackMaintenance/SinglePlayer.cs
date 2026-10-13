// File: SinglePlayer.cs
// Namespace: TrackMaintenance
// Einzelspieler-Version: Platzhalter für die Multiplayer-Schnittstelle, damit TrackRepair.cs in
// beiden Versionen identisch sein kann. Im Einzelspieler gibt es weder Host noch Clients.

namespace TrackMaintenance
{
    internal static class TM_Multiplayer
    {
        public static bool IsClient { get { return false; } }
        public static bool IsHost { get { return false; } }

        public static bool RequestRepair(RepairEntry entry, RepairQuote quote) { return false; }
        public static bool IsRepairPending(RailTrack track, double start) { return false; }
        public static void BroadcastRepairOwner(RailTrack track, TrainCar owner) { }
    }
}
