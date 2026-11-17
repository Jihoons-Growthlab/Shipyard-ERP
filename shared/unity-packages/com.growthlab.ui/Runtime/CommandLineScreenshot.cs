using System;
using System.IO;
using UnityEngine;

namespace GrowthLab.UI
{
    /// <summary>
    /// Automated visual check for any player build: start it with
    /// <c>-growthlab-screenshot &lt;file.png&gt; [-growthlab-screenshot-delay &lt;seconds&gt;]</c> and it saves one
    /// screenshot after the delay (default 3s) and quits. Without the argument it does nothing.
    /// </summary>
    public sealed class CommandLineScreenshot : MonoBehaviour
    {
        public const string PathArgument = "-growthlab-screenshot";
        public const string DelayArgument = "-growthlab-screenshot-delay";

        private string _path;
        private float _delay = 3f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var path = ArgumentValue(PathArgument);
            if (path == null)
            {
                return;
            }

            var go = new GameObject("GrowthLab CommandLineScreenshot") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            var shot = go.AddComponent<CommandLineScreenshot>();
            shot._path = Path.GetFullPath(path);
            if (float.TryParse(ArgumentValue(DelayArgument), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var delay))
            {
                shot._delay = Mathf.Max(0f, delay);
            }
        }

        private async void Start()
        {
            await Awaitable.WaitForSecondsAsync(_delay, destroyCancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            ScreenCapture.CaptureScreenshot(_path);
            for (var i = 0; i < 120 && !File.Exists(_path); i++)
            {
                await Awaitable.NextFrameAsync(destroyCancellationToken);
            }

            Debug.Log("[Screenshot] " + _path);
            Application.Quit(File.Exists(_path) ? 0 : 1);
        }

        private static string ArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}
