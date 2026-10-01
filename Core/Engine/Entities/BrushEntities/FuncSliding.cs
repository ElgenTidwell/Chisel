using Engine.Compilation;
using Engine.Physics;
using Engine.Utils;
using Microsoft.Xna.Framework;
using Rockwall;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Engine.Entities.BrushEntities;
[EntityDescriptor()]
[ExposeEntityProperty("Enable Use", EntityPropertyType.Bool, "Determines if this sliding entity can be activated by the 'Use' input. (eg. players)", defaultValue = "0")]
[ExposeEntityProperty("Move Direction", EntityPropertyType.Direction, "The direction this moves when interacted (0,1,0 is up, along the Y axis).", defaultValue="0,1,0")]
[ExposeEntityProperty("Move Amount", EntityPropertyType.Float, "How far to move along the direction.", defaultValue = "0")]
[ExposeEntityProperty("Move Speed", EntityPropertyType.Float, "How fast to move along the direction.", defaultValue = "0")]
[ExposeEntityProperty("Wait Time", EntityPropertyType.Float, "How long to wait before returning to the start position. -1 means stay indefinitely.", defaultValue = "-1")]
[RegisterEntityInputs("Use")]
[RegisterEntityInputs("BeginMovement", "MoveOpen", "MoveShut")]
[RegisterEntityOutputs("OnBeginMove", "OnEndMove")]
[EntityVisualize(typeof(ArrowVisualizer))]
[VisualizerProperty(nameof(ArrowVisualizer.Direction), "Move Direction")]
[VisualizerProperty(nameof(ArrowVisualizer.Length), "Move Amount")]
public class FuncSliding : BrushEntity
{
    public FuncSliding()
    {
        IsSimulated = true;
        IsKinematic = true;
        AxisAlignedBox = true;
        Controller = new FuncSlidingController();
        ignoreWorldCollision = true;
        PhysicsDensityMultiplier = 4;
        RegisterInputLocally("Use", (s, e) => { if ((bool)ReadProperty("Enable Use", EntityPropertyType.Bool)) ((FuncSlidingController)Controller).OnInteract(e); });
        RegisterInputLocally("BeginMovement", (s, e) => ((FuncSlidingController)Controller).OnInteract(e));
        RegisterInputLocally("MoveOpen", (s, e) => ((FuncSlidingController)Controller).Move(true));
        RegisterInputLocally("MoveShut", (s, e) => ((FuncSlidingController)Controller).Move(false));
    }
}
public class FuncSlidingController : EntityController
{
    Vector3 startPosition;
    Vector3 direction;
    float moveAmount;
    float moveSpeed;
    bool moveState;
    bool prevMove;

    float maxWaitTime;
    float waitTime;
    bool waiting;

    public override void OnDespawn()
    {
    }

    public override void OnRender(GameTime gameTime)
    {
    }

