using Engine.Console;
using Engine.Utils;
using Engine.Utils.Settings;
using Microsoft.Xna.Framework;
using Rockwall;
using Silk.NET.OpenAL;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static OpenTK.Audio.OpenAL.ALC;

namespace Engine.Sound;
public enum SoundCategory
{
    SFX,
    Music,
    Master
}
public unsafe class SoundDevice : IDisposable
{
    class Voice
    {
        public uint Id;
        public float[] Samples;
        public int Channels;
        public double Position;
        public float Gain;
        public float Pitch;
        public bool Is3D;
        public Vector3 SoundPos;
        public float MinDist, MaxDist, Rolloff;
        public bool Looping;
        public bool Finished;
        public SoundCategory Category;
        public float LowpassState;
        public int ReverbZone;
    }
    class SoundClip
    {
        public float[] Samples;
        public int Channels;
    }
    enum SoundCommandKind
    {
        Play,
        Stop,
        Update,
        Reset
    }
    struct SoundCommand
    {
        public SoundCommandKind Kind;
        public uint VoiceId;
        public uint BufferId;
        public Vector3 Position;
        public bool Loop;
        public float Gain;
        public float Pitch;
        public bool Disable3D;
        public float MinDist;
        public float MaxDist;
        public float Rolloff;
        public SoundCategory Category;
        public Vector3? NewPosition;
        public Vector3? NewVelocity;
        public float? NewGain;
    }
    class CombFilter
    {
        float[] buffer;
        int index;
        float feedback;
        float damping;
        float filterState;

        public CombFilter(int delaySamples, float feedback, float damping)
        {
            buffer = new float[Math.Max(8, delaySamples)];
            this.feedback = feedback;
            this.damping = damping;
        }

        public float Process(float input)
        {
            float output = buffer[index];
            filterState += (1f - damping) * (output - filterState);
            buffer[index] = input + filterState * feedback;
            index++;
            if (index >= buffer.Length)
            {
                index = 0;
            }
            return output;
        }
    }
    class AllpassFilter
    {
        float[] buffer;
        int index;
        float feedback;

        public AllpassFilter(int delaySamples, float feedback)
        {
            buffer = new float[Math.Max(8, delaySamples)];
            this.feedback = feedback;
        }

        public float Process(float input)
        {
            float bufOut = buffer[index];
            float output = -input + bufOut;
            buffer[index] = input + bufOut * feedback;
            index++;
            if (index >= buffer.Length)
            {
                index = 0;
            }
            return output;
        }
    }
    class ReverbProcessor
    {
        CombFilter[] combs;
        AllpassFilter[] allpasses;
        public ReverbProcessor(float decayTime, float diffusion, float gainHF, float sizeScale, int combCount, int allpassCount)
        {
            int[] combBaseDelays = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
            int[] allpassBaseDelays = { 556, 441, 341, 225 };

            combCount = MathHelper.Clamp(combCount, 2, combBaseDelays.Length);
            allpassCount = MathHelper.Clamp(allpassCount, 1, allpassBaseDelays.Length);

            float damping = 1f - MathHelper.Clamp(gainHF, 0.05f, 1f);
            float allpassFeedback = MathHelper.Lerp(0.3f, 0.7f, MathHelper.Clamp(diffusion, 0f, 1f));

            combs = new CombFilter[combCount];
            for (int i = 0; i < combCount; i++)
            {
                int sourceIndex = (i * combBaseDelays.Length) / combCount;
                int delaySamples = (int)(combBaseDelays[sourceIndex] * sizeScale);
                float combFeedback = MathF.Exp(-6.9077553f * delaySamples / (mixSampleRate * decayTime));
                combs[i] = new CombFilter(delaySamples, combFeedback, damping);
            }

            allpasses = new AllpassFilter[allpassCount];
            for (int i = 0; i < allpassCount; i++)
            {
                int sourceIndex = (i * allpassBaseDelays.Length) / allpassCount;
                int delaySamples = (int)(allpassBaseDelays[sourceIndex] * sizeScale);
                allpasses[i] = new AllpassFilter(delaySamples, allpassFeedback);
            }
        }

