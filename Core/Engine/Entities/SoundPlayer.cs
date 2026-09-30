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
        private SoundInstance soundInstance;
        public override void OnDespawn()
        {
        }
        public override void OnSpawn()
        {
            SetSound(entity.ReadProperty("Sound Name", Rockwall.EntityPropertyType.String) as string);
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
            soundInstance = SoundScriptManager.PlaySound(soundName, entity.Position);
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
