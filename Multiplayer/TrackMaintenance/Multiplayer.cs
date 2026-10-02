// File: Multiplayer.cs
// Namespace: TrackMaintenance
// Multiplayer (dv-multiplayer, MPAPI) nach dem Vorbild von JunctionMaintenance (MP).
//
// Grundsatz: Der Host ist autoritativ.
//   - Nur der Host erzeugt Verformungen (Entgleisung, Mitschleifen, Explosion).
//     Clients ignorieren ihre lokalen Auslöser, sonst entstünden abweichende Gleise.
//   - Jede Änderung am Verlauf eines Gleises (TrackHistory) markiert das Gleis als geändert.
//     Der Host schickt geänderte Gleise gebündelt an alle Clients (kompletter Verlauf pro Gleis).
//     Die Clients spielen den Verlauf exakt nach (gleiche Seeds) -> identische Geometrie.
//   - Reparaturen eines Clients sind Anfragen: Der Host prüft den Schaden, rechnet Kosten bzw.
//     Vergütung selbst aus (Host-Modus, Host-Versicherung), bucht das Geld auf der Host-Wallet,
//     repariert und verteilt den neuen Gleiszustand.
//   - Einstellungen kommen vom Host (temporär, die lokalen Einstellungen bleiben unverändert).
//   - Gespeichert wird nur auf dem Host.
//
// Messfahrt: Die Bremsphysik simuliert der Host. Die Zwangsbremsung eines Clients ist eine Anfrage
//   (ServerBoundTMBrakePacket, Heartbeat alle 0,5 s); der Host entlüftet den Bremsverband, bis der
//   Client freigibt oder der Heartbeat 2 s ausbleibt.
//
// Versicherung: Der Eigenanteil zählt nur beim Host. Der Host schickt den offenen Teil
//   (ClientBoundTMInsurancePacket) beim Beitritt, nach jeder Client-Reparatur und bei jeder Änderung
//   (Prüfung 1x pro Sekunde, z. B. nach eigenen Reparaturen oder Tilgen im CareerManager).
//
// Ablauf beim Beitritt:
//   Client vollständig geladen -> ServerBoundTMReadyPacket (wiederholt, bis Antwort kommt)
//   Host -> Einstellungen + kompletter Schadensstand (ClientBoundTMTracksPacket, FullSnapshot)

using System;
using System.Collections.Generic;
using System.IO;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using UnityEngine;
using DV.Utils;
using DV.InventorySystem;

namespace TrackMaintenance
{
    // ============================================================
    // PACKETS
    // ============================================================

    // Client -> Host: Client ist vollständig geladen, bitte Einstellungen + Schadensstand schicken
    public class ServerBoundTMReadyPacket : IPacket
    {
        public bool Ready { get; set; }
    }

    // Client -> Host: Zwangsbremsung der Messfahrt (Brake = true: anlegen/Heartbeat, false: lösen)
    public class ServerBoundTMBrakePacket : IPacket
    {
        public string CarId { get; set; } = string.Empty;   // irgendein Wagen des Zugverbands (Caboose)
        public bool Brake { get; set; }
    }

    // Client -> Host: Reparatur eines Abschnitts anfragen
    public class ServerBoundTMRepairPacket : IPacket
    {
        public int TrackIndex { get; set; }
        public string TrackName { get; set; } = string.Empty;
        public double Start { get; set; }
        public double End { get; set; }
        public int Percent { get; set; }           // nur zur Kontrolle (Host rechnet selbst)
        public double ClientAmount { get; set; }   // nur zur Kontrolle (Host rechnet selbst)
        public bool ClientIsReward { get; set; }   // nur zur Kontrolle (Host entscheidet)
    }

    // Host -> Client: Versicherungsstand des Hosts (offener Teil des Eigenanteils)
    public class ClientBoundTMInsurancePacket : IPacket
    {
        public bool Used { get; set; }     // Versicherung greift (Quote > 0)
        public double Left { get; set; }   // noch offener Teil des Eigenanteils
    }

