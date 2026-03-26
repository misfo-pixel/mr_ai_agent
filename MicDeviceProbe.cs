using System.Collections;
using UnityEngine;

/// <summary>
/// Independent microphone probe for Unity Editor / Quest debugging.
/// Attach to any GameObject, assign an AudioSource, then use the context menus
/// or public methods to inspect mic devices, record a short clip, and play it back.
/// </summary>
public class MicDeviceProbe : MonoBehaviour
{
    [Header("Mic Selection")]
    [SerializeField] private string preferredDeviceName = string.Empty;
    [SerializeField] private bool useFirstAvailableDevice = true;

    [Header("Recording")]
    [SerializeField] private int recordingLengthSeconds = 2;
    [SerializeField] private int sampleRate = 16000;

    [Header("Playback")]
    [SerializeField] private AudioSource playbackAudioSource;

    private string activeDeviceName;
    private AudioClip lastRecordedClip;
    private Coroutine recordingRoutine;

    private void Awake()
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
        playbackAudioSource.spatialBlend = 0f;
        playbackAudioSource.mute = false;
        playbackAudioSource.volume = 1f;
    }

    private void Start()
    {
        RefreshActiveDevice();
        LogAvailableDevices();
    }

    [ContextMenu("Log Available Microphones")]
    public void LogAvailableDevices()
    {
        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogWarning("[MicDeviceProbe] No microphone devices found.");
            return;
        }

        for (int i = 0; i < devices.Length; i++)
        {
            Debug.Log($"[MicDeviceProbe] Device[{i}] = {devices[i]}");
        }

        Debug.Log($"[MicDeviceProbe] Active device = {activeDeviceName}");
    }

    [ContextMenu("Refresh Active Microphone")]
    public void RefreshActiveDevice()
    {
        activeDeviceName = ResolveDeviceName();
        if (string.IsNullOrWhiteSpace(activeDeviceName))
        {
            Debug.LogWarning("[MicDeviceProbe] Could not resolve an active microphone device.");
            return;
        }

        Microphone.GetDeviceCaps(activeDeviceName, out int minFreq, out int maxFreq);
        Debug.Log($"[MicDeviceProbe] Selected device = {activeDeviceName}, minFreq={minFreq}, maxFreq={maxFreq}");
    }

    [ContextMenu("Record Probe Clip")]
    public void RecordProbeClip()
    {
        if (recordingRoutine != null)
        {
            StopCoroutine(recordingRoutine);
            recordingRoutine = null;
        }

        RefreshActiveDevice();
        if (string.IsNullOrWhiteSpace(activeDeviceName))
        {
            return;
        }

        recordingRoutine = StartCoroutine(RecordProbeCoroutine());
    }

    [ContextMenu("Play Last Probe Clip")]
    public void PlayLastRecordedClip()
    {
        if (lastRecordedClip == null)
        {
            Debug.LogWarning("[MicDeviceProbe] No recorded clip available.");
            return;
        }

        playbackAudioSource.Stop();
        playbackAudioSource.clip = lastRecordedClip;
        playbackAudioSource.time = 0f;
        playbackAudioSource.Play();

        Debug.Log($"[MicDeviceProbe] Playing recorded clip '{lastRecordedClip.name}' length={lastRecordedClip.length:F2}s samples={lastRecordedClip.samples} channels={lastRecordedClip.channels} frequency={lastRecordedClip.frequency}Hz");
    }

    private IEnumerator RecordProbeCoroutine()
    {
        Debug.Log($"[MicDeviceProbe] Recording from '{activeDeviceName}' for {recordingLengthSeconds}s at {sampleRate}Hz");
        var recordingClip = Microphone.Start(activeDeviceName, false, recordingLengthSeconds, sampleRate);

        float startTime = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - startTime < recordingLengthSeconds)
        {
            yield return null;
        }

        int capturedSamples = Microphone.GetPosition(activeDeviceName);
        bool wasRecording = Microphone.IsRecording(activeDeviceName);
        Microphone.End(activeDeviceName);

        if (recordingClip == null)
        {
            Debug.LogWarning("[MicDeviceProbe] Recording returned a null clip.");
            recordingRoutine = null;
            yield break;
        }

        if (capturedSamples <= 0)
        {
            Debug.LogWarning("[MicDeviceProbe] Captured 0 samples.");
            recordingRoutine = null;
            yield break;
        }

        lastRecordedClip = CreateTrimmedClip(recordingClip, capturedSamples);

        Debug.Log($"[MicDeviceProbe] Recording finished. wasRecording={wasRecording} capturedSamples={capturedSamples} length={lastRecordedClip.length:F2}s samples={lastRecordedClip.samples} channels={lastRecordedClip.channels} frequency={lastRecordedClip.frequency}Hz");
        recordingRoutine = null;
    }

    private string ResolveDeviceName()
    {
        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i] == preferredDeviceName)
                {
                    return devices[i];
                }
            }

            Debug.LogWarning($"[MicDeviceProbe] Preferred device '{preferredDeviceName}' not found. Falling back.");
        }

        if (useFirstAvailableDevice)
        {
            return devices[0];
        }

        return null;
    }

    private AudioClip CreateTrimmedClip(AudioClip sourceClip, int capturedSamples)
    {
        int sampleCount = Mathf.Clamp(capturedSamples, 1, sourceClip.samples);
        float[] samples = new float[sampleCount * sourceClip.channels];
        sourceClip.GetData(samples, 0);

        var trimmedClip = AudioClip.Create(
            $"{sourceClip.name}_probe_trimmed",
            sampleCount,
            sourceClip.channels,
            sourceClip.frequency,
            false);
        trimmedClip.SetData(samples, 0);
        return trimmedClip;
    }
}
