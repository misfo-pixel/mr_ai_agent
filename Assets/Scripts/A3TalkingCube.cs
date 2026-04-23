using System;
using System.Collections.Generic;
using System.Linq;
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
    [SerializeField] private A5SceneNavigator sceneNavigator;

    private List<Tool> assistantTools;

    [Serializable]
    private class ToolErrorPayload
    {
        public string status;
        public string message;
    }
    [SerializeField] private AudioSource outputAudioSource;

    [Header("Camera")]
    [SerializeField] private A4CameraFrameProvider cameraFrameProvider;

    [Header("OpenAI")]
    [SerializeField] private OpenAIConfiguration configuration;
    [SerializeField] private string openAIApiKey = string.Empty;
    [SerializeField] private string azureEndpoint = "https://csci5629-group8-resource.openai.azure.com/openai/v1";
    [SerializeField] private string azureResourceName = string.Empty;
    [SerializeField] private string azureDeploymentId = string.Empty;
    [SerializeField] private string azureApiVersion = "2024-10-21";
    [SerializeField] private bool useAzureActiveDirectory = false;

    [Header("OpenAI Models")]
    [SerializeField] private string chatModel = "gpt-5";
    [SerializeField] private string sttModel = "gpt-4o-mini-transcribe";
    [SerializeField] private string ttsModel = "gpt-4o-mini-tts";
    [SerializeField] private Voice ttsVoice = Voice.Alloy;
    [SerializeField] private string transcriptionPrompt = "Transcribe spoken English accurately. Do not translate. Prefer the most likely English words even if the audio is slightly unclear.";
    [SerializeField] private string systemPrompt = "You are a friendly talking cube with eyes. You can see what the user sees through their headset camera. When the user asks about something in their view, describe what you see. Always reply in short English sentences.";

    [Header("Recording")]
    [SerializeField] private int maxRecordingSeconds = 15;
    [SerializeField] private int sampleRate = 44100;

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
    private OpenAIAuthentication resolvedAuthentication;
    private readonly Dictionary<string, OpenAIClient> azureDeploymentClients = new();

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
    private string ResolvedChatModel => string.IsNullOrWhiteSpace(chatModel) ? string.Empty : chatModel.Trim();
    private string ResolvedSttModel => string.IsNullOrWhiteSpace(sttModel) ? string.Empty : sttModel.Trim();
    private string ResolvedTtsModel => string.IsNullOrWhiteSpace(ttsModel) ? string.Empty : ttsModel.Trim();

    void Start()
    {
        assistantTools = new List<Tool>();

        if (sceneNavigator != null)
        {
            assistantTools.Add(Tool.FromFunc<string, string>(
                "move_to_target",
                sceneNavigator.MoveToTarget,
                "Move the cube to the exact target id returned " +
                "in the scene candidates JSON."));

            assistantTools.Add(Tool.FromFunc<string, string, string>(
                "build_voxel_object",
                sceneNavigator.BuildVoxelObject,
                "Move to the exact target id and build a voxel object there. " +
                "blocksJson is optional; if omitted, build one cube."));

            systemPrompt +=
        "\nIf the user asks you to move toward or go to an object, " +
        "use the provided scene candidates JSON." +
        "\nChoose one exact candidate id from that list and call " +
        "move_to_target with that id." +
        "\nDo not invent target ids or object metadata that the tool did not return.";
        }


        if (!EnsureOpenAIClient())
        {
            enabled = false;
            return;
        }

        EnsureAudioSource();

        cubeRenderer = GetComponent<Renderer>();
        propBlock = new MaterialPropertyBlock();

        if (Microphone.devices.Length > 0)
            micDevice = Microphone.devices[0];
        else
            Debug.LogWarning("[TalkingCube] No microphone found.");

        ResetConversationHistory();
        ApplyColor();
    }

    void Update()
    {
        // Fallback input path for Quest controllers when mapper/input actions are not wired.
        if (currentState == State.Idle &&
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
        {
            Debug.Log("[TalkingCube] PrimaryIndexTrigger down -> BeginListening()");
            BeginListening();
        }

        if (currentState == State.Recording &&
            OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger))
        {
            Debug.Log("[TalkingCube] PrimaryIndexTrigger up -> StopListening()");
            StopListening();
        }
    }

    OpenAIAuthentication ResolveAuthentication()
    {
        if (!string.IsNullOrWhiteSpace(openAIApiKey))
        {
            return new OpenAIAuthentication(openAIApiKey.Trim());
        }

        if (configuration != null)
        {
            return new OpenAIAuthentication(configuration);
        }

        // Azure environment variable fallback, if provided.
        var azureApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(azureApiKey))
        {
            return new OpenAIAuthentication(azureApiKey.Trim());
        }

        return new OpenAIAuthentication().LoadFromEnvironment()
               ?? new OpenAIAuthentication().LoadFromDirectory();
    }

    OpenAISettings ResolveSettings()
    {
        if (configuration != null)
        {
            return new OpenAISettings(configuration);
        }

        var resolvedAzureEndpoint = !string.IsNullOrWhiteSpace(azureEndpoint)
            ? azureEndpoint.Trim()
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")?.Trim();

        if (!string.IsNullOrWhiteSpace(resolvedAzureEndpoint))
        {
            var endpoint = resolvedAzureEndpoint.TrimEnd('/');

            // Accept Azure endpoint forms like:
            // https://<resource>.openai.azure.com/openai/v1
            // and map to domain + v1 for the SDK.
            if (endpoint.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Substring(0, endpoint.Length - "/v1".Length);
            }

            return new OpenAISettings(domain: endpoint, apiVersion: "v1");
        }

        var resolvedResourceName = !string.IsNullOrWhiteSpace(azureResourceName)
            ? azureResourceName.Trim()
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_RESOURCE_NAME")?.Trim();

        var resolvedDeploymentId = !string.IsNullOrWhiteSpace(azureDeploymentId)
            ? azureDeploymentId.Trim()
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_ID")?.Trim();

        if (!string.IsNullOrWhiteSpace(resolvedResourceName) &&
            !string.IsNullOrWhiteSpace(resolvedDeploymentId))
        {
            var resolvedApiVersion = !string.IsNullOrWhiteSpace(azureApiVersion)
                ? azureApiVersion.Trim()
                : Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION")?.Trim();

            return new OpenAISettings(
                resourceName: resolvedResourceName,
                deploymentId: resolvedDeploymentId,
                apiVersion: resolvedApiVersion,
                useActiveDirectoryAuthentication: useAzureActiveDirectory);
        }

        return new OpenAISettings();
    }

    bool EnsureOpenAIClient()
    {
        if (openAIClient != null)
        {
            return true;
        }

        var auth = ResolveAuthentication();
        if (auth == null)
        {
            Debug.LogError("[TalkingCube] Missing API credentials. Set openAIApiKey, assign OpenAIConfiguration, or define OPENAI_API_KEY / AZURE_OPENAI_API_KEY.");
            return false;
        }

        resolvedAuthentication = auth;
        openAIClient = new OpenAIClient(auth, ResolveSettings());
        return true;
    }

    OpenAIClient ResolveClientForDeployment(string deploymentName)
    {
        if (!ShouldUseAzureDeploymentRouting())
        {
            return openAIClient;
        }

        var trimmedDeployment = string.IsNullOrWhiteSpace(deploymentName)
            ? string.Empty
            : deploymentName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDeployment))
        {
            return openAIClient;
        }

        if (!TryParseAzureEndpoint(out var resourceName, out var azureDomain))
        {
            return openAIClient;
        }

        if (azureDeploymentClients.TryGetValue(trimmedDeployment, out var cachedClient))
        {
            return cachedClient;
        }

        var resolvedApiVersion = !string.IsNullOrWhiteSpace(azureApiVersion)
            ? azureApiVersion.Trim()
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION")?.Trim();

        var settings = new OpenAISettings(
            resourceName: resourceName,
            deploymentId: trimmedDeployment,
            apiVersion: resolvedApiVersion,
            useActiveDirectoryAuthentication: useAzureActiveDirectory,
            azureDomain: azureDomain);

        var client = new OpenAIClient(resolvedAuthentication, settings);
        azureDeploymentClients[trimmedDeployment] = client;
        return client;
    }

    bool ShouldUseAzureDeploymentRouting()
    {
        if (configuration != null)
        {
            return false;
        }

        return TryParseAzureEndpoint(out _, out _);
    }

    bool TryParseAzureEndpoint(out string resourceName, out string azureDomain)
    {
        resourceName = string.Empty;
        azureDomain = string.Empty;

        var resolvedAzureEndpoint = !string.IsNullOrWhiteSpace(azureEndpoint)
            ? azureEndpoint.Trim()
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")?.Trim();

        if (string.IsNullOrWhiteSpace(resolvedAzureEndpoint) ||
            !Uri.TryCreate(resolvedAzureEndpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var parts = uri.Host.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        // Typical host: <resource>.openai.azure.com
        resourceName = parts[0];
        azureDomain = string.Join(".", parts.Skip(1));
        return !string.IsNullOrWhiteSpace(resourceName) && !string.IsNullOrWhiteSpace(azureDomain);
    }

    bool EnsureAudioSource()
    {
        if (audioSource == null)
        {
            audioSource = outputAudioSource != null
                ? outputAudioSource
                : GetComponent<AudioSource>();
        }

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        if (audioSource == null)
        {
            Debug.LogError("[TalkingCube] Failed to resolve or create an AudioSource.");
            return false;
        }

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.volume = 1f;
        audioSource.spatialBlend = 0f;
        audioSource.dopplerLevel = 0f;
        audioSource.ignoreListenerPause = true;
        audioSource.ignoreListenerVolume = true;
        audioSource.mute = false;
        return true;
    }

    void ResetConversationHistory()
    {
        history.Clear();
        history.Add(new Message(Role.System, systemPrompt));
    }

    /// <summary>Call this when the user presses the button.</summary>
    [ContextMenu("Begin Listening")]
    public void BeginListening()
    {
        if (micDevice == null || currentState != State.Idle) return;
        Microphone.GetDeviceCaps(micDevice, out int minFreq, out int maxFreq);
        recordingClip = Microphone.Start(micDevice, loop: false, maxRecordingSeconds, sampleRate);
        Debug.Log($"[TalkingCube] BeginListening. mic='{micDevice}' sampleRate={sampleRate} maxSeconds={maxRecordingSeconds}");

        // Recording state should show red
        SetState(State.Recording);
    }

    /// <summary>Call this when the user releases the button.</summary>
    [ContextMenu("Stop Listening")]
    public void StopListening()
    {
        if (currentState != State.Recording) return;

        if (!Microphone.IsRecording(micDevice))
        {
            Debug.LogWarning("[TalkingCube] Microphone stopped before release. Resetting to idle.");
            SetState(State.Idle);
            return;
        }

        int capturedSamples = Microphone.GetPosition(micDevice);
        Microphone.End(micDevice);
        Debug.Log($"[TalkingCube] StopListening. capturedSamples={capturedSamples}");

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
        _ = RunPipelineAsync(CreateTrimmedClip(recordingClip, capturedSamples), cts.Token);
    }

    // ── Pipeline (Part B) ─────────────────────────────────────────────────────
    async Task RunPipelineAsync(AudioClip clip, CancellationToken token)
    {
        try
        {
            Debug.Log($"[TalkingCube] Pipeline start. clipNull={clip == null} clipSamples={(clip != null ? clip.samples : 0)}");
            if (!EnsureOpenAIClient())
            {
                SetState(State.Idle);
                return;
            }

            using var sttRequest = new AudioTranscriptionRequest(
                clip,
                model: ResolvedSttModel,
                prompt: string.IsNullOrWhiteSpace(transcriptionPrompt) ? null : transcriptionPrompt.Trim(),
                temperature: 0.1f);

            var sttClient = ResolveClientForDeployment(ResolvedSttModel);
            Debug.Log($"[TalkingCube] STT request. model='{ResolvedSttModel}'");
            string userText = (await sttClient.AudioEndpoint.CreateTranscriptionTextAsync(sttRequest, token))?.Trim();
            Debug.Log($"[TalkingCube] STT result: '{userText}'");

            string sceneCandidatesJson = sceneNavigator != null
                ? sceneNavigator.GetSceneCandidates()
                : string.Empty;

            var userPrompt = string.IsNullOrWhiteSpace(sceneCandidatesJson)
                ? userText
                : $"{userText}\n\nScene candidates JSON:\n{sceneCandidatesJson}";

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

            // Reset history every turn so stale language context never leaks across requests.
            ResetConversationHistory();
            history.Add(new Message(Role.User, userPrompt));

            // Build the request messages: use history as-is for all past turns,
            // but replace the last message with a multimodal version if we have a snapshot
            var requestMessages = new List<Message>(history);
            if (snapshot != null)
            {
                requestMessages.RemoveAt(requestMessages.Count - 1);
                requestMessages.Add(new Message(Role.User, new List<Content>
                {
                    new Content(snapshot),
                    new Content(userPrompt)
                }));
            }
            // var chatRequest = new ChatRequest(requestMessages, chatModel);
            // var chatResponse = await openAIClient.ChatEndpoint.GetCompletionAsync(chatRequest, token);
            // string reply = chatResponse.FirstChoice.Message?.ToString().Trim();
            var chatClient = ResolveClientForDeployment(ResolvedChatModel);
            Debug.Log($"[TalkingCube] Chat request. model='{ResolvedChatModel}'");
            string reply = await GetAssistantReplyAsync(chatClient, requestMessages, token);
            if (string.IsNullOrWhiteSpace(reply))
            {
                Debug.LogWarning("[TalkingCube] OpenAI returned an empty reply.");
                SetState(State.Idle);
                return;
            }
            Debug.Log($"[TalkingCube] Chat result length={reply.Length}");

            history.Add(new Message(Role.Assistant, reply));

            var ttsRequest = new SpeechRequest(
                reply,
                model: ResolvedTtsModel,
                voice: ttsVoice,
                responseFormat: SpeechResponseFormat.PCM);
            Debug.Log($"[TalkingCube] TTS request. model='{ResolvedTtsModel}' voice='{ttsVoice}'");

            // Keep TTS on the base v1 client route for Azure compatibility.
            using var ttsResponse = await openAIClient.AudioEndpoint.GetSpeechAsync(ttsRequest, cancellationToken: token);
            AudioClip speechClip = CreateClipFromPcm(ttsResponse);

            if (speechClip == null)
            {
                Debug.LogWarning("[TalkingCube] Failed to generate speech audio.");
                SetState(State.Idle);
                return;
            }

            SetState(State.Speaking);
            Debug.Log($"[TalkingCube] TTS clip ready. length={speechClip.length:F2}s samples={speechClip.samples} channels={speechClip.channels} frequency={speechClip.frequency}Hz");
            if (!EnsureAudioSource())
            {
                SetState(State.Idle);
                return;
            }
            audioSource.Stop();
            audioSource.clip = speechClip;
            audioSource.time = 0f;
            audioSource.Play();
            Debug.Log($"[TalkingCube] Playback started. isPlaying={audioSource.isPlaying}");
            int lengthInMs = (int)(speechClip.length * 1000);
            await Task.Delay(lengthInMs + 500, token);
            SetState(State.Idle);
        }
        catch (OperationCanceledException)
        {
            if (this != null)
            {
                SetState(State.Idle);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TalkingCube] Error: {ex}");
            if (this != null)
            {
                SetState(State.Idle);
            }
        }
        finally
        {
            if (clip != null && clip != recordingClip)
            {
                Destroy(clip);
            }
        }
    }

    AudioClip CreateTrimmedClip(AudioClip sourceClip, int capturedSamples)
    {
        if (sourceClip == null)
        {
            return null;
        }

        int sampleCount = Mathf.Clamp(capturedSamples, 0, sourceClip.samples);
        if (sampleCount <= 0 || sampleCount == sourceClip.samples)
        {
            return sourceClip;
        }

        float[] samples = new float[sampleCount * sourceClip.channels];
        sourceClip.GetData(samples, 0);

        var trimmedClip = AudioClip.Create(
            $"{sourceClip.name}_trimmed",
            sampleCount,
            sourceClip.channels,
            sourceClip.frequency,
            false);
        trimmedClip.SetData(samples, 0);
        return trimmedClip;
    }

    AudioClip CreateClipFromPcm(SpeechClip speech)
    {
        if (speech == null)
        {
            Debug.LogWarning("[TalkingCube] Speech response was null.");
            return null;
        }

        var pcmData = speech.AudioData;
        if (!pcmData.IsCreated || pcmData.Length < 2)
        {
            Debug.LogWarning("[TalkingCube] Speech PCM data was empty.");
            return null;
        }

        int sampleCount = pcmData.Length / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            int byteIndex = i * 2;
            short pcmSample = (short)(pcmData[byteIndex] | (pcmData[byteIndex + 1] << 8));
            samples[i] = pcmSample / 32768f;
        }

        int sampleRateHz = speech.SampleRate > 0 ? speech.SampleRate : 24000;
        var clip = AudioClip.Create($"{speech.Name}_pcm", sampleCount, 1, sampleRateHz, false);
        clip.SetData(samples, 0);
        return clip;
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
    private async Task<string> GetAssistantReplyAsync(
        OpenAIClient chatClient,
        List<Message> conversation,
        CancellationToken token)
    {
        while (true)
        {
            var chatRequest = assistantTools != null && assistantTools.Count > 0
                ? new ChatRequest(conversation, assistantTools,
                                  toolChoice: "auto", model: ResolvedChatModel)
                : new ChatRequest(conversation, model: ResolvedChatModel);

            var response = await chatClient.ChatEndpoint
                               .GetCompletionAsync(chatRequest, token);
            var assistantMessage = response.FirstChoice.Message;

            if (response.FirstChoice.FinishReason != "tool_calls")
                return assistantMessage.Content?.ToString() ?? string.Empty;

            conversation.Add(assistantMessage);

            foreach (var toolCall in assistantMessage.ToolCalls)
            {
                string result;
                try
                {
                    result = await toolCall.InvokeFunctionAsync<string>(token);
                }
                catch (Exception ex)
                {
                    result = JsonUtility.ToJson(new ToolErrorPayload
                    {
                        status = "error",
                        message = ex.Message
                    });
                }
                conversation.Add(new Message(toolCall, result));
            }
        }
    }
}
