using System.Collections;
using UnityEngine;

/// <summary>
/// Minimal Quest/local audio output proof.
/// Attach to any GameObject in the scene and it will generate and play
/// a short beep on start so device audio output can be verified without
/// involving microphone, network, or TTS.
/// </summary>
public class QuestBeepProbe : MonoBehaviour
{
    [Header("Playback")]
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private AudioSource playbackAudioSource;

    [Header("Beep")]
    [SerializeField] private float beepFrequency = 880f;
    [SerializeField] private float beepDurationSeconds = 0.6f;
    [SerializeField] private float beepVolume = 0.25f;
    [SerializeField] private int sampleRate = 44100;
    [SerializeField] private int repeatCount = 3;
    [SerializeField] private float gapSeconds = 0.35f;

    private AudioClip generatedClip;

    private void Awake()
    {
        EnsureAudioSource();
    }

    private void Start()
    {
        if (playOnStart)
        {
            StartCoroutine(PlaySequenceCoroutine());
        }
    }

    [ContextMenu("Play Beep Sequence")]
    public void PlayBeepSequence()
    {
        StopAllCoroutines();
        StartCoroutine(PlaySequenceCoroutine());
    }

    private IEnumerator PlaySequenceCoroutine()
    {
        var clip = GetOrCreateClip();
        Debug.Log($"[QuestBeepProbe] Starting beep sequence. repeats={repeatCount} length={clip.length:F2}s frequency={clip.frequency}Hz");

        for (int i = 0; i < repeatCount; i++)
        {
            playbackAudioSource.Stop();
            playbackAudioSource.clip = clip;
            playbackAudioSource.time = 0f;
            playbackAudioSource.Play();
            Debug.Log($"[QuestBeepProbe] Beep {i + 1}/{repeatCount} started. isPlaying={playbackAudioSource.isPlaying}");

            yield return new WaitForSeconds(clip.length + gapSeconds);
        }

        Debug.Log("[QuestBeepProbe] Beep sequence finished.");
    }

    private void EnsureAudioSource()
    {
        if (playbackAudioSource == null)
        {
            playbackAudioSource = GetComponent<AudioSource>();
        }

        if (playbackAudioSource == null)
        {
            playbackAudioSource = gameObject.AddComponent<AudioSource>();
        }

        playbackAudioSource.playOnAwake = false;
        playbackAudioSource.loop = false;
        playbackAudioSource.volume = 1f;
        playbackAudioSource.spatialBlend = 0f;
        playbackAudioSource.mute = false;
    }

    private AudioClip GetOrCreateClip()
    {
        if (generatedClip != null)
        {
            return generatedClip;
        }

        int totalSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * beepDurationSeconds));
        float[] samples = new float[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            float t = i / (float)sampleRate;
            samples[i] = Mathf.Sin(2f * Mathf.PI * beepFrequency * t) * beepVolume;
        }

        generatedClip = AudioClip.Create("QuestBeepProbe", totalSamples, 1, sampleRate, false);
        generatedClip.SetData(samples, 0);
        return generatedClip;
    }

    private void OnDestroy()
    {
        if (generatedClip != null)
        {
            Destroy(generatedClip);
        }
    }
}
