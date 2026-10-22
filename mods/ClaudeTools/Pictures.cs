using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeTools
{
    /// <summary>Pictures: your own view, and a separate camera anywhere (your character does not move). The ground surveys are the game's (its adapter).</summary>
    public partial class Plugin
    {
        /// <summary>What you see, saved as a PNG at the end of this frame (with a .json beside it saying where you were).</summary>
        public IEnumerator Screenshot(string path, int maxWidth, Action<string> done)
        {
            yield return new WaitForEndOfFrame();
            string saved = null;
            try
            {
                Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (maxWidth > 0 && tex.width > maxWidth) tex = Shrink(tex, maxWidth);
                saved = SavePng(tex, path);
                Destroy(tex);
                WriteShotInfo(path);
            }
            catch (Exception e) { Logger.LogWarning("Screenshot failed: " + e.Message); }
            done?.Invoke(saved);
        }

        /// <summary>
        /// The screenshot made smaller. The copies are Linear render textures: the screenshot's bytes are already the colours you see,
        /// and an sRGB copy (Unity's default in a linear-colour game like this one) converts them again, which washes the whole picture
        /// out like a haze. Big reductions go in halves, each a plain average of the last, so detail stays sharp instead of shimmering.
        /// </summary>
        private static Texture2D Shrink(Texture2D tex, int width)
        {
            int height = Mathf.RoundToInt(tex.height * (width / (float)tex.width));
            Texture source = tex;
            RenderTexture step = null;
            int w = tex.width, h = tex.height;
            while (w / 2 >= width)
            {
                w /= 2; h /= 2;
                RenderTexture half = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                half.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, half);
                if (step != null) RenderTexture.ReleaseTemporary(step);
                source = step = half;
            }
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            rt.filterMode = FilterMode.Bilinear;
            Graphics.Blit(source, rt);
            Texture2D small = ReadBack(rt);
            RenderTexture.ReleaseTemporary(rt);
            if (step != null) RenderTexture.ReleaseTemporary(step);
            Destroy(tex);
            return small;
        }

        private static Texture2D ReadBack(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            return tex;
        }

        private static string SavePng(Texture2D tex, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            File.Copy(path, Path.Combine(Path.GetDirectoryName(path), "latest.png"), true);
            return path;
        }

        private static void WriteShotInfo(string png)
        {
            Transform player = Adapter.Player;
            if (player == null) return;
            Transform cam = Adapter.Camera != null ? Adapter.Camera.transform : null;
            var info = new JObject
            {
                ["time"] = DateTime.Now.ToString("s"),
                ["world"] = Adapter.WorldName,
                ["player"] = Vec(player.position),
                ["playerYaw"] = Math.Round(player.eulerAngles.y, 1),
                ["camera"] = cam != null ? Vec(cam.position) : null,
                ["cameraYaw"] = cam != null ? Math.Round(cam.eulerAngles.y, 1) : 0,
                ["cameraPitch"] = cam != null ? Math.Round(cam.eulerAngles.x, 1) : 0,
            };
            if (LookPoint(out Vector3 hit)) info["lookingAt"] = Vec(hit);
            Adapter.ShotInfo(info);
            File.WriteAllText(Path.ChangeExtension(png, ".json"), info.ToString());
        }

        /// <summary>A picture from a separate camera at any spot (the character does not move). Only the world near the player is loaded.</summary>
        public static string RenderView(Vector3 position, Quaternion rotation, int width, int height, float fov, string path, bool orthographic = false, float orthoSize = 20f)
        {
            Camera main = Adapter.Camera != null ? Adapter.Camera : Camera.main;
            var go = new GameObject("ClaudeViewCamera");
            RenderTexture rt = RenderTexture.GetTemporary(Mathf.Clamp(width, 64, 4096), Mathf.Clamp(height, 64, 4096), 24, RenderTextureFormat.ARGB32);
            try
            {
                Camera cam = go.AddComponent<Camera>();
                if (main != null) cam.CopyFrom(main);
                cam.enabled = false;
                cam.transform.SetPositionAndRotation(position, rotation);
                cam.fieldOfView = fov;
                cam.orthographic = orthographic;
                cam.orthographicSize = orthoSize;
                cam.nearClipPlane = 0.2f;
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, 600f);
                cam.targetTexture = rt;
                bool hidden = Adapter.HideForPictures(true);   // (Claude's pictures are of the world, not of Valheim's Clawd)
                cam.Render();
                if (hidden) Adapter.HideForPictures(false);
                Texture2D tex = ReadBack(rt);
                string saved = SavePng(tex, path);
                Destroy(tex);
                return saved;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
                Destroy(go);
            }
        }

        private void RegisterPictureCommands()
        {
            Builtin("shot", "shot [width]: what the player sees", (a, output, error) => ShotCommand(a, output, error));

            Builtin("view", "view <place> <x> <y> <z> <yaw> <pitch> [fov] [width] [height]: a separate camera at a spot measured from a place (world, here, look, or a name)", (a, output, error) =>
            {
                if (!Frame(a.Length > 1 ? a[1] : "look", out Vector3 origin, out float yaw, out string err)) { error(err); return null; }
                Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
                Vector3 pos = origin + turn * new Vector3(F(a, 2, 0), F(a, 3, 10), F(a, 4, -20));
                Quaternion rot = turn * Quaternion.Euler(F(a, 6, 20), F(a, 5, 0), 0f);
                output(new JObject { ["view"] = RenderView(pos, rot, I(a, 8, 1280), I(a, 9, 720), F(a, 7, 55), Path.Combine(ShotDir, Stamp() + "-view.png")) });
                return null;
            });

            Builtin("orbit", "orbit <place> [radius] [pitch] [count] [width] [height]: pictures from all round a place", (a, output, error) =>
            {
                if (!Frame(a.Length > 1 ? a[1] : "look", out Vector3 origin, out float yaw, out string err)) { error(err); return null; }
                float radius = F(a, 2, 28), pitch = F(a, 3, 30);
                int count = Mathf.Clamp(I(a, 4, 4), 1, 12);
                Vector3 target = origin + Vector3.up * 2f;
                for (int k = 0; k < count; k++)
                {
                    float angle = yaw + 180f + 45f + k * 360f / count;
                    Quaternion look = Quaternion.Euler(pitch, angle, 0f);
                    output(new JObject { ["orbit"] = RenderView(target - look * Vector3.forward * radius, look, I(a, 5, 1280), I(a, 6, 720), 50f, Path.Combine(ShotDir, Stamp() + "-orbit" + k + ".png")) });
                }
                return null;
            });

            Builtin("top", "top <place> [size] [width]: straight down over a place", (a, output, error) =>
            {
                if (!Frame(a.Length > 1 ? a[1] : "look", out Vector3 origin, out float yaw, out string err)) { error(err); return null; }
                float size = F(a, 2, 30);
                output(new JObject { ["top"] = RenderView(origin + Vector3.up * 80f, Quaternion.Euler(90f, yaw, 0f), I(a, 3, 1024), I(a, 3, 1024), 50f, Path.Combine(ShotDir, Stamp() + "-top.png"), true, size / 2f) });
                return null;
            });
        }

        private IEnumerator ShotCommand(string[] a, Action<JObject> output, Action<string> error)
        {
            string got = null;
            yield return Screenshot(Path.Combine(ShotDir, Stamp() + ".png"), I(a, 1, 0), p => got = p);
            if (got != null) output(new JObject { ["shot"] = got }); else error("the screenshot failed");
        }
    }
}