        public float Process(float input)
        {
            float combSum = 0f;
            foreach (var comb in combs)
            {
                combSum += comb.Process(input);
            }
            combSum /= combs.Length;

            float output = combSum;
            foreach (var allpass in allpasses)
            {
                output = allpass.Process(output);
            }

            return output;
        }
    }
    class EchoProcessor
    {
        float[] buffer;
        int writeIndex;
        float feedback;
        float damping;
        float filterState;

        public EchoProcessor(float delaySeconds, float feedback, float damping)
        {
            buffer = new float[Math.Max(1, (int)(delaySeconds * mixSampleRate))];
            this.feedback = feedback;
            this.damping = damping;
        }

        public float Process(float input)
        {
            float delayed = buffer[writeIndex];

            filterState += (1f - damping) * (delayed - filterState);

            buffer[writeIndex] = input + filterState * feedback;
            writeIndex++;
            if (writeIndex >= buffer.Length)
            {
                writeIndex = 0;
            }

            return delayed;
        }
    }

    public Dictionary<SoundCategory, float> SoundLevels = Enum.GetValues<SoundCategory>().ToDictionary(c => c, f => 1.0f);

    /// <summary>
    /// The instance of the sound device.
    /// </summary>
    public static SoundDevice Device { get; private set; }
    public static CVarInt MaxSources { get; private set; } = new CVarInt("s_maxsource", 256);

    public int reverbCombCount = 6;
    public int reverbAllpassCount = 3;

    public Vector3 ListenerPosition;
    public Vector3 ListenerVelocity;
    public Vector3 CameraForward;
    public Vector3 CameraUp;
    public Vector3 CameraPosition;

    public float TimeScale = 1f;
    public HashSet<SoundCategory> TimescaleAffected = new HashSet<SoundCategory> { SoundCategory.SFX };

    bool disposedValue;

    Context* context;
    Device* device;

    AL al;
    ALContext alc;

    Dictionary<uint, Voice> voices = new Dictionary<uint, Voice>();
    uint nextVoiceId = 1;
    ConcurrentDictionary<uint, byte> activeVoiceIds = new ConcurrentDictionary<uint, byte>();

    List<SoundClip> clipSamples = new List<SoundClip>();
    Dictionary<string, uint> cachedSounds = new Dictionary<string, uint>();

    const int mixSampleRate = 44100;
    const int mixChunkFrames = 1024;
    const int mixBufferCount = 4;
    uint mixerSource;
    uint[] mixBuffers;
    float[] mixAccumulate = new float[mixChunkFrames * 2];
    float[] echoSendAccum = new float[mixChunkFrames];
    float[] reverbOutScratch = new float[mixChunkFrames];
    byte[] pcmScratch;
    uint[] unqueueScratch = new uint[1];

    AcousticZoneCache acousticZones = new AcousticZoneCache();
    float acousticClock;

    Dictionary<int, ReverbProcessor> zoneReverbs = new Dictionary<int, ReverbProcessor>();
    Dictionary<int, float> zoneWetness = new Dictionary<int, float>();
    Dictionary<int, float[]> zoneSendScratch = new Dictionary<int, float[]>();
    Dictionary<int, float> zoneLastActive = new Dictionary<int, float>();
    const float zoneReverbIdleTimeout = 8f;

    EchoProcessor distantEcho;
    public float distanceEchoNear = 15f;
    public float distanceEchoFar = 60f;
    public float distanceEchoMaxSend = 0.6f;
    public float echoDelaySeconds = 0.35f;
    public float echoFeedback = 0.4f;
    public float echoDamping = 0.5f;
    public float reverbOutputGain = 0.5f;
    public float echoOutputGain = 0.7f;

    ConcurrentQueue<SoundCommand> commandQueue = new ConcurrentQueue<SoundCommand>();
    int nextVoiceIdRaw;

