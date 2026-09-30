using Chisel.Utils;
using Engine.Physics;
using Engine.Rendering;
using Engine.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Rockwall;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static Engine.MainEngine;

namespace Engine.Entities
{
    public abstract class AIAgent : EntityController
    {
        /// <summary>
        /// Indexes into the maps nodegraph describing the calculated path for the agent, null if unavailable or if we can walk to the target.
        /// </summary>
        protected int[] currentNodePath;
        /// <summary>
        /// How far along we are into <see cref="waypoints"/>, indicating what point we're moving to.
        /// </summary>
        protected int pathProgress;
        /// <summary>
        /// Dictates whether the path as a whole is complete, meaning the agent has completely reached the target.
        /// </summary>
        protected bool pathCompleted;
        /// <summary>
        /// Where we're trying to go.
        /// </summary>
        protected Vector3 currentTarget;
        /// <summary>
        /// True if the last call to <see cref="GetMoveTarget"/> had to steer away from the direct
        /// path (angleOffset != 0, or every angle was blocked) rather than moving straight along it.
        /// </summary>
        protected bool lastMoveAvoided;
        /// <summary>
        /// Distance budget remaining while committed to <see cref="avoidTurnSign"/>
        /// </summary>
        protected int avoidCommitSteps;
        protected float avoidAngleDeg;
        protected float avoidTurnSign;
        /// <summary>
        /// The point actually returned by the last <see cref="GetMoveTarget"/> call, kept purely for debug
        /// visualization so you can see the final decision separately from the probes that led to it.
        /// </summary>
        protected Vector3? lastMoveTarget;

        private const float MaxStepHeight = 0.75f;

        protected struct DirectionProbe
        {
            public Vector3 direction;
            public float distance;
            public bool blocked;
            public bool chosen;
            public bool trusted;
            public Vector3 hitPoint;
            public Vector3 halfExtents;
            public float vertOffset;
        }
        protected readonly List<DirectionProbe> lastProbes = new();
        private static readonly Dictionary<(float hx, float hy, float hz, float exp), BoundingBox> sweepBoundsCache = new();

        public struct Waypoint
        {
            public Vector3 position;
        }
        /// <summary>
        /// A list of points describing where we need to move.
        /// </summary>
        protected readonly List<Waypoint> waypoints = new List<Waypoint>();

        /// <summary>
        /// In units, how close this agent has to be to the target point (projected onto the floor) for <see cref="pathCompleted"/> to be marked true
        /// </summary>
        protected float pathCompleteThreshold = 1f;

        // Only repath when the target has moved more than ~0.5 units from the last search.
        private const float RepathThresholdSq = 0.25f;
        private Vector3 lastSearchTarget = new Vector3(float.MaxValue);

        private static int globalAgentCounter = 0;
        private readonly int agentUpdateOffset = globalAgentCounter++;

        public const float MoveStepIntervalSeconds = 1f / 6f;
        public const int StaggerBucketCount = 10;

        private float moveStepAccumulator;

        private DynamicVertexBuffer debugBuffer;
        private BasicEffect debugEffect;

        private const float GroundClearanceBias = 0.05f;

        private const float ObstacleLookaheadDistance = 4f;
        private const float ObstacleLookaheadStep = 1f;

        private const float AvoidTurnStepDeg = 20;
        private const int AvoidCommitSteps = 40;

        private const int MaxProbeSteps = 8;

        public bool IsPathCompleted() => pathCompleted;

        public AIAgent()
        {
            moveStepAccumulator = (agentUpdateOffset % StaggerBucketCount) * (MoveStepIntervalSeconds / StaggerBucketCount);
        }

