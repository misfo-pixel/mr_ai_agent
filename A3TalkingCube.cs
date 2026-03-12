using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using Utilities.Encoding.Wav;     // important
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using UnityEngine;

/// <summary>
/// Talking Cube — Speech → Text → GPT → TTS pipeline using Azure OpenAI.
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
    [SerializeField] private string azureResourceName = "";
    [SerializeField] private string azureApiKey = "apikey.txt";
    [SerializeField] private string azureApiVersion = "2024-10-21";

    [Header("Azure — deployments")]
    [SerializeField] private string chatDeployment = "gpt-5-chat";
    [SerializeField] private string sttDeployment = "gpt-4o-mini-transcribe";
    [SerializeField] private string ttsDeployment = "gpt-4o-mini-tts";
    [SerializeField] private string systemPrompt = "You are a friendly talking cube. Keep answers short.";

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

    // ── One client per deployment ────────────────────────────────────────────
    private OpenAIClient sttClient;
    private OpenAIClient chatClient;
    private OpenAIClient ttsClient;

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
        var domain = "cognitiveservices.azure.com";
        var auth = new OpenAIAuthentication(azureApiKey);

        // Clients are already set up for you (do not change unless instructed).
        sttClient = new OpenAIClient(auth, new OpenAISettings(azureResourceName, sttDeployment, azureApiVersion, azureDomain: domain));
        chatClient = new OpenAIClient(auth, new OpenAISettings(azureResourceName, chatDeployment, azureApiVersion, azureDomain: domain));
        ttsClient = new OpenAIClient(auth, new OpenAISettings(azureResourceName, ttsDeployment, azureApiVersion, azureDomain: domain));

        // Unity components
        audioSource = gameObject.AddComponent<AudioSource>();
        cubeRenderer = GetComponent<Renderer>();
        propBlock = new MaterialPropertyBlock();

        if (Microphone.devices.Length > 0)
            micDevice = Microphone.devices[0];
        else
            Debug.LogWarning("[TalkingCube] No microphone found.");

        history.Add(new Message(Role.System, systemPrompt));

        // TODO (Part A): ensure the cube starts in the correct color.
        ApplyColor();
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

        // Processing state should show yellow
        SetState(State.Processing);

        cts = new CancellationTokenSource();
        _ = RunPipelineAsync(recordingClip, cts.Token);
    }

    // ── Pipeline (Part B) ─────────────────────────────────────────────────────
    async Task RunPipelineAsync(AudioClip clip, CancellationToken token)
    {
        try
        {
            // TODO (Part B.1): Speech → Text
            var wavBytes = clip.EncodeToWav(outputSampleRate: clip.frequency); // match header + data
            using var wavStream = new MemoryStream(wavBytes);

            // - Create an AudioTranscriptionRequest
            var sttRequest = new AudioTranscriptionRequest(wavStream, "recording.wav");

            // - Call sttClient.AudioEndpoint.CreateTranscriptionTextAsync(...)
            // - Store result in: string userText
            string userText = await sttClient.AudioEndpoint.CreateTranscriptionTextAsync(sttRequest, token);

            // TODO: If userText is empty/whitespace:
            // - Log "No speech detected."
            // - SetState(State.Idle)
            // - return
            if (string.IsNullOrWhiteSpace(userText))
            {
                // - Log "No speech detected."
                Debug.Log("[TalkingCube] No speech detected.");
                // - SetState(State.Idle)
                SetState(State.Idle);
                // - return
                return;
            }

            // TODO (Part B.2): Text → GPT
            // - add message to history
            history.Add(new Message(Role.User, userText));

            // - Create ChatRequest(history, chatDeployment)
            var chatRequest = new ChatRequest(history, model: chatDeployment);

            // - Call chatClient.ChatEndpoint.GetCompletionAsync(...)
            var chatResponse = await chatClient.ChatEndpoint.GetCompletionAsync(chatRequest, token);

            // - Extract reply string
            // - history.Add(new Message(Role.Assistant, reply));
            string reply = chatResponse.FirstChoice.Message;
            history.Add(new Message(Role.Assistant, reply));

            // TODO (Part B.3): Text → Speech
            // - Create SpeechRequest(reply)
            var ttsRequest = new SpeechRequest(reply, model: ttsDeployment);

            // - Call ttsClient.AudioEndpoint.GetSpeechAsync(...)
            var ttsResponse = await ttsClient.AudioEndpoint.GetSpeechAsync(ttsRequest, cancellationToken: token);

            // - Store in AudioClip speechClip
            AudioClip speechClip = ttsResponse.AudioClip;

            // TODO (Part B.4): Play + state transitions
            // - SetState(State.Speaking)
            SetState(State.Speaking);

            // - audioSource.PlayOneShot(speechClip)
            audioSource.PlayOneShot(speechClip);

            // - await Task.Delay( lengthInMs + smallPadding, token )
            int lengthInMs = (int)(speechClip.length * 1000);
            await Task.Delay(lengthInMs + 500, token);

            // - SetState(State.Idle)
            SetState(State.Idle);
        }
        catch (OperationCanceledException)
        {
            // TODO: Return to Idle if canceled
            SetState(State.Idle);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TalkingCube] Error: {ex.Message}");
            // TODO: Return to Idle on error
            SetState(State.Idle);
        }
    }

    // ── State + Visual Feedback (Part A) ──────────────────────────────────────
    void SetState(State newState)
    {
        // TODO (Part A.1):
        // - Update currentState
        currentState = newState;
        // - Call ApplyColor()
        ApplyColor();
    }

    void ApplyColor()
    {
        // TODO (Part A.2):
        // - If cubeRenderer is null, return
        if (cubeRenderer == null) return;
        // - Pick the correct Color based on currentState:
        //     Idle       -> idleColor
        //     Recording  -> recordingColor
        //     Processing -> processingColor
        //     Speaking   -> speakingColor
        //
        Color targetColor = currentState switch
        {
            State.Recording => recordingColor,
            State.Processing => processingColor,
            State.Speaking => speakingColor,
            _ => idleColor
        };
        // - Use MaterialPropertyBlock to set "_BaseColor"
        //   (GetPropertyBlock, SetColor, SetPropertyBlock)
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