    Thread audioThread;
    volatile bool audioThreadRunning;
    Stopwatch audioClock = new Stopwatch();
    public int audioThreadSleepMs = 5;

    public static void CreateListedOptions()
    {
        GameSettings.RegisterOption(OptionsTab.Audio, new SliderOption
        {
            OptionLabel = "Master Volume",
            GameOption = "masterVol",
            Min = 0,
            Max = 100,
            SmallChange = 1,
            GetCurrentValue = () => (double)GameSettings.Settings["masterVol"] * 100.0,
            OnChanged = val =>
            {
                GameSettings.Settings["masterVol"] = val / 100.0;
                Device.SoundLevels[SoundCategory.Master] = (float)(val / 100f);
            }
        });
        GameSettings.RegisterOption(OptionsTab.Audio, new SliderOption
        {
            OptionLabel = "Music Volume",
            GameOption = "musicVol",
            Min = 0,
            Max = 100,
            SmallChange = 1,
            GetCurrentValue = () => (double)GameSettings.Settings["musicVol"] * 100.0,
            OnChanged = val =>
            {
                GameSettings.Settings["musicVol"] = val / 100.0;
                Device.SoundLevels[SoundCategory.Music] = (float)(val / 100f);
            }
        });
        GameSettings.RegisterOption(OptionsTab.Audio, new SliderOption
        {
            OptionLabel = "Sound Volume",
            GameOption = "sfxVol",
            Min = 0,
            Max = 100,
            SmallChange = 1,
            GetCurrentValue = () => (double)GameSettings.Settings["sfxVol"] * 100.0,
            OnChanged = val =>
            {
                GameSettings.Settings["sfxVol"] = val / 100.0;
                Device.SoundLevels[SoundCategory.SFX] = (float)(val / 100f);
            }
        });
    }

    public static void Initialize()
    {
        Device ??= new SoundDevice();
    }

    public SoundDevice()
    {
        al = AL.GetApi();
        alc = ALContext.GetApi();
        device = alc.OpenDevice(null);
        context = alc.CreateContext(device, null);

        alc.MakeContextCurrent(context);

        distantEcho = new EchoProcessor(echoDelaySeconds, echoFeedback, echoDamping);

        InitMixer();
        StartAudioThread();
    }

    void InitMixer()
    {
        mixerSource = al.GenSource();
        al.SetSourceProperty(mixerSource, SourceBoolean.SourceRelative, true);
        al.SetSourceProperty(mixerSource, SourceVector3.Position, 0, 0, 0);
        al.SetSourceProperty(mixerSource, SourceFloat.Gain, 1f);

        mixBuffers = new uint[mixBufferCount];
        for (int i = 0; i < mixBufferCount; i++)
        {
            mixBuffers[i] = al.GenBuffer();
            MixChunk(mixBuffers[i]);
        }

        al.SourceQueueBuffers(mixerSource, mixBuffers);
        al.SourcePlay(mixerSource);
    }

    void StartAudioThread()
    {
        audioClock.Start();
        audioThreadRunning = true;
        audioThread = new Thread(AudioThreadLoop)
        {
            IsBackground = true,
            Name = "SoundMixer",
            Priority = ThreadPriority.AboveNormal
        };
        audioThread.Start();
    }

    void AudioThreadLoop()
    {
        alc.MakeContextCurrent(context);

        double lastTime = audioClock.Elapsed.TotalSeconds;

        while (audioThreadRunning)
        {
            double now = audioClock.Elapsed.TotalSeconds;
            float delta = (float)(now - lastTime);
            lastTime = now;

            acousticClock += delta;

            ProcessCommandQueue();

            acousticZones.SweepExpired(acousticClock);
            SweepIdleZoneReverbs();
            RemoveFinishedVoices();

            UpdateMixer();

            Thread.Sleep(audioThreadSleepMs);
        }
    }

    public bool IsVoicePlaying(uint id) => activeVoiceIds.ContainsKey(id);

