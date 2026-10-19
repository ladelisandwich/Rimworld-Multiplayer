using System;
using System.Runtime.InteropServices;

namespace Multiplayer.Client
{
    static class ArbiterWindowFix
    {
        const string LpWindowName = "Rimworld by Ludeon Studios";
        const string LpBatchModeClassName = "Unity.BatchModeWindow";

        [DllImport("User32")]
        static extern int SetParent(int hwnd, int nCmdShow);

        [DllImport("User32")]
        static extern IntPtr FindWindowA(string lpClassName, string lpWindowName);

        internal static void Run()
        {
            // Reparents Unity's batch mode window, which only exists on the Mono/Unity builds
            if (!Environment.OSVersion.Platform.Equals(PlatformID.Win32NT) || !Native.IsMono) {
                return;
            }

            SetParent(FindWindowA(LpBatchModeClassName, LpWindowName).ToInt32(), -3);
        }
    }

}
