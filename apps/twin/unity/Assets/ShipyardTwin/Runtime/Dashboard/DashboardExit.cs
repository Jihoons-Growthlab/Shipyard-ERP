using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Exit button action: stops Play Mode in the Unity editor, quits the built app otherwise.</summary>
    public static class DashboardExit
    {
        public static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.ExitPlaymode();
#else
            Application.Quit();
#endif
        }
    }
}