    void RemoveFinishedVoices()
    {
        List<uint> finishedIds = null;
        foreach (var kv in voices)
        {
            if (kv.Value.Finished && !kv.Value.Looping)
            {
                (finishedIds ??= new List<uint>()).Add(kv.Key);
            }
        }
        if (finishedIds != null)
        {
            foreach (uint id in finishedIds)
            {
                if (voices.TryGetValue(id, out Voice voice) && voice.Is3D)
                {
                    acousticZones.Exit((int)id, acousticClock);
                }
                voices.Remove(id);
                activeVoiceIds.TryRemove(id, out _);
            }
        }
    }

    void ProcessCommandQueue()
    {
        while (commandQueue.TryDequeue(out SoundCommand command))
        {
            switch (command.Kind)
            {
                case SoundCommandKind.Play:
                    ProcessPlayCommand(ref command);
                    break;
                case SoundCommandKind.Stop:
                    ProcessStopCommand(command.VoiceId);
                    break;
                case SoundCommandKind.Update:
                    ProcessUpdateCommand(ref command);
                    break;
                case SoundCommandKind.Reset:
                    acousticZones.Clear();
                    break;
            }
        }
    }

    void ProcessPlayCommand(ref SoundCommand command)
    {
        if (command.BufferId >= (uint)clipSamples.Count) return;
        if (!command.Disable3D && Vector3.Distance(command.Position, ListenerPosition) > command.MaxDist) return;

        if (voices.Count >= MaxSources)
        {
            uint? oldestNonMusic = null;
            foreach (var kv in voices)
            {
                if (kv.Value.Category != SoundCategory.Music)
                {
                    if (oldestNonMusic == null || kv.Key < oldestNonMusic.Value)
                    {
                        oldestNonMusic = kv.Key;
                    }
                }
            }
            if (oldestNonMusic != null)
            {
                voices.Remove(oldestNonMusic.Value);
                activeVoiceIds.TryRemove(oldestNonMusic.Value, out _);
            }
        }

        SoundClip clip = clipSamples[(int)command.BufferId];

        int reverbZone = -1;
        if (!command.Disable3D)
        {
            AcousticPreset zonePreset = acousticZones.Enter((int)command.VoiceId, command.Position, acousticClock, out int zoneKey, out _);
            GetOrCreateZoneReverb(zoneKey, zonePreset);
            reverbZone = zoneKey;
        }

        voices[command.VoiceId] = new Voice
        {
            Id = command.VoiceId,
            Samples = clip.Samples,
            Channels = clip.Channels,
            Position = 0,
            Gain = command.Gain,
            Pitch = command.Pitch,
            Is3D = !command.Disable3D,
            SoundPos = command.Position,
            MinDist = command.MinDist,
            MaxDist = command.MaxDist,
            Rolloff = command.Rolloff,
            Looping = command.Loop,
            Finished = false,
            Category = command.Category,
            ReverbZone = reverbZone
        };
        activeVoiceIds[command.VoiceId] = 0;
    }

    void ProcessStopCommand(uint id)
    {
        if (voices.TryGetValue(id, out Voice voice) && voice.Is3D)
        {
            acousticZones.Exit((int)id, acousticClock);
        }
        voices.Remove(id);
    }

    void ProcessUpdateCommand(ref SoundCommand command)
    {
        if (!voices.TryGetValue(command.VoiceId, out Voice voice)) return;

        if (command.NewPosition != null) voice.SoundPos = command.NewPosition.Value;
        if (command.NewGain != null) voice.Gain = command.NewGain.Value;
    }

    void UpdateMixer()
    {
        int processed = 0;
        int* processedPtr = &processed;
        al.GetSourceProperty(mixerSource, GetSourceInteger.BuffersProcessed, processedPtr);

        for (int i = 0; i < processed; i++)
        {
            al.SourceUnqueueBuffers(mixerSource, unqueueScratch);
            MixChunk(unqueueScratch[0]);
            al.SourceQueueBuffers(mixerSource, unqueueScratch);
        }

        int state = 0;
        int* statePtr = &state;
        al.GetSourceProperty(mixerSource, GetSourceInteger.SourceState, statePtr);
        if ((SourceState)state != SourceState.Playing)
        {
            al.SourcePlay(mixerSource);
        }
    }

