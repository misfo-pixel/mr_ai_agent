using UnityEngine;

/// <summary>
/// Minimal local audio playback tester for Quest/Unity.
/// Attach to any GameObject, assign an AudioSource and optionally an AudioClip,
/// then call PlayTestClip() from a button/event or the inspector context menu.
/// </summary>
public class LocalAudioTester : MonoBehaviour
{
    [Header("Playback")]
    [SerializeField] private AudioSource targetAudioSource;
    [SerializeField] private AudioClip testClip;
    [SerializeField] private bool playOnStart;

    [Header("Fallback Beep")]
    [SerializeField] private bool generateFallbackBeep = true;
    [SerializeField] private float beepFrequency = 440f;
    [SerializeField] private float beepDurationSeconds = 1f;
    [SerializeField] private float beepVolume = 0.2f;
    [SerializeField] private int sampleRate = 44100;

    private AudioClip generatedClip;

    private void Awake()
    {
        if (targetAudioSource == null)
        {
            targetAudioSource = GetComponent<AudioSource>();
        }

        if (targetAudioSource == null)
        {
            targetAudioSource = gameObject.AddComponent<AudioSource>();
        }

        targetAudioSource.playOnAwake = false;
        targetAudioSource.loop = false;
        targetAudioSource.spatialBlend = 0f;
        targetAudioSource.volume = 1f;
        targetAudioSource.mute = false;
    }

    private void Start()
    {
        if (playOnStart)
        {
            PlayTestClip();
        }
    }

    [ContextMenu("Play Test Clip")]
    public void PlayTestClip()
    {
        if (targetAudioSource == null)
        {
            Debug.LogError("[LocalAudioTester] Missing AudioSource.");
            return;
        }

        var clipToPlay = ResolveClip();
        if (clipToPlay == null)
        {
            Debug.LogWarning("[LocalAudioTester] No AudioClip assigned and fallback beep is disabled.");
            return;
        }

        targetAudioSource.Stop();
        targetAudioSource.clip = clipToPlay;
        targetAudioSource.time = 0f;
        targetAudioSource.Play();

        Debug.Log($"[LocalAudioTester] Playing clip '{clipToPlay.name}' length={clipToPlay.length:F2}s samples={clipToPlay.samples} channels={clipToPlay.channels} frequency={clipToPlay.frequency}Hz");
    }

    [ContextMenu("Stop Test Clip")]
    public void StopTestClip()
    {
        if (targetAudioSource != null)
        {
            targetAudioSource.Stop();
        }
    }

    private AudioClip ResolveClip()
    {
        if (testClip != null)
        {
            return testClip;
        }

        if (!generateFallbackBeep)
        {
            return null;
        }

        if (generatedClip == null)
        {
            generatedClip = CreateBeepClip();
        }

        return generatedClip;
    }

    private AudioClip CreateBeepClip()
    {
        int totalSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * beepDurationSeconds));
        var samples = new float[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            float t = i / (float)sampleRate;
            samples[i] = Mathf.Sin(2f * Mathf.PI * beepFrequency * t) * beepVolume;
        }

        var clip = AudioClip.Create("LocalTestBeep", totalSamples, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private void OnDestroy()
    {
        if (generatedClip != null)
        {
            Destroy(generatedClip);
        }
    }
}
