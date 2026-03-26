using System;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Audio;
using UnityEngine;

/// <summary>
/// Independent TTS playback probe. Does not depend on TalkingCube.
/// Assign an AudioSource, then call SpeakProbeText() from the inspector or a button.
/// </summary>
public class TtsPlaybackProbe : MonoBehaviour
{
    [Header("Audio")]
    [SerializeField] private AudioSource playbackAudioSource;

    [Header("OpenAI")]
    [SerializeField] private OpenAIConfiguration configuration;
    [SerializeField] private string openAIApiKey = String.Empty;

    [Header("Speech")]
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private string probeText = "Hello from Quest. This is a TTS playback test.";
    [SerializeField] private string ttsModel = "gpt-4o-mini-tts";
    [SerializeField] private Voice ttsVoice = Voice.Alloy;
    [SerializeField] private SpeechResponseFormat responseFormat = SpeechResponseFormat.PCM;

    private OpenAIClient openAIClient;
    private CancellationTokenSource cts;

    private void Awake()
    {
        EnsureAudioSource();
    }

    private void Start()
    {
        if (!EnsureInitialized())
        {
            enabled = false;
            return;
        }

        if (playOnStart)
        {
            SpeakProbeText();
        }
    }

    private bool EnsureInitialized()
    {
        var auth = ResolveAuthentication();
        if (auth == null)
        {
            Debug.LogError("[TtsPlaybackProbe] Missing OpenAI API credentials.");
            return false;
        }

        if (openAIClient == null)
        {
            openAIClient = new OpenAIClient(auth, new OpenAISettings());
        }

        EnsureAudioSource();
        return true;
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

    OpenAIAuthentication ResolveAuthentication()
    {
        if (!string.IsNullOrWhiteSpace(openAIApiKey))
        {
            return new OpenAIAuthentication(openAIApiKey.Trim());
        }

        if (configuration != null && !configuration.UseAzureOpenAI)
        {
            return new OpenAIAuthentication(configuration);
        }

        return new OpenAIAuthentication().LoadFromEnvironment()
               ?? new OpenAIAuthentication().LoadFromDirectory();
    }

    [ContextMenu("Speak Probe Text")]
    public void SpeakProbeText()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        _ = SpeakProbeTextAsync(cts.Token);
    }

    async Task SpeakProbeTextAsync(CancellationToken token)
    {
        try
        {
            if (!EnsureInitialized())
            {
                return;
            }

            var request = new SpeechRequest(
                probeText,
                model: ttsModel,
                voice: ttsVoice,
                responseFormat: responseFormat);

            using var speech = await openAIClient.AudioEndpoint.GetSpeechAsync(request, cancellationToken: token);
            var clip = speech.AudioClip;

            if (clip == null)
            {
                Debug.LogWarning("[TtsPlaybackProbe] TTS returned a null clip.");
                return;
            }

            Debug.Log($"[TtsPlaybackProbe] Clip ready. length={clip.length:F2}s samples={clip.samples} channels={clip.channels} frequency={clip.frequency}Hz format={responseFormat}");

            playbackAudioSource.Stop();
            playbackAudioSource.clip = clip;
            playbackAudioSource.time = 0f;
            playbackAudioSource.Play();

            Debug.Log($"[TtsPlaybackProbe] AudioSource started. isPlaying={playbackAudioSource.isPlaying}");
            await Task.Delay((int)(clip.length * 1000) + 250, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TtsPlaybackProbe] Error: {ex.Message}");
        }
    }

    private void OnDestroy()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
