using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundboardBrowser;

/// <summary>Your mic + the browser's audio, mixed and sent to a virtual mic (e.g. "CABLE Input").</summary>
public sealed class AudioEngine : IDisposable
{
    public const int Rate = 48000;

    public readonly VolumeSampleProvider MicGain, BrowserGain;
    public event Action<string> Failed { add => _loop.Failed += value; remove => _loop.Failed -= value; }

    readonly ProcessLoopbackCapture _loop;
    readonly WasapiCapture _mic;
    readonly WasapiOut _out;

    public AudioEngine(MMDevice mic, MMDevice output, uint browserPid)
    {
        _loop = new ProcessLoopbackCapture(browserPid);

        _mic = new WasapiCapture(mic, true, 30);
        var micBuf = new BufferedWaveProvider(_mic.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(200)
        };
        _mic.DataAvailable += (_, e) => micBuf.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Normalise the mic to 48 kHz float stereo so it can be mixed with the browser audio.
        ISampleProvider m = micBuf.ToSampleProvider();
        if (m.WaveFormat.Channels == 1) m = new MonoToStereoSampleProvider(m);
        else if (m.WaveFormat.Channels > 2) m = new MultiplexingSampleProvider(new[] { m }, 2);
        if (m.WaveFormat.SampleRate != Rate) m = new WdlResamplingSampleProvider(m, Rate);

        MicGain = new VolumeSampleProvider(m);
        BrowserGain = new VolumeSampleProvider(_loop.Buffer.ToSampleProvider());

        var mixer = new MixingSampleProvider(new ISampleProvider[] { MicGain, BrowserGain }) { ReadFully = true };

        _out = new WasapiOut(output, AudioClientShareMode.Shared, true, 40);
        _out.Init(mixer.ToWaveProvider());
    }

    public void Start()
    {
        _loop.Start();
        _mic.StartRecording();
        _out.Play();
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        try { _mic.StopRecording(); } catch { }
        _loop.Dispose();
        _out.Dispose();
        _mic.Dispose();
    }
}