    // Host -> Client: Ergebnis einer Reparaturanfrage
    public class ClientBoundTMRepairResultPacket : IPacket
    {
        public bool Ok { get; set; }
        public int TrackIndex { get; set; }
        public string TrackName { get; set; } = string.Empty;
        public double Start { get; set; }
    }

    // Host -> Client: Einstellungen des Hosts
    public class ClientBoundTMSettingsPacket : IPacket
    {
        public bool DeformEnabled { get; set; }
        public bool DeformAtDerail { get; set; }
        public bool DeformWhenDragged { get; set; }
        public bool DeformByExplosion { get; set; }
        public bool MeasureRunEnabled { get; set; }

        public float RepairRadius { get; set; }
        public float RepairSectionLength { get; set; }
        public float MaxRepairCost { get; set; }
        public float MaxRepairReward { get; set; }
        public int RepairMode { get; set; }        // wirksamer Modus des Hosts

        public float LicensePrice { get; set; }
        public float LicenseCopay { get; set; }
        public float License2Price { get; set; }
        public float License2Copay { get; set; }

        public float VmaxToleranceKmh { get; set; }
    }

    // Host -> Client: Verlauf eines oder mehrerer Gleise (manuell serialisiert, volle double-Genauigkeit)
    public class ClientBoundTMTracksPacket : ISerializablePacket
    {
        public sealed class Entry
        {
            public int Index;
            public string Name = string.Empty;
            public List<TrackOp> Ops = new List<TrackOp>();
        }

        // true = vollständiger Stand: Gleise, die nicht enthalten sind, sind unbeschädigt
        public bool FullSnapshot;
        public List<Entry> Tracks = new List<Entry>();

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(FullSnapshot);
            writer.Write(Tracks.Count);
            foreach (Entry e in Tracks)
            {
                writer.Write(e.Index);
                writer.Write(e.Name ?? string.Empty);
                writer.Write(e.Ops.Count);
                foreach (TrackOp op in e.Ops)
                {
                    writer.Write(op.IsRepair);
                    if (op.IsRepair)
                    {
                        writer.Write(op.Start);
                        writer.Write(op.End);
                        writer.Write(op.Blend);
                    }
                    else
                    {
                        writer.Write(op.Center);
                        writer.Write(op.Radius);
                        writer.Write(op.Kink.horizontal);
                        writer.Write(op.Kink.vertical);
                        writer.Write(op.Kink.roll);
                        writer.Write(op.Kink.frequency);
                        writer.Write(op.Kink.seed);
                        writer.Write(op.Kink.fade);
                    }
                }
            }
        }

