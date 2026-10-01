using Chisel.Utils;
using Engine.Compilation;
using Engine.Console;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Engine.Entities;

[EntityDescriptor()]
[ExposeEntityProperty("Start With Control", Rockwall.EntityPropertyType.Bool, "Will have control by default when spawned.")]
[ExposeEntityProperty("Camera Priority", Rockwall.EntityPropertyType.Float, "Higher priority cameras will take control above lower priority ones.")]
[ExposeEntityPropertyTarget("Track Target")]
[RegisterEntityInputs("BeginTrack", "EnableControl", "DisableControl")]
[RegisterEntityOutputs("OnTrackComplete")]
public class CameraDirector : WorldEntity
{
    public CameraDirector()
    {
        var ctrl = new CameraDirectorController();
        Controller = ctrl;
        IgnoreCollision = true;
        IsSimulated = false;

        Bounds = new BoundingBox(-Vector3.One * 0.5f, Vector3.One * 0.5f);

        RegisterInputLocally("BeginTrack", (a, b) => { ctrl.StartTrack(); });
        RegisterInputLocally("EnableControl", (a, b) => { ctrl.EnableControl(); });
        RegisterInputLocally("DisableControl", (a, b) => { ctrl.DisableControl(); });
    }
    private class CameraDirectorController : CameraController
    {
        private InfoPath rootPath;
        private bool control;
        private int priority;

        private InfoPath prevTarg;
        private InfoPath currentTarg;
        private InfoPath nextTarg;
        private InfoPath nextNextTarg;

        private float segmentT;
        private float segmentDuration;

        public override void OnSpawn()
        {
            control = (bool)entity.ReadProperty("Start With Control", Rockwall.EntityPropertyType.Bool);
            priority = (int)(float)entity.ReadProperty("Camera Priority", Rockwall.EntityPropertyType.Float);
        }

        public override void OnAllEntitiesSpawned()
        {
            var str = entity.ReadProperty("Track Target", Rockwall.EntityPropertyType.String);

            if (str == null || string.IsNullOrEmpty((string)str)) return;

            var ent = EntityManager.FindSingleEntityByName(str as string);

            if (ent is not InfoPath path)
            {
                Logger.AppendInfo("Camera director track target was not an InfoPath.");

                return;
            }

            rootPath = path;
        }

        public override void OnUpdate(GameTime gameTime)
        {
            if (!control) return;

            base.OnUpdate(gameTime);
            RequestCameraControl((uint)priority);

            if (rootPath == null || currentTarg == null) return;

            if (nextTarg == null)
            {
                currentTarg = null;

                entity.CallOutput("OnTrackComplete", entity);

                return;
            }

            segmentT += segmentDuration > 0f ? MainEngine.PreviousFrameDelta / segmentDuration : 1f;
            segmentT = MathHelper.Clamp(segmentT, 0f, 1f);

            Vector3 targPos = Vector3.Zero;
            Quaternion targRot;

            if (currentTarg.PathController.MoveSmooth)
            {
                Quaternion r0 = prevTarg?.Rotation ?? currentTarg.Rotation;
                Quaternion r3 = nextNextTarg?.Rotation ?? nextTarg.Rotation;

                targRot = CMath.SquadRotation(r0, currentTarg.Rotation, nextTarg.Rotation, r3, segmentT);

                Vector3 p0 = prevTarg?.Position ?? currentTarg.Position;
                Vector3 p3 = nextNextTarg?.Position ?? nextTarg.Position;

                targPos = CMath.CatmullRomCentripetal(p0, currentTarg.Position, nextTarg.Position, p3, segmentT);
            }
            else
            {
                targRot = Quaternion.Slerp(currentTarg.Rotation, nextTarg.Rotation, segmentT);
                targPos = Vector3.Lerp(currentTarg.Position, nextTarg.Position, segmentT);
            }

            entity.Position = targPos;
            entity.Rotation = targRot;

            if (segmentT >= 1f)
            {
                currentTarg.CallOutput("OnPassed",entity);

                prevTarg = currentTarg;
                currentTarg = nextTarg;
                nextTarg = nextNextTarg;
                nextNextTarg = nextTarg?.GetNext();

                BeginSegment();
            }
        }

        internal void StartTrack()
        {
            prevTarg = null;
            currentTarg = rootPath;
            nextTarg = currentTarg?.GetNext();
            nextNextTarg = nextTarg?.GetNext();

            BeginSegment();
        }
        internal void EnableControl()
        {
            control = true;

            if (rootPath != null)
            {
                entity.Position = rootPath.Position;
                entity.Rotation = rootPath.Rotation;
            }

            RebuildMatrix();
        }
        internal void DisableControl()
        {
            control = false;
        }

        private void BeginSegment()
        {
            segmentT = 0f;

            if (currentTarg == null || nextTarg == null)
            {
                segmentDuration = 0f;

                return;
            }

            float dst = Vector3.Distance(currentTarg.Position, nextTarg.Position);
            float speed = currentTarg.PathController.MoveSpeed;

            segmentDuration = speed > 0f ? dst / speed : 0f;
        }

    }
}