    ReverbProcessor BuildReverb(AcousticPreset preset)
    {
        float sizeScale = MathHelper.Clamp(preset.ReferenceSize / 18f, 0.3f, 4f);
        float decayTime = MathHelper.Clamp(preset.DecayTime, 0.2f, 10f);
        float diffusion = MathHelper.Clamp(preset.Diffusion, 0f, 0.9f);
        float gainHF = MathHelper.Clamp(preset.GainHF, 0.05f, 1f);

        return new ReverbProcessor(decayTime, diffusion, gainHF, sizeScale, reverbCombCount, reverbAllpassCount);
    }

    void GetOrCreateZoneReverb(int zoneKey, AcousticPreset preset)
    {
        if (!zoneReverbs.ContainsKey(zoneKey))
        {
            zoneReverbs[zoneKey] = BuildReverb(preset);
            zoneWetness[zoneKey] = MathHelper.Clamp(preset.WetSend, 0f, 1f);
            zoneSendScratch[zoneKey] = new float[mixChunkFrames];
        }
        zoneLastActive[zoneKey] = acousticClock;
    }

    void SweepIdleZoneReverbs()
    {
        List<int> expired = null;
        foreach (var kv in zoneLastActive)
        {
            if (acousticClock - kv.Value > zoneReverbIdleTimeout)
            {
                (expired ??= new List<int>()).Add(kv.Key);
            }
        }
        if (expired != null)
        {
            foreach (int key in expired)
            {
                zoneReverbs.Remove(key);
                zoneWetness.Remove(key);
                zoneSendScratch.Remove(key);
                zoneLastActive.Remove(key);
            }
        }
    }

