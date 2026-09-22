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
            // While a tool is active the game draws networks as white outlines,
            // so a screenshot taken then shows the tool preview instead of the
            // city. Drop back to the default tool for the capture and restore it
            // afterwards. One frame is needed for the renderer to catch up.
            Game.Tools.ToolSystem toolSystem = null;
            Game.Tools.ToolBaseSystem suspendedTool = null;
            if (!request.TryGetBool("keepTool", out bool keepTool) || !keepTool)
            {
                toolSystem = TrySuspendActiveTool(out suspendedTool);
                if (toolSystem != null)
                {
                    yield return null;
                }
            }

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
                if (toolSystem != null && suspendedTool != null)
                {
                    toolSystem.activeTool = suspendedTool;
                }
            }
        }

        /// <summary>
        /// Switches to the default tool if anything else is active, returning the
        /// ToolSystem so the caller can put the previous tool back. Returns null
        /// when nothing had to change, which is the common case.
        /// </summary>
        private static Game.Tools.ToolSystem TrySuspendActiveTool(out Game.Tools.ToolBaseSystem previous)
        {
            previous = null;
            try
            {
                Unity.Entities.World world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                if (world == null)
                {
                    return null;
                }
                var toolSystem = world.GetExistingSystemManaged<Game.Tools.ToolSystem>();
                var defaultTool = world.GetExistingSystemManaged<Game.Tools.DefaultToolSystem>();
                if (toolSystem == null || defaultTool == null)
                {
                    return null;
                }
                Game.Tools.ToolBaseSystem active = toolSystem.activeTool;
                if (active == null || ReferenceEquals(active, defaultTool))
                {
                    return null;
                }
                previous = active;
                toolSystem.activeTool = defaultTool;
                return toolSystem;
            }
            catch (Exception e)
            {
                // A screenshot is never worth failing over a tool swap.
                Mod.Log.Warn($"screenshot could not suspend the active tool: {e.Message}");
                return null;
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
