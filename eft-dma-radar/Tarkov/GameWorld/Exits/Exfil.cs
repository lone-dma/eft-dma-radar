﻿using eft_dma_radar.Tarkov.Player;
using eft_dma_radar.UI.Radar;
using eft_dma_radar.Unity;
using eft_dma_radar.Unity.Collections;
using eft_dma_radar.UI.Skia;
using eft_dma_radar.Misc;
using eft_dma_radar.UI.Skia.Maps;
using eft_dma_radar.Tarkov.Data;

namespace eft_dma_radar.Tarkov.GameWorld.Exits
{
    public class Exfil : IExitPoint, IWorldEntity, IMapEntity, IMouseoverEntity
    {
        public static implicit operator ulong(Exfil x) => x._addr;
        private static readonly uint[] _transformInternalChain =
{
            ObjectClass.MonoBehaviourOffset, MonoBehaviour.GameObjectOffset, GameObject.ComponentsOffset, 0x8
        };

        private readonly bool _isPMC;
        private HashSet<string> PmcEntries { get; } = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> ScavIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Exfil(ulong baseAddr, bool isPMC)
        {
            _addr = baseAddr;
            _isPMC = isPMC;
            var transformInternal = Memory.ReadPtrChain(baseAddr, _transformInternalChain, false);
            var namePtr = Memory.ReadPtrChain(baseAddr, new[] { Offsets.Exfil.Settings, Offsets.ExfilSettings.Name });
            Name = Memory.ReadUnityString(namePtr)?.Trim();
            if (string.IsNullOrEmpty(Name))
                Name = "default";
            // Lookup real map name (if possible)
            if (StaticGameData.ExfilNames.TryGetValue(Memory.MapID, out var mapExfils)
                && mapExfils.TryGetValue(Name, out var exfilName))
                Name = exfilName;
            _position = new UnityTransform(transformInternal).UpdatePosition();
        }

        private readonly ulong _addr;
        public string Name { get; }
        public virtual EStatus Status { get; private set; } = EStatus.Closed;

        /// <summary>
        /// Update Exfil Information/Status.
        /// </summary>
        public virtual void Update(Enums.EExfiltrationStatus status)
        {
            /// Update Status
            switch (status)
            {
                case Enums.EExfiltrationStatus.NotPresent:
                    Status = EStatus.Closed;
                    break;
                case Enums.EExfiltrationStatus.UncompleteRequirements:
                    Status = EStatus.Pending;
                    break;
                case Enums.EExfiltrationStatus.Countdown:
                    Status = EStatus.Open;
                    break;
                case Enums.EExfiltrationStatus.RegularMode:
                    Status = EStatus.Open;
                    break;
                case Enums.EExfiltrationStatus.Pending:
                    Status = EStatus.Pending;
                    break;
                case Enums.EExfiltrationStatus.AwaitsManualActivation:
                    Status = EStatus.Pending;
                    break;
                case Enums.EExfiltrationStatus.Hidden:
                    Status = EStatus.Pending;
                    break;
            }
            /// Update Entry Points
            if (_isPMC)
            {
                var entriesArrPtr = Memory.ReadPtr(_addr + Offsets.Exfil.EligibleEntryPoints);
                using var entriesArrLease = MemArray<ulong>.Lease(entriesArrPtr, true, out var entriesArr);
                foreach (var entryNamePtr in entriesArr)
                {
                    var entryName = Memory.ReadUnityString(entryNamePtr);
                    PmcEntries.Add(entryName);
                }
            }
            else // Scav Exfils
            {
                var eligibleIdsPtr = Memory.ReadPtr(_addr + Offsets.ScavExfil.EligibleIds);
                using var idsArrLease = MemList<ulong>.Lease(eligibleIdsPtr, true, out var idsArr);
                foreach (var idPtr in idsArr)
                {
                    var idName = Memory.ReadUnityString(idPtr);
                    ScavIds.Add(idName);
                }
            }
        }

        #region Interfaces

        private Vector3 _position;
        public ref Vector3 Position => ref _position;
        public Vector2 MouseoverPosition { get; set; }

        public void Draw(SKCanvas canvas, EftMapParams mapParams, LocalPlayer localPlayer)
        {
            var heightDiff = Position.Y - localPlayer.Position.Y;
            var paint = GetPaint();
            var point = Position.ToMapPos(mapParams.Map).ToZoomedPos(mapParams);
            MouseoverPosition = new Vector2(point.X, point.Y);
            SKPaints.ShapeOutline.StrokeWidth = 2f;
            if (heightDiff > 1.85f) // exfil is above player
            {
                using var path = point.GetUpArrow(6.5f);
                canvas.DrawPath(path, SKPaints.ShapeOutline);
                canvas.DrawPath(path, paint);
            }
            else if (heightDiff < -1.85f) // exfil is below player
            {
                using var path = point.GetDownArrow(6.5f);
                canvas.DrawPath(path, SKPaints.ShapeOutline);
                canvas.DrawPath(path, paint);
            }
            else // exfil is level with player
            {
                float size = 4.75f * App.Config.UI.UIScale;
                canvas.DrawCircle(point, size, SKPaints.ShapeOutline);
                canvas.DrawCircle(point, size, paint);
            }
        }

        public virtual SKPaint GetPaint()
        {
            var localPlayer = Memory.LocalPlayer;
            if (localPlayer is not null && localPlayer.IsPmc &&
                !PmcEntries.Contains(localPlayer.EntryPoint ?? "NULL"))
                return SKPaints.PaintExfilInactive;
            if (localPlayer is not null && localPlayer.IsScav &&
                !ScavIds.Contains(localPlayer.ProfileId))
                return SKPaints.PaintExfilInactive;
            switch (Status)
            {
                case EStatus.Open:
                    return SKPaints.PaintExfilOpen;
                case EStatus.Pending:
                    return SKPaints.PaintExfilPending;
                case EStatus.Closed:
                    return SKPaints.PaintExfilClosed;
                default:
                    return SKPaints.PaintExfilClosed;
            }
        }

        public void DrawMouseover(SKCanvas canvas, EftMapParams mapParams, LocalPlayer localPlayer)
        {
            List<string> lines = new();
            var exfilName = Name;
            exfilName ??= "unknown";
            lines.Add($"{exfilName} ({Status.ToString()})");
            Position.ToMapPos(mapParams.Map).ToZoomedPos(mapParams).DrawMouseoverText(canvas, lines);
        }

        #endregion

        public enum EStatus
        {
            Open,
            Pending,
            Closed
        }
    }
}
