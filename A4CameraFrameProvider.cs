using System.Threading.Tasks;
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Captures frames from PassthroughCameraAccess and provides them as Texture2D.
/// Also displays a live preview on a debug canvas.
/// </summary>
public class A4CameraFrameProvider : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess passthroughCamera;
    [SerializeField] private RawImage debugPreview;

    private Texture2D _capturedTexture;

    void Update()
    {
        // Update the debug preview every frame the camera has new data
        if (passthroughCamera != null
            && passthroughCamera.IsPlaying
            && passthroughCamera.IsUpdatedThisFrame
            && debugPreview != null)
        {
            debugPreview.texture = passthroughCamera.GetTexture();
        }
    }

    /// <summary>
    /// Captures the current camera frame as a Texture2D using async GPU readback.
    /// Returns null if the camera is not playing or readback fails.
    /// </summary>
    public async Task<Texture2D> CaptureFrameAsync()
    {
        // TODO: Check that passthroughCamera is not null and IsPlaying.
        //       If not, log a warning and return null.
        if (passthroughCamera == null || !passthroughCamera.IsPlaying)
        {
            Debug.LogWarning("Passthrough camera not ready");
            return null;
        }
        // TODO: Call passthroughCamera.GetTexture() and cast it to RenderTexture.
        //       If the cast fails (null), return null.
        var rt = passthroughCamera.GetTexture() as RenderTexture;
        if (rt == null)
        {
            Debug.LogWarning("Failed to get RenderTexture");
            return null;
        }

        // TODO: Issue an async GPU readback using AsyncGPUReadback.Request(rt).
        //       Wait for it to complete WITHOUT blocking the main thread.
        //       Hint: use a while loop with `await Task.Yield()`.
        var request = AsyncGPUReadback.Request(rt);

        while (!request.done)
        {
            await Task.Yield();
        }
        // TODO: If the request has an error, log it and return null.
        if (request.hasError)
        {
            Debug.LogError("GPU readback error");
            return null;
        }


        // TODO: Read the pixel data from the request using request.GetData<Color32>().
        var data = request.GetData<Color32>();
        //       Create (or reuse) a Texture2D at the correct resolution,
        if (_capturedTexture == null ||
            _capturedTexture.width != rt.width ||
            _capturedTexture.height != rt.height)
        {
            _capturedTexture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        }
        //      copy the pixel data in with SetPixelData, and call Apply().
        _capturedTexture.SetPixelData(data, 0);
        _capturedTexture.Apply();

        // TODO: Return the Texture2D.
        return _capturedTexture;
    }
}