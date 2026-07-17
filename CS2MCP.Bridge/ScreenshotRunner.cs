using System;
using System.Collections;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>
    /// MonoBehaviour helper that captures the screen at WaitForEndOfFrame —
    /// the only point in the frame where Unity reliably allows reading the
    /// back buffer (calling ScreenCapture from a system update returns null).
    /// Completes the BridgeRequest itself, so the handler returns no response.
    /// </summary>
    public sealed class ScreenshotRunner : MonoBehaviour
    {
        private static ScreenshotRunner s_Instance;

        public static ScreenshotRunner Ensure()
        {
            if (s_Instance == null)
            {
                var host = new GameObject("CS2MCP.ScreenshotRunner")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                DontDestroyOnLoad(host);
                s_Instance = host.AddComponent<ScreenshotRunner>();
            }
            return s_Instance;
        }

        public void Capture(BridgeRequest request)
        {
            StartCoroutine(CaptureRoutine(request));
        }

        private IEnumerator CaptureRoutine(BridgeRequest request)
        {
            yield return new WaitForEndOfFrame();

            Texture2D captured = null;
            Texture2D output = null;
            try
            {
                captured = ScreenCapture.CaptureScreenshotAsTexture();
                if (captured == null)
                {
                    // Fallback: read the back buffer directly.
                    captured = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                    captured.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                    captured.Apply();
                }

                output = captured;
                if (request.TryGetInt("width", out int width) && width > 0 && width < captured.width)
                {
                    output = Downscale(captured, width);
                }

                byte[] png = ImageConversion.EncodeToPNG(output);
                if (png == null || png.Length == 0)
                {
                    request.Complete(BridgeResponse.Error(500, "PNG encode failed"));
                }
                else
                {
                    request.Complete(BridgeResponse.Png(png));
                }
            }
            catch (Exception e)
            {
                request.Complete(BridgeResponse.Error(500, $"screenshot failed: {e.GetType().Name}: {e.Message}"));
            }
            finally
            {
                if (output != null && !ReferenceEquals(output, captured))
                {
                    Destroy(output);
                }
                if (captured != null)
                {
                    Destroy(captured);
                }
            }
        }

        private static Texture2D Downscale(Texture2D source, int targetWidth)
        {
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt((float)source.height * targetWidth / source.width));
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
                result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