        /// <summary>
        /// Recalculates a path to a new target point, projected onto the floor from <paramref name="_targetPoint"/>
        /// </summary>
        /// <param name="_targetPoint">The point we want to reach that will be projected onto the floor</param>
        protected void FindNewPath(Vector3 _targetPoint)
        {
            Vector3 targetPoint = _targetPoint;
            var hit = BSPRoot.TraceRay(new Ray(_targetPoint, Vector3.Down), 100f, false);
            if (hit.Hit)
            {
                targetPoint = hit.Point;
            }
            else if (GlobalMapData.ActiveMap.Nodegraph.Nodes.Length > 0)
            {
                int nearestNode = AINodeUtils.FindClosestNode(_targetPoint);
                targetPoint = GlobalMapData.ActiveMap.Nodegraph.Nodes[nearestNode].Position;
            }

            if (Vector3.DistanceSquared(targetPoint, lastSearchTarget) < RepathThresholdSq && !pathCompleted)
                return;

            lastSearchTarget = targetPoint;
            pathCompleted = false;
            currentTarget = targetPoint;

            int ourNode = AINodeUtils.FindClosestNode(entity.Position);
            int targNode = AINodeUtils.FindClosestNode(currentTarget);

            currentNodePath = AINodeUtils.Search(ourNode, targNode);
            pathProgress = 0;

            GenerateWaypoints();
        }

        /// <summary>
        /// Forces a full repath even if the target hasn't changed.
        /// Use when the environment has changed (a door closed, path blocked, etc.)
        /// </summary>
        protected void ForceRepath(Vector3 targetPoint)
        {
            lastSearchTarget = new Vector3(float.MaxValue);
            FindNewPath(targetPoint);
        }

        private void GenerateWaypoints()
        {
            waypoints.Clear();

            Vector3 projectedSelf = AINodeUtils.ProjectOnFloor(entity.Position, entity);
            Vector3 projectedTarget = AINodeUtils.ProjectOnFloor(currentTarget, entity);

            bool targetOnFloor = projectedTarget != currentTarget;
            bool directPathClear = targetOnFloor && AINodeUtils.IsPathWalkable(projectedSelf, projectedTarget, out _);

            if (!directPathClear && currentNodePath != null && currentNodePath.Length > 0 && GlobalMapData.ActiveMap.Nodegraph.Nodes.Length > 0)
            {
                for (int i = 0; i < currentNodePath.Length; i++)
                {
                    waypoints.Add(new Waypoint { position = GlobalMapData.ActiveMap.Nodegraph.Nodes[currentNodePath[i]].Position });
                }
            }

            waypoints.Add(new Waypoint { position = currentTarget });

            //StringPullWaypoints();
        }


        /// <summary>
        /// Returns a point projected <paramref name="distance"/> units ahead along the
        /// remaining waypoint path.
        /// </summary>
        protected Vector3 GetLookAheadTarget(float distance)
        {
            if (pathCompleted || pathProgress >= waypoints.Count)
            {
                return currentTarget;
            }

            Vector3 cursor = entity.Position;
            float remaining = distance;

            for (int i = pathProgress; i < waypoints.Count; i++)
            {
                Vector3 wp = waypoints[i].position;
                float segLen = Vector3.Distance(cursor, wp);

                if (segLen < 0.001f) { cursor = wp; continue; }

                if (segLen >= remaining)
                {
                    return cursor + Vector3.Normalize(wp - cursor) * remaining;
                }

                remaining -= segLen;
                cursor = wp;
            }

            return currentTarget;
        }

        /// <summary>
        /// Returns the total remaining path length from the current entity position
        /// through all unvisited waypoints to the destination.
        /// </summary>
        protected float GetRemainingPathLength()
        {
            if (pathCompleted || pathProgress >= waypoints.Count)
            {
                return 0f;
            }

            float total = Vector3.Distance(entity.Position, waypoints[pathProgress].position);

            for (int i = pathProgress; i < waypoints.Count - 1; i++)
            {
                total += Vector3.Distance(waypoints[i].position, waypoints[i + 1].position);
            }

            return total;
        }