    void MixChunk(uint bufferId)
    {
        Array.Clear(mixAccumulate, 0, mixAccumulate.Length);
        Array.Clear(echoSendAccum, 0, echoSendAccum.Length);
        foreach (var scratch in zoneSendScratch.Values)
        {
            Array.Clear(scratch, 0, scratch.Length);
        }

        Vector3 right = Vector3.Normalize(Vector3.Cross(CameraForward, CameraUp));

        foreach (var voice in voices.Values)
        {
            if (voice.Finished) continue;

            float gain = voice.Gain * SoundLevels[voice.Category];
            float voicePitchScale = TimescaleAffected.Contains(voice.Category) ? TimeScale : 1f;
            if (voicePitchScale <= 0f) continue; // fully paused

            float leftGain = gain;
            float rightGain = gain;
            float distGain = 1f;

            float dist = 0f;
            bool occluded = false;
            if (voice.Is3D)
            {
                dist = Vector3.Distance(voice.SoundPos, ListenerPosition);

                float clampedDist = MathHelper.Clamp(dist, voice.MinDist, voice.MaxDist);
                distGain = voice.MinDist / (voice.MinDist + voice.Rolloff * (clampedDist - voice.MinDist));
                distGain = MathHelper.Clamp(distGain, 0f, 1f);

                Vector3 dir = dist > 0.0001f ? (voice.SoundPos - ListenerPosition) / dist : Vector3.Zero;
                float pan = MathHelper.Clamp(Vector3.Dot(dir, right), -1f, 1f);
                float panAngle = (pan + 1f) * MathF.PI / 4f;

                leftGain = gain * distGain * MathF.Cos(panAngle);
                rightGain = gain * distGain * MathF.Sin(panAngle);

                zoneLastActive[voice.ReverbZone] = acousticClock;
            }

            float lowpassAlpha = MathHelper.Lerp(0.4f, 1f, distGain);
            if (occluded) lowpassAlpha *= 0.1f;

            float echoT = voice.Is3D ? MathHelper.Clamp((dist - distanceEchoNear) / (distanceEchoFar - distanceEchoNear), 0f, 1f) : 0f;
            float echoSend = echoT * distanceEchoMaxSend * distGain;

            float[] zoneScratch = null;
            float voiceWetness = 0f;
            if (voice.Is3D && zoneSendScratch.TryGetValue(voice.ReverbZone, out var scratch))
            {
                zoneScratch = scratch;
                zoneWetness.TryGetValue(voice.ReverbZone, out voiceWetness);
            }
            float reverbSend = distGain * voiceWetness;

            for (int frame = 0; frame < mixChunkFrames; frame++)
            {
                int sampleIndex = (int)voice.Position;
                int frameCount = voice.Channels == 2 ? voice.Samples.Length / 2 : voice.Samples.Length;

                if (sampleIndex >= frameCount)
                {
                    if (voice.Looping)
                    {
                        voice.Position = 0;
                        sampleIndex = 0;
                    }
                    else
                    {
                        voice.Finished = true;
                        break;
                    }
                }

                float sampleL, sampleR;
                if (voice.Channels == 2)
                {
                    sampleL = voice.Samples[sampleIndex * 2];
                    sampleR = voice.Samples[sampleIndex * 2 + 1];
                }
                else
                {
                    sampleL = sampleR = voice.Samples[sampleIndex];
                }

                if (voice.Is3D)
                {
                    voice.LowpassState += lowpassAlpha * (sampleL - voice.LowpassState);
                    sampleL = voice.LowpassState;
                    sampleR = sampleL;
                }

                mixAccumulate[frame * 2] += sampleL * leftGain;
                mixAccumulate[frame * 2 + 1] += sampleR * rightGain;

                if (voice.Is3D)
                {
                    float monoDry = (sampleL + sampleR) * 0.5f * gain;
                    if (zoneScratch != null)
                    {
                        zoneScratch[frame] += monoDry * reverbSend;
                    }
                    echoSendAccum[frame] += monoDry * echoSend;
                }

                voice.Position += voice.Pitch * voicePitchScale;
            }
        }

        float[] reverbOutPerFrame = reverbOutScratch;
        Array.Clear(reverbOutPerFrame, 0, mixChunkFrames);
        foreach (var kv in zoneReverbs)
        {
            zoneSendScratch.TryGetValue(kv.Key, out var scratch);
            var processor = kv.Value;
            for (int frame = 0; frame < mixChunkFrames; frame++)
            {
                float input = scratch != null ? scratch[frame] : 0f;
                reverbOutPerFrame[frame] += processor.Process(input);
            }
        }
        for (int frame = 0; frame < mixChunkFrames; frame++)
        {
            float echoOut = distantEcho.Process(echoSendAccum[frame]);
            mixAccumulate[frame * 2] += reverbOutPerFrame[frame] * reverbOutputGain + echoOut * echoOutputGain;
            mixAccumulate[frame * 2 + 1] += reverbOutPerFrame[frame] * reverbOutputGain + echoOut * echoOutputGain;
        }

        byte[] pcm = FloatsToPcm16Bytes(mixAccumulate, mixAccumulate.Length);
        al.BufferData(bufferId, BufferFormat.Stereo16, pcm, mixSampleRate);
    }

    /// <summary>
    /// Loads and caches a sound so that it can be used later without needing to load it then.
    /// </summary>
    /// <param name="filePath">Path to the sound file, from the application directory.</param>
    /// <param name="mono">Whether to downmix to mono (needed for 3D sounds) or keep original channels (music/UI).</param>
    /// <returns></returns>

