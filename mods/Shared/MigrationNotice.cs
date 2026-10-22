using UnityEngine;

namespace DHack.Shared
{
    /// <summary>
    /// Migration's on-screen notice for Valheim mods: two copies of a mod installed (see Migration.cs). Compiled into the mods that use
    /// Valheim's code by mods/Directory.Build.props.
    /// </summary>
    internal static partial class Migration
    {
        static partial void ShowNotice(string name, string text)
        {
            var notice = new GameObject("Migration_TwoCopies_" + name);
            Object.DontDestroyOnLoad(notice);
            notice.AddComponent<Notice>().Text = text;
        }

        /// <summary>Says it on screen once the player is in a world (nobody reads the log).</summary>
        private sealed class Notice : MonoBehaviour
        {
            public string Text;
            private float _next;

            private void Update()
            {
                if (Time.unscaledTime < _next || Player.m_localPlayer == null || MessageHud.instance == null) return;
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, Text);
                _next = Time.unscaledTime + 60f; // again every minute until it's fixed
            }
        }
    }
}