        /// <summary>
        /// Gives the next move target of the agent: heads straight for the current waypoint when possible, or
        /// steers around obstacles Doom-monster style.
        /// </summary>
        /// <returns>Where the agent should next move</returns>
        protected Vector3 GetMoveTarget()
        {
            lastProbes.Clear();

            if (pathCompleted)
            {
                lastMoveTarget = entity.Position;
                return entity.Position;
            }

            if (waypoints.Count == 0)
            {
                pathCompleted = true;
                lastMoveTarget = currentTarget;
                return currentTarget;
            }

            Vector3 projectedPos = AINodeUtils.ProjectOnFloor(entity.Position, entity);

            // Advance past waypoints we're already within threshold of.
            while (pathProgress < waypoints.Count &&
                   Vector3.Distance(projectedPos, waypoints[pathProgress].position) < pathCompleteThreshold)
            {
                pathProgress++;
            }

            // Skip ahead to the furthest waypoint we can that ALSO connects onward to the following waypoint
            for (int skip = waypoints.Count - 1; skip > pathProgress; skip--)
            {
                var floorWaypoint = AINodeUtils.ProjectOnFloor(waypoints[skip].position, entity);
                float heightDifference = float.Abs(projectedPos.Y - floorWaypoint.Y);
                if (heightDifference > 0.1f)
                {
                    continue;
                }

                if (!AINodeUtils.IsPathWalkable(projectedPos, floorWaypoint, out _))
                {
                    continue;
                }

                Vector3 checkPoint = floorWaypoint;

                if (!IsSweepClear(entity, checkPoint, expansionFactor: 1.25f))
                {
                    continue;
                }

                if (skip < waypoints.Count - 1)
                {
                    Vector3 nextFloorWaypoint = AINodeUtils.ProjectOnFloor(waypoints[skip + 1].position, entity);
                    if (!AINodeUtils.IsPathWalkable(floorWaypoint, nextFloorWaypoint, out _))
                    {
                        continue;
                    }
                }

                pathProgress = skip;
                break;
            }

            if (pathProgress >= waypoints.Count)
            {
                pathCompleted = true;
                lastMoveTarget = currentTarget;
                return currentTarget;
            }

            Vector3 desiredTarget = waypoints[pathProgress].position;
            float baseAngle = MathF.Atan2(desiredTarget.X - entity.Position.X, desiredTarget.Z - entity.Position.Z);
            float distanceToWaypoint = Vector2.Distance(
                new Vector2(desiredTarget.X, desiredTarget.Z),
                new Vector2(entity.Position.X, entity.Position.Z));
            float moveDist = MathF.Min(distanceToWaypoint, 1f);

            Vector3 straightFwd = DirectionFromAngle(baseAngle);
            if (TryMoveAt(straightFwd, moveDist, distanceToWaypoint, out Vector3 straightMove, out _))
            {
                avoidAngleDeg = 0f;
                avoidCommitSteps = 0;
                lastMoveAvoided = false;
                lastMoveTarget = desiredTarget;
                return desiredTarget;
            }

            // Straight is blocked. If we already found a working avoid angle on a previous step, just keep using it
            if (avoidCommitSteps > 0 && avoidAngleDeg != 0f)
            {
                Vector3 committedFwd = DirectionFromAngle(baseAngle + MathHelper.ToRadians(avoidAngleDeg));
                if (TryMoveAt(committedFwd, moveDist, distanceToWaypoint, out Vector3 committedMove, out _))
                {
                    lastMoveAvoided = true;
                    avoidCommitSteps--;
                    lastMoveTarget = committedMove;
                    return committedMove;
                }
            }

            // Need a new avoid angle. Pick a turn sign and scan outward that same direction (Doom-style) until we
            // find one that's clear.
            avoidTurnSign = PickTurnSign(baseAngle);
            avoidAngleDeg = 0f;

            while (MathF.Abs(avoidAngleDeg) < 180f)
            {
                avoidAngleDeg += avoidTurnSign * AvoidTurnStepDeg;
                Vector3 fwd = DirectionFromAngle(baseAngle + MathHelper.ToRadians(avoidAngleDeg));

                if (TryMoveAt(fwd, moveDist, distanceToWaypoint, out Vector3 avoidMove, out _))
                {
                    lastMoveAvoided = true;
                    avoidCommitSteps = AvoidCommitSteps - 1;
                    lastMoveTarget = avoidMove;
                    return avoidMove;
                }
            }

            avoidAngleDeg = 0f;
            avoidCommitSteps = 0;
            lastMoveAvoided = true;
            lastMoveTarget = desiredTarget;

            // Last resort, just try going there anyway. Maybe we can free ourselves.
            return desiredTarget;
        }

        /// <summary>
        /// Advances the move-step timer by the frame's elapsed time and reports whether a move-step is due.
        /// </summary>
        protected bool ShouldTakeMoveStep()
        {
            moveStepAccumulator += PreviousFrameDelta;
            if (moveStepAccumulator < MoveStepIntervalSeconds)
            {
                return false;
            }

            // Subtract rather than reset to zero, so a slightly-over-length frame doesn't lose its remainder -
            // keeps the average cadence accurate instead of biasing slow over time.
            moveStepAccumulator -= MoveStepIntervalSeconds;
            return true;
        }