    public uint PrefetchSound(string filePath, bool mono)
    {
        string cacheKey = mono ? filePath + "#mono" : filePath;

        if (cachedSounds.TryGetValue(cacheKey, out uint existing))
        {
            return existing;
        }

        float[] rentedRaw = ReadOggStreamRaw(filePath, out int channels, out int sampleRate, out int sampleCount);

        float[] finalSamples;
        int finalChannels;

        if (mono && channels > 1)
        {
            finalSamples = DownmixToMono(rentedRaw, sampleCount, channels);
            finalChannels = 1;
        }
        else
        {
            finalSamples = new float[sampleCount];
            Array.Copy(rentedRaw, finalSamples, sampleCount);
            finalChannels = channels;
        }

        ArrayPool<float>.Shared.Return(rentedRaw);

        if (sampleRate != mixSampleRate)
        {
            int frameCount = finalSamples.Length / finalChannels;
            finalSamples = Resample(finalSamples, frameCount, finalChannels, sampleRate, mixSampleRate, out _);
        }

        uint id = (uint)clipSamples.Count;
        clipSamples.Add(new SoundClip { Samples = finalSamples, Channels = finalChannels });
        cachedSounds[cacheKey] = id;
        return id;
    }

    static float[] Resample(float[] source, int sourceFrames, int channels, int sourceRate, int targetRate, out int outFrames)
    {
        if (sourceRate == targetRate)
        {
            outFrames = sourceFrames;
            return source;
        }
        outFrames = (int)((long)sourceFrames * targetRate / sourceRate);
        float[] result = new float[outFrames * channels];
        double step = (double)sourceRate / targetRate;
        for (int i = 0; i < outFrames; i++)
        {
            double pos = i * step;
            int srcFrame0 = (int)pos;
            float frac = (float)(pos - srcFrame0);

            if (srcFrame0 >= sourceFrames - 1)
            {
                srcFrame0 = sourceFrames - 1;
                frac = 0f;
            }
            int srcFrame1 = srcFrame0 + 1 < sourceFrames ? srcFrame0 + 1 : srcFrame0;

            for (int c = 0; c < channels; c++)
            {
                float a = source[srcFrame0 * channels + c];
                float b = source[srcFrame1 * channels + c];
                result[i * channels + c] = a + (b - a) * frac;
            }
        }
        return result;
    }

    /// <summary>
    /// Plays a sound file.
    /// </summary>
    /// <param name="filePath">Path to the sound file, from the application directory.</param>
    /// <param name="position">Global position in 3D space to play the sound.</param>
    /// <param name="loop">Should the sound loop when finished?</param>
    /// <param name="gain">Volume of the sound.</param>
    /// <param name="pitch">Pitch of the sound. 1.0f is default.</param>
    /// <param name="disable3D">Disables the 3D sound spatialization. Used for music, or anything alike. 3D sounds are forced to be mono.</param>
    /// <param name="minDist">Distance to which the sound is at full volume.</param>
    /// <param name="maxDist">Distance where the sound is outside of hearing range.</param>
    /// <param name="rolloff">How quickly volume decreases over distance.</param>
    /// <param name="category">Which category does the sound belong to?</param>
    /// <returns>ID of the sound</returns>
    [Obsolete("in most cases, you should instead be using Soundscripts to play audio, rather than playing files directly.")]
    public uint PlaySound(string filePath, Vector3 position, bool loop = false, float gain = 1f, float pitch = 1f, bool disable3D = false, float minDist = 5f, float maxDist = 80f, float rolloff = 1f, SoundCategory category = SoundCategory.SFX)
    {
        uint bufferID = PrefetchSound(filePath, !disable3D);
        return PlaySound(bufferID, position, loop, gain, pitch, disable3D, minDist, maxDist, rolloff, category);
    }
    /// <summary>
    /// Plays a sound file.
    /// </summary>
    /// <param name="bufferID">ID of the sound file. Obtained from Prefetch.</param>
    /// <param name="position">Global position in 3D space to play the sound.</param>
    /// <param name="loop">Should the sound loop when finished?</param>
    /// <param name="gain">Volume of the sound.</param>
    /// <param name="pitch">Pitch of the sound. 1.0f is default.</param>
    /// <param name="disable3D">Disables the 3D sound spatialization. Used for music, or anything alike. 3D sounds are forced to be mono.</param>
    /// <param name="minDist">Distance to which the sound is at full volume.</param>
    /// <param name="maxDist">Distance where the sound is outside of hearing range.</param>
    /// <param name="rolloff">How quickly volume decreases over distance.</param>
    /// <param name="category">Which category does the sound belong to?</param>
    /// <returns>ID of the sound</returns>
    [Obsolete("in most cases, you should instead be using Soundscripts to play audio, rather than playing files directly.")]
    public uint PlaySound(uint bufferID, Vector3 position, bool loop = false, float gain = 1f, float pitch = 1f, bool disable3D = false, float minDist = 1f, float maxDist = 64, float rolloff = 1f, SoundCategory category = SoundCategory.SFX)
    {
        if (bufferID >= (uint)clipSamples.Count) return 0;

        uint id = unchecked((uint)Interlocked.Increment(ref nextVoiceIdRaw));

        commandQueue.Enqueue(new SoundCommand
        {
            Kind = SoundCommandKind.Play,
            VoiceId = id,
            BufferId = bufferID,
            Position = position,
            Loop = loop,
            Gain = gain,
            Pitch = pitch,
            Disable3D = disable3D,
            MinDist = minDist,
            MaxDist = maxDist,
            Rolloff = rolloff,
            Category = category
        });

        return id;
    }
    public void StopSound(uint id)
    {
        commandQueue.Enqueue(new SoundCommand
        {
            Kind = SoundCommandKind.Stop,
            VoiceId = id
        });
    }
    public void UpdateSound(uint id, Vector3? pos = null, Vector3? velocity = null, float? gain = null)
    {
        commandQueue.Enqueue(new SoundCommand
        {
            Kind = SoundCommandKind.Update,
            VoiceId = id,
            NewPosition = pos,
            NewVelocity = velocity,
            NewGain = gain
        });
    }