    public override void OnSpawn()
    {
        startPosition = entity.Position;
        direction = Vector3.Normalize((Vector3)entity.ReadProperty("Move Direction", EntityPropertyType.Direction));
        moveSpeed = (float)entity.ReadProperty("Move Speed", EntityPropertyType.Float);
        moveAmount = (float)entity.ReadProperty("Move Amount", EntityPropertyType.Float);
        maxWaitTime = (float)entity.ReadProperty("Wait Time", EntityPropertyType.Float);
    }
    public override void OnUpdate(GameTime gameTime)
    {
        if (!waiting && entity.Velocity.LengthSquared() > 0.5f && WouldCrush())
        {
            entity.Velocity = Vector3.Zero;
            moveState = !moveState;
            return;
        }

        var toCurrent = entity.Position - startPosition;

        float d = Vector3.Dot(toCurrent, direction);
        d = float.Clamp(d, 0, moveAmount);

        entity.Position = startPosition + direction * d;

        var target = startPosition + direction * (moveState ? 1 : 0) * moveAmount;

        if (waiting && maxWaitTime > 0)
        {
            waitTime -= MainEngine.PreviousFrameDelta;

            if (waitTime <= 0)
            {
                waiting = false;
                OnInteract(entity);
                var dim = entity.Bounds.Max - entity.Bounds.Min;
                var entities = Collision.GetEntitiesInSphere(entity.Position, float.Max(float.Max(dim.X, dim.Y), dim.Z) * 2);
                foreach (var ent in entities) PhysicsEngine.BodyInterface.ActivateBody(ent.PhysicsBodyID);
            }
        }

        if (Vector3.Distance(target, entity.Position) < 0.1f && !waiting && moveState)
        {
            waitTime = maxWaitTime;
            waiting = true;

            entity.CallOutput("OnEndMove", entity);

            entity.Position = startPosition + direction * d;
        }

        var dir = (target - entity.Position) * 10;
        var length = dir.Length();
        if (length > 1f)
            dir /= length;

        entity.Velocity = dir * moveSpeed;
    }
    private bool WouldCrush()
    {
        if (!PhysicsEngine.ContactInfo.TryGetValue(entity.PhysicsBodyID, out var doorContacts))
            return false;

        foreach (var (_, (_, _, pushedBodyID)) in doorContacts)
        {
            // Only care about contacts with dynamic entities, not world geometry
            if (!PhysicsEngine.BodyMapper.TryGetValue(pushedBodyID, out var pushedEntity)) continue;
            if (pushedEntity is BrushEntity) continue;

            // Check if the pushed entity has a world geometry contact roughly
            // aligned with our movement direction
            if (!PhysicsEngine.ContactInfo.TryGetValue(pushedBodyID, out var entityContacts)) continue;

            foreach (var (_, (contactNormal, _, contactOther)) in entityContacts)
            {
                if (contactOther == entity.PhysicsBodyID) continue; // skip the door itself

                // World geometry has no BodyMapper entry
                if (PhysicsEngine.BodyMapper.ContainsKey(contactOther)) continue;

                // Abs because contact normal sign depends on body ordering in the manifold
                if (float.Abs(Vector3.Dot(contactNormal, direction)) > 0.5f)
                    return true;
            }
        }

        return false;
    }
    public void OnInteract(WorldEntity e)
    {
        moveState = !moveState;
        waitTime = 0;
        waiting = false;

        entity.CallOutput("OnBeginMove", entity);
    }
    internal void Move(bool dir)
    {
        if (dir == moveState) return;

        moveState = dir;
        waitTime = 0;
        waiting = false;

        entity.CallOutput("OnBeginMove", entity);
    }
    public override CustomSaveData CaptureCustomData()
    {
        CustomSaveData d = new CustomSaveData();

        d.vals = new Dictionary<string, object>()
        {
            { "posX", entity.Position.X },
            { "posY", entity.Position.Y },
            { "posZ", entity.Position.Z },
            { "startPosX", startPosition.X },
            { "startPosY", startPosition.Y },
            { "startPosZ", startPosition.Z },
            { "dirX", direction.X },
            { "dirY", direction.Y },
            { "dirZ", direction.Z },
            { "moveState", moveState },
            { "waitTime", waitTime },
            { "waiting", waiting },
        };

        return d;
    }

    public override void RestoreCustomData(CustomSaveData? o)
    {
        if (!o.HasValue) return;

        // Otherwise it gets reset for whatever reason. Prob something to do with load order
        entity.Position.X = (float)Convert.ChangeType(o.Value.vals["posX"], typeof(float));
        entity.Position.Y = (float)Convert.ChangeType(o.Value.vals["posY"], typeof(float));
        entity.Position.Z = (float)Convert.ChangeType(o.Value.vals["posZ"], typeof(float));

        startPosition.X = (float)Convert.ChangeType(o.Value.vals["startPosX"], typeof(float));
        startPosition.Y = (float)Convert.ChangeType(o.Value.vals["startPosY"], typeof(float));
        startPosition.Z = (float)Convert.ChangeType(o.Value.vals["startPosZ"], typeof(float));

        direction.X = (float)Convert.ChangeType(o.Value.vals["dirX"], typeof(float));
        direction.Y = (float)Convert.ChangeType(o.Value.vals["dirY"], typeof(float));
        direction.Z = (float)Convert.ChangeType(o.Value.vals["dirZ"], typeof(float));

        waitTime = (float)Convert.ChangeType(o.Value.vals["waitTime"], typeof(float));

        moveState = (bool)o.Value.vals["moveState"];
        waiting = (bool)o.Value.vals["waiting"];
    }
}
