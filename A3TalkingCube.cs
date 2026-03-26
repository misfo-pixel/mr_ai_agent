using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using UnityEngine;

/// <summary>
/// Talking Cube — Speech → Text → GPT → TTS pipeline using the OpenAI API.
///
/// Call BeginListening() on button-down and StopListening() on button-up.
///
/// Visual feedback:
///   White  = Idle
///   Red    = Recording
///   Yellow = Processing
///   Green  = Speaking
/// </summary>
public class A3TalkingCube : MonoBehaviour
{
    [Header("Camera")]
    [SerializeField] private A4CameraFrameProvider cameraFrameProvider;

    [Header("OpenAI")]
    [SerializeField] private OpenAIConfiguration configuration;
    [SerializeField] private string openAIApiKey = "apikey.txt";

    [Header("OpenAI Models")]
    [SerializeField] private string chatModel = "gpt-5-mini";
    [SerializeField] private string sttModel = "gpt-4o-mini-transcribe";
    [SerializeField] private string ttsModel = "gpt-4o-mini-tts";
    [SerializeField] private Voice ttsVoice = Voice.Alloy;
    [SerializeField] private string transcriptionLanguage = "zh";
    [SerializeField] private string systemPrompt = "You are a friendly talking cube with eyes. You can see what the user sees through their headset camera. When the user asks about something in their view, describe what you see. Keep answers short.";

    [Header("Recording")]
    [SerializeField] private int maxRecordingSeconds = 15;
    [SerializeField] private int sampleRate = 16000;

    [Header("Visual Feedback")]
    [SerializeField] private Color idleColor = Color.white;
    [SerializeField] private Color recordingColor = Color.red;
    [SerializeField] private Color processingColor = Color.yellow;
    [SerializeField] private Color speakingColor = Color.green;

    // ── State ────────────────────────────────────────────────────────────────
    private enum State { Idle, Recording, Processing, Speaking }
    private State currentState = State.Idle;

    // ── OpenAI client ────────────────────────────────────────────────────────
    private OpenAIClient openAIClient;

    // ── Components ───────────────────────────────────────────────────────────
    private AudioSource audioSource;
    private Renderer cubeRenderer;
    private MaterialPropertyBlock propBlock;

    // ── Recording ────────────────────────────────────────────────────────────
    private string micDevice;
    private AudioClip recordingClip;

    // ── Conversation memory ──────────────────────────────────────────────────
    private readonly List<Message> history = new();
    private CancellationTokenSource cts;

    void Start()
    {
        var auth = ResolveAuthentication();
        if (auth == null)
        {
            Debug.LogError("[TalkingCube] Missing OpenAI API credentials. Set openAIApiKey, or provide a non-Azure OpenAIConfiguration, or define OPENAI_API_KEY.");
            enabled = false;
            return;
        }

        openAIClient = new OpenAIClient(auth, new OpenAISettings());

        // Unity components
        audioSource = gameObject.AddComponent<AudioSource>();
        cubeRenderer = GetComponent<Renderer>();
        propBlock = new MaterialPropertyBlock();

        if (Microphone.devices.Length > 0)
            micDevice = Microphone.devices[0];
        else
            Debug.LogWarning("[TalkingCube] No microphone found.");

        history.Add(new Message(Role.System, systemPrompt));
        ApplyColor();
    }

    OpenAIAuthentication ResolveAuthentication()
    {
        if (!string.IsNullOrWhiteSpace(openAIApiKey))
        {
            return new OpenAIAuthentication(openAIApiKey.Trim());
        }

        if (configuration != null)
        {
            if (configuration.UseAzureOpenAI)
            {
                Debug.LogWarning("[TalkingCube] Assigned OpenAIConfiguration is set to Azure OpenAI. Ignoring it for the standard OpenAI API flow.");
            }
            else
            {
                return new OpenAIAuthentication(configuration);
            }
        }

        return new OpenAIAuthentication().LoadFromEnvironment()
               ?? new OpenAIAuthentication().LoadFromDirectory();
    }

    /// <summary>Call this when the user presses the button.</summary>
    [ContextMenu("Begin Listening")]
    public void BeginListening()
    {
        if (micDevice == null || currentState != State.Idle) return;
        Microphone.GetDeviceCaps(micDevice, out int minFreq, out int maxFreq);
        recordingClip = Microphone.Start(micDevice, loop: false, maxRecordingSeconds, sampleRate);

        // Recording state should show red
        SetState(State.Recording);
    }