    public void Reset()
    {
        commandQueue.Enqueue(new SoundCommand { Kind = SoundCommandKind.Reset });
    }

    public void UpdateWorld()
    {
        PhysicsSounds.PlayAll();
    }

    static float[] ReadOggStreamRaw(string filePath, out int channels, out int sampleRate, out int sampleCount)
    {
        using var vorbis = new NVorbis.VorbisReader(filePath);
        channels = vorbis.Channels;
        sampleRate = vorbis.SampleRate;

        int requested = (int)(vorbis.TotalSamples * channels);
        float[] rented = ArrayPool<float>.Shared.Rent(requested);

        sampleCount = vorbis.ReadSamples(rented, 0, requested);

        return rented;
    }

    static float[] DownmixToMono(float[] interleaved, int sampleCount, int channels)
    {
        int frames = sampleCount / channels;
        var mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0f;
            int baseIdx = frame * channels;
            for (int c = 0; c < channels; c++)
            {
                sum += interleaved[baseIdx + c];
            }
            mono[frame] = sum / channels;
        }
        return mono;
    }
    byte[] FloatsToPcm16Bytes(float[] floats, int count)
    {
        int byteCount = count * sizeof(short);
        if (pcmScratch == null || pcmScratch.Length != byteCount)
        {
            pcmScratch = new byte[byteCount];
        }

        fixed (float* fp = floats)
        fixed (byte* bp = pcmScratch)
        {
            short* sp = (short*)bp;
            for (int i = 0; i < count; i++)
            {
                float f = fp[i];
                f = f < -1f ? -1f : (f > 1f ? 1f : f);
                sp[i] = (short)(f * short.MaxValue);
            }
        }

        return pcmScratch;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                audioThreadRunning = false;
                audioThread?.Join();

                al.SourceStop(mixerSource);
                al.DeleteSource(mixerSource);
                foreach (uint buffer in mixBuffers)
                {
                    al.DeleteBuffer(buffer);
                }
                alc.MakeContextCurrent(null);
                alc.DestroyContext(context);
                alc.CloseDevice(device);
            }
            disposedValue = true;
        }
    }
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}