        private static void AddWireBox(List<VertexPositionColor> verts, Vector3 center, float halfX, float halfZ, float minY, float maxY, Color color)
        {
            Vector3 c000 = center + new Vector3(-halfX, minY, -halfZ);
            Vector3 c100 = center + new Vector3(halfX, minY, -halfZ);
            Vector3 c110 = center + new Vector3(halfX, minY, halfZ);
            Vector3 c010 = center + new Vector3(-halfX, minY, halfZ);
            Vector3 c001 = center + new Vector3(-halfX, maxY, -halfZ);
            Vector3 c101 = center + new Vector3(halfX, maxY, -halfZ);
            Vector3 c111 = center + new Vector3(halfX, maxY, halfZ);
            Vector3 c011 = center + new Vector3(-halfX, maxY, halfZ);

            void Line(Vector3 a, Vector3 b) { verts.Add(new VertexPositionColor(a, color)); verts.Add(new VertexPositionColor(b, color)); }

            Line(c000, c100); Line(c100, c110); Line(c110, c010); Line(c010, c000);
            Line(c001, c101); Line(c101, c111); Line(c111, c011); Line(c011, c001);
            Line(c000, c001); Line(c100, c101); Line(c110, c111); Line(c010, c011);
        }
        protected void DebugRender()
        {
            debugEffect ??= new BasicEffect(Instance.GraphicsDevice) { VertexColorEnabled = true };
            debugEffect.World = RenderEngine.WorldMatrix;
            debugEffect.View = RenderEngine.ViewMatrix;
            debugEffect.Projection = RenderEngine.ProjectionMatrix;

            var verts = new List<VertexPositionColor>();

            foreach (var probe in lastProbes)
            {
                Color color = probe.trusted ? Color.Cyan
                    : probe.chosen ? Color.Lime
                    : probe.blocked ? Color.Red
                    : Color.Gray;

                Vector3 start = entity.Position;
                Vector3 end = entity.Position + probe.direction * probe.distance;

                verts.Add(new VertexPositionColor(start, color));
                verts.Add(new VertexPositionColor(end, color));

                if (!probe.trusted)
                {
                    Vector3 boxCenter = probe.hitPoint;
                    float minY = probe.vertOffset - probe.halfExtents.Y;
                    float maxY = probe.vertOffset + probe.halfExtents.Y;
                    AddWireBox(verts, boxCenter, probe.halfExtents.X, probe.halfExtents.Z, minY, maxY, color);
                }
            }

            if (lastMoveTarget.HasValue)
            {
                Vector3 chosen = lastMoveTarget.Value;
                verts.Add(new VertexPositionColor(entity.Position, Color.White));
                verts.Add(new VertexPositionColor(chosen, Color.White));

                AddWireBox(verts, chosen, 0.08f, 0.08f, chosen.Y - 0.2f, chosen.Y + 0.2f, Color.White);
            }

            if (waypoints.Count > 0)
            {
                Vector3 finalPos = waypoints[^1].position;

                if (pathProgress < waypoints.Count)
                {
                    Vector3 prevPos = entity.Position;
                    for (int i = pathProgress; i < waypoints.Count; i++)
                    {
                        Vector3 waypointPos = waypoints[i].position;
                        Color lineColor = i == pathProgress ? Color.DarkGray : Color.SlateGray;

                        verts.Add(new VertexPositionColor(prevPos, lineColor));
                        verts.Add(new VertexPositionColor(waypointPos, lineColor));

                        bool isFinal = i == waypoints.Count - 1;
                        Color markerColor = isFinal ? Color.Orange : Color.Yellow;
                        float markerSize = isFinal ? 0.15f : 0.1f;

                        Vector3 waypointMarkerCenter = new Vector3(waypointPos.X, 0f, waypointPos.Z);
                        AddWireBox(verts, waypointMarkerCenter, markerSize, markerSize, waypointPos.Y - markerSize, waypointPos.Y + markerSize, markerColor);

                        prevPos = waypointPos;
                    }
                }
                else
                {
                    Vector3 finalMarkerCenter = new Vector3(finalPos.X, 0f, finalPos.Z);
                    AddWireBox(verts, finalMarkerCenter, 0.15f, 0.15f, finalPos.Y - 0.15f, finalPos.Y + 0.15f, Color.Orange);
                }
            }

            if (verts.Count == 0)
            {
                return;
            }

            debugBuffer?.Dispose();
            debugBuffer = new DynamicVertexBuffer(Instance.GraphicsDevice, typeof(VertexPositionColor), verts.Count, BufferUsage.WriteOnly);
            debugBuffer.SetData(verts.ToArray());
            Instance.GraphicsDevice.SetVertexBuffer(debugBuffer);
            foreach (var pass in debugEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                Instance.GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, verts.ToArray(), 0, verts.Count / 2);
            }
            Instance.GraphicsDevice.SetVertexBuffer(null);
        }