    /// <summary>Call this when the user releases the button.</summary>
    [ContextMenu("Stop Listening")]
    public void StopListening()
    {
        if (currentState != State.Recording) return;
        if (!Microphone.IsRecording(micDevice)) return;

        int capturedSamples = Microphone.GetPosition(micDevice);
        Microphone.End(micDevice);

        if (capturedSamples < sampleRate / 4)
        {
            Debug.Log("[TalkingCube] Recording too short, ignoring.");
            SetState(State.Idle);
            return;
        }

        SetState(State.Processing);

        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        _ = RunPipelineAsync(recordingClip, cts.Token);
    }

    // ── Pipeline (Part B) ─────────────────────────────────────────────────────
    async Task RunPipelineAsync(AudioClip clip, CancellationToken token)
    {
        try
        {
            var sttRequest = new AudioTranscriptionRequest(
                clip,
                model: sttModel,
                language: string.IsNullOrWhiteSpace(transcriptionLanguage) ? null : transcriptionLanguage.Trim(),
                temperature: 0.1f);

            string userText = (await openAIClient.AudioEndpoint.CreateTranscriptionTextAsync(sttRequest, token))?.Trim();

            Texture2D snapshot = null;
            if (cameraFrameProvider != null)
            {
                snapshot = await cameraFrameProvider.CaptureFrameAsync();
            }

            if (string.IsNullOrWhiteSpace(userText))
            {
                Debug.Log("[TalkingCube] No speech detected.");
                SetState(State.Idle);
                return;
            }

            // Always store text-only in history to avoid sending images repeatedly
            history.Add(new Message(Role.User, userText));

            // Build the request messages: use history as-is for all past turns,
            // but replace the last message with a multimodal version if we have a snapshot
            var requestMessages = new List<Message>(history);
            if (snapshot != null)
            {
                requestMessages.RemoveAt(requestMessages.Count - 1);
                requestMessages.Add(new Message(Role.User, new List<Content>
                {
                    new Content(snapshot),
                    new Content(userText)
                }));
            }
            var chatRequest = new ChatRequest(requestMessages, chatModel);
            var chatResponse = await openAIClient.ChatEndpoint.GetCompletionAsync(chatRequest, token);
            string reply = chatResponse.FirstChoice.Message?.ToString().Trim();

            if (string.IsNullOrWhiteSpace(reply))
            {
                Debug.LogWarning("[TalkingCube] OpenAI returned an empty reply.");
                SetState(State.Idle);
                return;
            }

            history.Add(new Message(Role.Assistant, reply));

            var ttsRequest = new SpeechRequest(
                reply,
                model: ttsModel,
                voice: ttsVoice,
                responseFormat: SpeechResponseFormat.PCM);

            var ttsResponse = await openAIClient.AudioEndpoint.GetSpeechAsync(ttsRequest, cancellationToken: token);
            AudioClip speechClip = ttsResponse.AudioClip;

            if (speechClip == null)
            {
                Debug.LogWarning("[TalkingCube] Failed to generate speech audio.");
                SetState(State.Idle);
                return;
            }

            SetState(State.Speaking);
            audioSource.PlayOneShot(speechClip);
            int lengthInMs = (int)(speechClip.length * 1000);
            await Task.Delay(lengthInMs + 500, token);
            SetState(State.Idle);
        }
        catch (OperationCanceledException)
        {
            SetState(State.Idle);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TalkingCube] Error: {ex.Message}");
            SetState(State.Idle);
        }
    }

    // ── State + Visual Feedback (Part A) ──────────────────────────────────────
    void SetState(State newState)
    {
        currentState = newState;
        ApplyColor();
    }

    void ApplyColor()
    {
        if (cubeRenderer == null) return;

        Color targetColor = currentState switch
        {
            State.Recording => recordingColor,
            State.Processing => processingColor,
            State.Speaking => speakingColor,
            _ => idleColor
        };

        cubeRenderer.GetPropertyBlock(propBlock);
        propBlock.SetColor("_BaseColor", targetColor);
        cubeRenderer.SetPropertyBlock(propBlock);
    }

    void OnDestroy()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