        public void Deserialize(BinaryReader reader)
        {
            FullSnapshot = reader.ReadBoolean();
            int count = reader.ReadInt32();
            Tracks = new List<Entry>(Math.Max(0, count));

            for (int t = 0; t < count; t++)
            {
                var e = new Entry
                {
                    Index = reader.ReadInt32(),
                    Name = reader.ReadString()
                };

                int ops = reader.ReadInt32();
                for (int i = 0; i < ops; i++)
                {
                    bool isRepair = reader.ReadBoolean();
                    if (isRepair)
                    {
                        double start = reader.ReadDouble();
                        double end = reader.ReadDouble();
                        float blend = reader.ReadSingle();
                        e.Ops.Add(TrackOp.Repair(start, end, blend));
                    }
                    else
                    {
                        double center = reader.ReadDouble();
                        float radius = reader.ReadSingle();
                        var kink = new KinkParams
                        {
                            horizontal = reader.ReadSingle(),
                            vertical = reader.ReadSingle(),
                            roll = reader.ReadSingle(),
                            frequency = reader.ReadSingle(),
                            seed = reader.ReadSingle(),
                            fade = reader.ReadSingle()
                        };
                        e.Ops.Add(TrackOp.Deformation(center, radius, kink));
                    }
                }
                Tracks.Add(e);
            }
        }
    }

    // ============================================================
    // CENTRAL API
    // ============================================================

    internal static class TM_Multiplayer
    {
        private static GameObject runtimeObject;

        public static bool IsHost
        {
            get
            {
                try
                {
                    return MultiplayerAPI.Instance != null
                        && MultiplayerAPI.Server != null
                        && MultiplayerAPI.Instance.IsHost;
                }
                catch { return false; }
            }
        }

        public static bool IsClient
        {
            get
            {
                try
                {
                    return MultiplayerAPI.Instance != null
                        && MultiplayerAPI.Client != null
                        && !MultiplayerAPI.Instance.IsHost;
                }
                catch { return false; }
            }
        }

        public static bool IsMultiplayer { get { return IsHost || IsClient; } }

        public static void Initialize()
        {
            if (runtimeObject != null) return;

            runtimeObject = new GameObject("TrackMaintenance_Multiplayer");
            UnityEngine.Object.DontDestroyOnLoad(runtimeObject);
            runtimeObject.AddComponent<TrackMaintenanceMPClient>();
            runtimeObject.AddComponent<TrackMaintenanceMPServer>();

            Main.Log("[MP] Multiplayer runtime created");
        }

        // Client: Reparatur beim Host anfragen. false = konnte nicht gesendet werden.
        public static bool RequestRepair(RepairEntry entry, RepairQuote quote)
        {
            var client = TrackMaintenanceMPClient.Instance;
            return client != null && client.SendRepairRequest(entry, quote);
        }

        // Client: Abschnitt wartet auf die Antwort des Hosts (wird in der Liste ausgeblendet)
        public static bool IsRepairPending(RailTrack track, double start)
        {
            var client = TrackMaintenanceMPClient.Instance;
            return client != null && client.IsPending(track, start);
        }

        // Host: Einstellungen nach dem Speichern an alle Clients
        public static void BroadcastHostSettings()
        {
            if (IsHost) TrackMaintenanceMPServer.Instance?.SendSettingsToAll();
        }

        // Reparaturliste nach einer Änderung aus dem Netz aktualisieren
        internal static void RefreshOpenRepairList()
        {
            try { TQ_ListState.RefreshFromNetwork(); }
            catch (Exception e) { Main.ModEntry.Logger.Error("[MP] Repair list refresh failed: " + e); }
        }
    }

    // ============================================================
    // CLIENT
    // ============================================================

    internal class TrackMaintenanceMPClient : MonoBehaviour
    {
        public static TrackMaintenanceMPClient Instance { get; private set; }

        private IClient client;
        private bool registered;
        private bool wasClient;
        private bool clearedForConnection;
        private bool snapshotReceived;
        private float nextReadyRequest;

        // Zwangsbremsung der Messfahrt beim Host
        private const float BrakeHeartbeat = 0.5f;
        private string brakeCarId;          // Wagen, für den zuletzt "bremsen" gesendet wurde
        private float nextBrakeHeartbeat;

        // Offene Reparaturanfragen: Gleis -> Abschnittsanfänge
        private readonly Dictionary<RailTrack, HashSet<double>> pending = new Dictionary<RailTrack, HashSet<double>>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(this); return; }

            wasClient = TM_Multiplayer.IsClient;
        }

        private void Update()
        {
            try
            {
                RefreshConnectionState();
                if (!registered) TryRegister();
                TrySendReady();
                SyncPenaltyBrake();
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("[MP] Client update failed: " + e);
            }
        }

        private void RefreshConnectionState()
        {
            bool isClientNow = TM_Multiplayer.IsClient;
            IClient current = MultiplayerAPI.Client;

            if (!ReferenceEquals(client, current))
            {
                if (client != null) Main.RestoreLocalSettings();
                client = current;
                ResetSyncState();
                Main.Log("[MP] Client connection changed, synchronization state reset");
            }

            if (wasClient != isClientNow)
            {
                bool left = wasClient && !isClientNow;
                wasClient = isClientNow;
                ResetSyncState();

                if (left) Main.RestoreLocalSettings();
                Main.Log(isClientNow ? "[MP] Entered multiplayer as client"
                                     : "[MP] Left multiplayer client state, local settings restored");
            }

            if (isClientNow && !clearedForConnection)
            {
                // Lokalen (Einzelspieler-)Zustand verwerfen, der Host liefert den echten Stand
                LiveDeform.ApplyRemoteHistories(new List<(RailTrack, List<TrackOp>)>(), true);
                clearedForConnection = true;
                Main.Log("[MP] Local track damage cleared, waiting for host snapshot");
                TM_Multiplayer.RefreshOpenRepairList();
            }
        }

        private void ResetSyncState()
        {
            registered = false;
            snapshotReceived = false;
            nextReadyRequest = 0f;
            clearedForConnection = false;
            pending.Clear();
            brakeCarId = null;
            nextBrakeHeartbeat = 0f;
            RepairEconomy.ClearHostInsurance();
        }

        // ---------- Zwangsbremsung (Messfahrt) ----------

        // Zustand der lokalen Messfahrt an den Host melden: Anlegen sofort, solange gebremst wird
        // regelmäßig als Heartbeat, Lösen sofort (auch beim Beenden der Messfahrt).
        private void SyncPenaltyBrake()
        {
            if (!registered || client == null || !TM_Multiplayer.IsClient) return;

            string id = null;
            if (MeasureRun.Active && MeasureRun.Braking && MeasureRun.Car != null)
                id = MeasureRun.Car.ID;

            if (string.IsNullOrEmpty(id))
            {
                if (brakeCarId != null)
                {
                    SendBrake(brakeCarId, false);
                    brakeCarId = null;
                }
                return;
            }

            // Wagen gewechselt: alten freigeben
            if (brakeCarId != null && brakeCarId != id)
                SendBrake(brakeCarId, false);

            if (brakeCarId != id || Time.unscaledTime >= nextBrakeHeartbeat)
            {
                SendBrake(id, true);
                brakeCarId = id;
                nextBrakeHeartbeat = Time.unscaledTime + BrakeHeartbeat;
            }
        }

        private void SendBrake(string carId, bool brake)
        {
            try
            {
                client.SendPacketToServer(new ServerBoundTMBrakePacket { CarId = carId, Brake = brake }, reliable: true);
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("[MP] Sending penalty brake failed: " + e);
            }
        }

        private void TryRegister()
        {
            client = MultiplayerAPI.Client;
            if (client == null) return;

            client.RegisterPacket<ClientBoundTMSettingsPacket>(OnSettingsReceived);
            client.RegisterPacket<ClientBoundTMRepairResultPacket>(OnRepairResult);
            client.RegisterPacket<ClientBoundTMInsurancePacket>(OnInsuranceReceived);
            client.RegisterSerializablePacket<ClientBoundTMTracksPacket>(OnTracksReceived);

            registered = true;
            Main.Log("[MP] Client packet handlers registered");
        }

        private void TrySendReady()
        {
            if (!registered || client == null || !TM_Multiplayer.IsClient || snapshotReceived) return;
            if (PlayerManager.PlayerTransform == null) return;
            if (!TrackPersistence.LoadingFinished()) return;
            if (Time.unscaledTime < nextReadyRequest) return;

            client.SendPacketToServer(new ServerBoundTMReadyPacket { Ready = true }, reliable: true);
            nextReadyRequest = Time.unscaledTime + 2f;
            Main.Log("[MP] Client loaded, snapshot requested");
        }

        // ---------- Reparatur ----------

        public bool IsPending(RailTrack track, double start)
        {
            HashSet<double> set;
            return track != null && pending.TryGetValue(track, out set) && set.Contains(start);
        }

        public bool SendRepairRequest(RepairEntry entry, RepairQuote quote)
        {
            if (!registered || client == null || entry == null || entry.Track == null || !snapshotReceived)
                return false;

            int index = TrackRefs.IndexOf(entry.Track);
            if (index < 0) return false;

            var packet = new ServerBoundTMRepairPacket
            {
                TrackIndex = index,
                TrackName = entry.Track.name ?? string.Empty,
                Start = entry.Start,
                End = entry.End,
                Percent = entry.Percent,
                ClientAmount = quote.IsReward ? quote.Payout : quote.Pay,
                ClientIsReward = quote.IsReward
            };
            client.SendPacketToServer(packet, reliable: true);

            HashSet<double> set;
            if (!pending.TryGetValue(entry.Track, out set)) pending[entry.Track] = set = new HashSet<double>();
            set.Add(entry.Start);

            Main.Log($"[MP] Repair requested: {entry.TrackId} {entry.Start:F1}-{entry.End:F1} m ({entry.Percent} %)");
            return true;
        }

        private void OnRepairResult(ClientBoundTMRepairResultPacket packet)
        {
            if (packet == null) return;
            RailTrack track = TrackRefs.Find(packet.TrackIndex, packet.TrackName);

            HashSet<double> set;
            if (track != null && pending.TryGetValue(track, out set))
            {
                set.Remove(packet.Start);
                if (set.Count == 0) pending.Remove(track);
            }

            if (!packet.Ok)
            {
                TQ_ListState.Status = Loc.T("repair.failed");
                Main.Warn($"[MP] Host rejected repair of '{packet.TrackName}' at {packet.Start:F1} m");
            }
            TM_Multiplayer.RefreshOpenRepairList();
        }

        // ---------- Versicherung ----------

        private void OnInsuranceReceived(ClientBoundTMInsurancePacket p)
        {
            if (p == null) return;
            RepairEconomy.SetHostInsurance(p.Used, p.Left);
            Main.Log($"[MP] Host insurance: {(p.Used ? "active" : "inactive")}, {p.Left:F2} left to reach quota");
            TM_Multiplayer.RefreshOpenRepairList();   // Einträge neu aufbauen -> Preise neu berechnet
        }

        // ---------- Gleiszustand ----------

        private void OnTracksReceived(ClientBoundTMTracksPacket packet)
        {
            if (packet == null) return;

            // Einzelne Änderungen vor dem ersten vollständigen Stand ignorieren (werden davon ohnehin überschrieben)
            if (!packet.FullSnapshot && !snapshotReceived) return;

            var items = new List<(RailTrack, List<TrackOp>)>(packet.Tracks.Count);
            int missing = 0;
            foreach (var e in packet.Tracks)
            {
                RailTrack track = TrackRefs.Find(e.Index, e.Name);
                if (track == null) { missing++; continue; }
                items.Add((track, e.Ops));
                pending.Remove(track);   // neuer Zustand -> Liste wird ohnehin neu aufgebaut
            }

            LiveDeform.ApplyRemoteHistories(items, packet.FullSnapshot);

            if (packet.FullSnapshot)
            {
                snapshotReceived = true;
                pending.Clear();
            }

            Main.Log($"[MP] {(packet.FullSnapshot ? "Snapshot" : "Update")} applied: {items.Count} track(s)"
                     + (missing > 0 ? $", {missing} not found" : ""));
            TM_Multiplayer.RefreshOpenRepairList();
        }

        // ---------- Einstellungen ----------

        private void OnSettingsReceived(ClientBoundTMSettingsPacket p)
        {
            if (p == null || Main.LocalSettings == null) return;

            Settings s = Main.LocalSettings.CloneConfiguration();
            s.deformEnabled = p.DeformEnabled;
            s.deformAtDerail = p.DeformAtDerail;
            s.deformWhenDragged = p.DeformWhenDragged;
            s.deformByExplosion = p.DeformByExplosion;
            s.measureRunEnabled = p.MeasureRunEnabled;
            s.repairRadius = Mathf.Max(0f, p.RepairRadius);
            s.repairSectionLength = Mathf.Max(1f, p.RepairSectionLength);
            s.maxRepairCost = Mathf.Max(0f, p.MaxRepairCost);
            s.maxRepairReward = Mathf.Max(0f, p.MaxRepairReward);
            s.licensePrice = Mathf.Max(0f, p.LicensePrice);
            s.licenseCopay = Mathf.Max(0f, p.LicenseCopay);
            s.license2Price = Mathf.Max(0f, p.License2Price);
            s.license2Copay = Mathf.Max(0f, p.License2Copay);
            s.vmaxToleranceKmh = Mathf.Max(0f, p.VmaxToleranceKmh);

            RepairMode mode = Enum.IsDefined(typeof(RepairMode), p.RepairMode) ? (RepairMode)p.RepairMode : RepairMode.Penalty;
            s.repairMode = mode;

            Main.UseHostSettings(s, mode);
            Main.Log("[MP] Host settings applied (mode " + mode + ")");
            TM_Multiplayer.RefreshOpenRepairList();
        }

        private void OnDestroy()
        {
            Main.RestoreLocalSettings();
            if (Instance == this) Instance = null;
            client = null;
            ResetSyncState();
        }
    }

    // ============================================================
    // SERVER (Host)
    // ============================================================

    internal class TrackMaintenanceMPServer : MonoBehaviour
    {
        public static TrackMaintenanceMPServer Instance { get; private set; }

        private IServer server;
        private bool initialized;
        private float nextFlush;
        private const float FlushInterval = 0.25f;

        // Zuletzt an die Clients geschickter Versicherungsstand
        private const float InsuranceCheckInterval = 1f;
        private float nextInsuranceCheck;
        private bool insuranceSent;
        private bool sentInsuranceUsed;
        private double sentInsuranceLeft;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(this); return; }
        }

        private void Update()
        {
            try
            {
                if (!ReferenceEquals(server, MultiplayerAPI.Server))
                {
                    server = null;
                    initialized = false;
                    insuranceSent = false;
                    PenaltyBrake.ClearRemote();
                }
                if (!initialized) TryInitialize();

                // Versicherungsstand bei Änderung an alle Clients (z. B. nach Tilgen im CareerManager)
                if (initialized && TM_Multiplayer.IsHost)
                    SendInsuranceIfChanged(false);

                // Geänderte Gleise gebündelt an alle Clients
                if (initialized && TM_Multiplayer.IsHost && Time.unscaledTime >= nextFlush)
                {
                    nextFlush = Time.unscaledTime + FlushInterval;
                    FlushDirtyTracks();
                }
                else if (!TM_Multiplayer.IsHost)
                {
                    TrackHistory.ClearDirty();
                }
            }
            catch (Exception e)
            {
                Main.ModEntry.Logger.Error("[MP] Server update failed: " + e);
            }
        }

        private void TryInitialize()
        {
            server = MultiplayerAPI.Server;
            if (server == null) return;

            server.RegisterPacket<ServerBoundTMReadyPacket>(OnClientReady);
            server.RegisterPacket<ServerBoundTMRepairPacket>(OnRepairRequested);
            server.RegisterPacket<ServerBoundTMBrakePacket>(OnBrakeRequested);

            initialized = true;
            Main.Log("[MP] Server packet handlers registered");
        }

        // ---------- Beitritt ----------

        private void OnClientReady(ServerBoundTMReadyPacket packet, IPlayer sender)
        {
            if (packet == null || sender == null || !packet.Ready) return;

            server.SendPacketToPlayer(CreateSettingsPacket(), sender, reliable: true);
            server.SendSerializablePacketToPlayer(CreateTracksPacket(null, true), sender, reliable: true);
            server.SendPacketToPlayer(CreateInsurancePacket(), sender, reliable: true);
            Main.Log("[MP] Settings, insurance and track snapshot sent to client");
        }

        // ---------- Versicherung ----------

        private static ClientBoundTMInsurancePacket CreateInsurancePacket()
        {
            bool used;
            double left;
            RepairEconomy.GetLocalInsurance(out used, out left);
            return new ClientBoundTMInsurancePacket { Used = used, Left = left };
        }

        // force = true: sofort prüfen (nach einer Reparatur eines Clients)
        private void SendInsuranceIfChanged(bool force)
        {
            if (server == null) return;
            if (!force && Time.unscaledTime < nextInsuranceCheck) return;
            nextInsuranceCheck = Time.unscaledTime + InsuranceCheckInterval;

            var p = CreateInsurancePacket();
            if (insuranceSent && p.Used == sentInsuranceUsed && Math.Abs(p.Left - sentInsuranceLeft) < 0.005) return;

            insuranceSent = true;
            sentInsuranceUsed = p.Used;
            sentInsuranceLeft = p.Left;
            server.SendPacketToAll(p, reliable: true, excludeSelf: true);
            Main.Log($"[MP] Insurance state sent: {(p.Used ? "active" : "inactive")}, {p.Left:F2} left to reach quota");
        }

        // ---------- Zwangsbremsung eines Clients (Messfahrt) ----------

        private void OnBrakeRequested(ServerBoundTMBrakePacket packet, IPlayer sender)
        {
            if (packet == null || sender == null || string.IsNullOrEmpty(packet.CarId)) return;
            if (packet.Brake && (Main.Settings == null || !Main.Settings.MeasureRun)) return;

            PenaltyBrake.SetRemote(packet.CarId, packet.Brake);
        }

        // ---------- Gleiszustand ----------

        private void FlushDirtyTracks()
        {
            List<RailTrack> dirty = TrackHistory.TakeDirty();
            if (dirty.Count == 0) return;

            var packet = CreateTracksPacket(dirty, false);
            if (packet.Tracks.Count == 0) return;

            server.SendSerializablePacketToAll(packet, reliable: true, excludeSelf: true);
            Main.Log($"[MP] Track update sent: {packet.Tracks.Count} track(s)");
        }

        // tracks == null: alle Gleise mit Verlauf (vollständiger Stand)
        private static ClientBoundTMTracksPacket CreateTracksPacket(List<RailTrack> tracks, bool full)
        {
            var packet = new ClientBoundTMTracksPacket { FullSnapshot = full };

            IEnumerable<RailTrack> source;
            if (tracks != null) source = tracks;
            else
            {
                var all = new List<RailTrack>();
                foreach (var kv in TrackHistory.All()) all.Add(kv.Key);
                source = all;
            }

            foreach (RailTrack track in source)
            {
                if (track == null || JunctionTracks.Is(track)) continue;
                int index = TrackRefs.IndexOf(track);
                if (index < 0) continue;

                // Leerer Verlauf = Gleis ist wieder im Originalzustand (muss bei Updates mitgeschickt werden)
                List<TrackOp> ops = TrackHistory.Get(track);
                packet.Tracks.Add(new ClientBoundTMTracksPacket.Entry
                {
                    Index = index,
                    Name = track.name ?? string.Empty,
                    Ops = ops != null ? new List<TrackOp>(ops) : new List<TrackOp>()
                });
            }
            return packet;
        }

        // ---------- Einstellungen ----------

        public void SendSettingsToAll()
        {
            if (!initialized || server == null || Main.Settings == null) return;
            server.SendPacketToAll(CreateSettingsPacket(), reliable: true, excludeSelf: true);
        }

        private static ClientBoundTMSettingsPacket CreateSettingsPacket()
        {
            Settings s = Main.Settings;
            return new ClientBoundTMSettingsPacket
            {
                DeformEnabled = s.deformEnabled,
                DeformAtDerail = s.deformAtDerail,
                DeformWhenDragged = s.deformWhenDragged,
                DeformByExplosion = s.deformByExplosion,
                MeasureRunEnabled = s.measureRunEnabled,
                RepairRadius = s.repairRadius,
                RepairSectionLength = s.RepairSectionLength,
                MaxRepairCost = s.maxRepairCost,
                MaxRepairReward = s.maxRepairReward,
                RepairMode = (int)RepairEconomy.Mode,
                LicensePrice = s.licensePrice,
                LicenseCopay = s.licenseCopay,
                License2Price = s.license2Price,
                License2Copay = s.license2Copay,
                VmaxToleranceKmh = s.vmaxToleranceKmh
            };
        }

        // ---------- Reparatur eines Clients ----------

        private void OnRepairRequested(ServerBoundTMRepairPacket p, IPlayer sender)
        {
            if (p == null || sender == null) return;

            bool ok = false;
            try { ok = ProcessRepair(p); }
            catch (Exception e) { Main.ModEntry.Logger.Error("[MP] Client repair failed: " + e); }

            // Eigenanteil hat sich durch die Reparatur geändert -> allen Clients sofort melden
            if (ok) SendInsuranceIfChanged(true);

            server.SendPacketToPlayer(new ClientBoundTMRepairResultPacket
            {
                Ok = ok,
                TrackIndex = p.TrackIndex,
                TrackName = p.TrackName ?? string.Empty,
                Start = p.Start
            }, sender, reliable: true);
        }

        private static bool ProcessRepair(ServerBoundTMRepairPacket p)
        {
            if (Main.Settings == null || !Main.Settings.TrackRepair) return false;
            if (!RepairEconomy.CanRepair)
            {
                Main.Warn("[MP] Client repair rejected: no track maintenance license");
                return false;
            }

            RailTrack track = TrackRefs.Find(p.TrackIndex, p.TrackName);
            if (track == null || JunctionTracks.Is(track))
            {
                Main.Warn($"[MP] Client repair rejected: track '{p.TrackName}' not found");
                return false;
            }

            // Abschnitt plausibel? (höchstens eine Abschnittslänge, innerhalb des Gleises)
            double len = p.End - p.Start;
            if (len <= 0.0 || len > Main.Settings.RepairSectionLength + 0.01 || p.Start < -0.01)
            {
                Main.Warn($"[MP] Client repair rejected: invalid section {p.Start:F1}-{p.End:F1} m");
                return false;
            }

            // Nur der Schadensstand des Hosts zählt
            int percent = Mathf.RoundToInt(DamageLedger.Section01(track, p.Start, p.End) * 100f);
            if (percent < 1)
            {
                Main.Warn($"[MP] Client repair rejected: '{p.TrackName}' {p.Start:F1} m has no damage");
                return false;
            }

            // Host berechnet Betrag und Modus selbst (eigene Versicherung, eigene Lizenzen)
            RepairQuote q = RepairEconomy.Quote(percent);
            double hostAmount = q.IsReward ? q.Payout : q.Pay;
            if (q.IsReward != p.ClientIsReward || Math.Abs(hostAmount - p.ClientAmount) > 0.01)
                Main.Log($"[MP] Client quote differs (client {(p.ClientIsReward ? "+" : "")}{p.ClientAmount:F2}, "
                         + $"host {(q.IsReward ? "+" : "")}{hostAmount:F2}), host values used");

            var inventory = DV.Utils.SingletonBehaviour<DV.InventorySystem.Inventory>.Instance;
            if (inventory == null)
            {
                Main.Warn("[MP] Host inventory unavailable, client repair rejected");
                return false;
            }

            // Bezahlen: vorher prüfen, ob die Host-Wallet reicht
            if (!q.IsReward && q.Pay > 0.0 && inventory.PlayerMoney + 0.001 < q.Pay)
            {
                Main.Warn($"[MP] Host cannot pay {q.Pay:F2} for client repair (balance {inventory.PlayerMoney:F2})");
                return false;
            }

            if (!LiveDeform.RepairSection(track, p.Start, p.End)) return false;

            if (q.IsReward)
            {
                if (q.Payout > 0.0) inventory.AddMoney(q.Payout);
                Main.Log($"[MP] Client repair of '{track.name}': host wallet credited {q.Payout:F2}");
            }
            else
            {
                if (q.Pay > 0.0 && !inventory.RemoveMoney(q.Pay))
                    Main.Warn($"[MP] Host wallet payment of {q.Pay:F2} failed after client repair");
                RepairEconomy.AfterPaidRepair(q);
                Main.Log($"[MP] Client repair of '{track.name}': host wallet charged {q.Pay:F2}, insurance {q.Covered:F2}");
            }
            return true;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            server = null;
            initialized = false;
            PenaltyBrake.ClearRemote();
        }
    }
}