        private static Vector3 DirectionFromAngle(float angle) => new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));

        private bool ProbeHeading(Vector3 fwd, Span<DirectionProbe> outProbes, float maxdst, out int probeCount, out float clearDistance)
        {
            float curDistance = float.Min(maxdst, ObstacleLookaheadDistance);

            int steps = (int)MathF.Ceiling(curDistance / ObstacleLookaheadStep);
            if (steps > outProbes.Length)
            {
                steps = outProbes.Length;
            }

            bool allClear = true;
            bool foundBlocked = false;
            clearDistance = curDistance;

            float previousFloorHeight = AINodeUtils.ProjectOnFloor(entity.Position, entity).Y;

            for (int i = 1; i <= steps; i++)
            {
                float dist = MathF.Min(i * ObstacleLookaheadStep, curDistance);
                Vector3 stepTarget = entity.Position + fwd * dist;
                bool stepClear = IsSweepClear(entity, stepTarget, out Vector3 hit, out Vector3 extents, out float offset, out float stepFloorHeight);

                bool steppable = MathF.Abs(stepFloorHeight - previousFloorHeight) <= MaxStepHeight;
                stepClear &= steppable;
                previousFloorHeight = stepFloorHeight;

                outProbes[i - 1] = new DirectionProbe
                {
                    direction = fwd,
                    distance = dist,
                    blocked = !stepClear,
                    hitPoint = hit,
                    halfExtents = extents,
                    vertOffset = offset
                };

                if (!stepClear)
                {
                    allClear = false;
                    if (!foundBlocked)
                    {
                        clearDistance = dist;
                        foundBlocked = true;
                    }
                }
            }

            probeCount = steps;
            return allClear;
        }
        private bool TryMoveAt(Vector3 fwd, float moveDist, float probeDist, out Vector3 moveTarget, out float verifiedDistance)
        {
            Span<DirectionProbe> probes = stackalloc DirectionProbe[MaxProbeSteps];
            bool clear = ProbeHeading(fwd, probes, probeDist, out int probeCount, out float clearDistance);

            for (int i = 0; i < probeCount; i++)
            {
                var probe = probes[i];
                probe.chosen = clear;
                lastProbes.Add(probe);
            }

            moveTarget = entity.Position + fwd * moveDist;
            verifiedDistance = clearDistance;
            return clear;
        }
        private float PickTurnSign(float baseAngle)
        {
            Vector3 rightFwd = DirectionFromAngle(baseAngle + MathHelper.ToRadians(AvoidTurnStepDeg));
            Vector3 leftFwd = DirectionFromAngle(baseAngle - MathHelper.ToRadians(AvoidTurnStepDeg));

            Span<DirectionProbe> rightProbes = stackalloc DirectionProbe[MaxProbeSteps];
            Span<DirectionProbe> leftProbes = stackalloc DirectionProbe[MaxProbeSteps];

            bool rightClear = ProbeHeading(rightFwd, rightProbes, ObstacleLookaheadDistance, out int rightCount, out float rightDist);
            bool leftClear = ProbeHeading(leftFwd, leftProbes, ObstacleLookaheadDistance, out int leftCount, out float leftDist);

            for (int i = 0; i < rightCount; i++) lastProbes.Add(rightProbes[i]);
            for (int i = 0; i < leftCount; i++) lastProbes.Add(leftProbes[i]);

            if (rightClear && !leftClear)
            {
                return 1f;
            }

            if (leftClear && !rightClear)
            {
                return -1f;
            }

            return rightDist >= leftDist ? 1f : -1f;
        }

        private static (float halfX, float halfY, float halfZ, float vertCenter) ComputeSweepExtents(WorldEntity entity, float expansionFactor)
        {
            var realBounds = entity.GetRealBounds();
            var scale = entity.WorldScale;

            float halfX = (realBounds.Max.X - realBounds.Min.X) * scale.X * 0.5f * expansionFactor;
            float halfZ = (realBounds.Max.Z - realBounds.Min.Z) * scale.Z * 0.5f * expansionFactor;
            float halfY = (realBounds.Max.Y - realBounds.Min.Y) * scale.Y * 0.5f;
            float vertCenter = (realBounds.Min.Y + realBounds.Max.Y) * scale.Y * 0.5f;

            if (entity.PhysicsShape == WorldEntity.EntityPhysicsShapes.Cylinder)
            {
                float r = MathF.Max(halfX, halfZ);
                halfX = r; halfZ = r;
            }

            return (halfX, halfY, halfZ, vertCenter);
        }
        private static BoundingBox GetSweepBounds(WorldEntity entity, float expansionFactor, out Vector3 halfExtents, out float vertOffset)
        {
            var (halfX, halfY, halfZ, vertCenter) = ComputeSweepExtents(entity, expansionFactor);
            halfExtents = new Vector3(halfX, halfY, halfZ);
            vertOffset = vertCenter + GroundClearanceBias;

            var key = (MathF.Round(halfX, 3), MathF.Round(halfY, 3), MathF.Round(halfZ, 3), expansionFactor);
            if (sweepBoundsCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var localBounds = new BoundingBox(
                new Vector3(-halfX, vertOffset - halfY, -halfZ),
                new Vector3(halfX, vertOffset + halfY, halfZ));
            sweepBoundsCache[key] = localBounds;
            return localBounds;
        }
        private static float SampleFloorHeight(Vector3 center, float halfX, float halfZ, WorldEntity entity)
        {
            Span<Vector3> offsets = stackalloc Vector3[]
            {
                Vector3.Zero,
                new Vector3(halfX, 0, halfZ),
                new Vector3(halfX, 0, -halfZ),
                new Vector3(-halfX, 0, halfZ),
                new Vector3(-halfX, 0, -halfZ),
            };

            float maxHeight = float.NegativeInfinity;
            foreach (var offset in offsets)
            {
                float sampleY = AINodeUtils.ProjectOnFloor(center + offset, entity).Y;
                if (sampleY > maxHeight)
                {
                    maxHeight = sampleY;
                }
            }
            return maxHeight;
        }

        private static bool IsSweepClear(WorldEntity entity, Vector3 targetEntityPosition, out Vector3 hitPoint, out Vector3 usedHalfExtents, out float usedVertOffset, out float floorHeight, float expansionFactor = 1f)
        {
            var localBounds = GetSweepBounds(entity, expansionFactor, out Vector3 halfExtents, out float vertOffset);
            usedHalfExtents = halfExtents;
            usedVertOffset = vertOffset;

            floorHeight = SampleFloorHeight(targetEntityPosition, halfExtents.X, halfExtents.Z, entity);
            Vector3 testPosition = new Vector3(targetEntityPosition.X, floorHeight, targetEntityPosition.Z) - localBounds.Min.Y * Vector3.UnitY + new Vector3(0, 0.5f, 0);
            hitPoint = testPosition;

            bool blocked = Collision.CheckBounds(localBounds, testPosition, ignoreEntity: entity);
            return !blocked;
        }
        private static bool IsSweepClear(WorldEntity entity, Vector3 targetEntityPosition, out Vector3 hitPoint, out Vector3 usedHalfExtents, out float usedVertOffset, float expansionFactor = 1f)
            => IsSweepClear(entity, targetEntityPosition, out hitPoint, out usedHalfExtents, out usedVertOffset, out _, expansionFactor);
        private static bool IsSweepClear(WorldEntity entity, Vector3 targetEntityPosition, float expansionFactor = 1f)
            => IsSweepClear(entity, targetEntityPosition, out _, out _, out _, out _, expansionFactor);

    }
}