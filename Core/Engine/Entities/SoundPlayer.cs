using Engine.Compilation;
using Engine.Scripting.Sound;
using Engine.Sound;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Engine.Entities;
[EntityDescriptor]
[ExposeEntityProperty("Sound Name", Rockwall.EntityPropertyType.String, "Either the name of a soundscript, or a path to a sound file.")]
[ExposeEntityPropertyTarget("Position Override", "Blank for none. Set to an entity to use that entity's position as the origin for the sound.")]
[ExposeEntityPropertyEnum("3D Override", "How 3D this sound should be. No Override will use the setting set by the soundscript.","General", "No Override", "Force 3D", "Force Non-3D")]
[RegisterEntityInputs("Play","SetSound","Stop")]
public class SoundPlayer : WorldEntity
{
    public SoundPlayer()
    {
        IsSimulated = false;
        IgnoreCollision = true;
        var soundPlayer = new SoundPlayerController();
        Controller = soundPlayer;

        RegisterInputLocally("Play", (a, b) => soundPlayer.Play());
        RegisterInputLocally("SetSound", (a, b) => soundPlayer.SetSound(a));
        RegisterInputLocally("Stop", (a, b) => soundPlayer.Stop());
    }

    private class SoundPlayerController : EntityController
    {
        private string soundName;
        private Vector3? soundOrigin;
        private bool? force3D;
        private SoundInstance soundInstance;
        public override void OnDespawn()
        {
        }
        public override void OnSpawn()
        {
            SetSound(entity.ReadProperty("Sound Name", Rockwall.EntityPropertyType.String) as string);

            var ent = EntityManager.FindSingleEntityByName(entity.ReadProperty("Position Override", Rockwall.EntityPropertyType.String) as string);
            soundOrigin = ent?.Position ?? null;

            var enumval = (entity.ReadProperty("3D Override", Rockwall.EntityPropertyType.String) as string);

            switch(enumval)
            {
                default: break;

                case "Force 3D":
                    force3D = true;
                    break;
                case "Force Non-3D":
                    force3D = false;
                    break;
            }
        }
        public override void OnRender(GameTime gameTime)
        {
        }
        public override void OnUpdate(GameTime gameTime)
        {
        }

        internal void Play()
        {
            soundInstance?.Stop();
            soundInstance = SoundScriptManager.PlaySound(soundName, soundOrigin ?? entity.Position, is3DOverride: force3D);
        }
        internal void Stop()
        {
            soundInstance?.Stop();
        }
        internal void SetSound(string sound)
        {
            soundName = sound;
        }
    }
}
