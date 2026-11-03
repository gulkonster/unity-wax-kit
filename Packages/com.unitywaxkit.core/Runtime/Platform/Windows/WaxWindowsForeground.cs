#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace UnityWaxKit.Platform
{
    /// <summary>Finds the game window and puts it (or a wallet app's window) in front despite Windows' foreground lock.</summary>
    internal static class WaxWindowsForeground
    {
        static IntPtr gameWindow;

        /// <summary>The game's main window (the Editor's main window in play mode).</summary>
        public static IntPtr GameWindow
        {
            get
            {
                if (gameWindow != IntPtr.Zero && IsWindow(gameWindow)) return gameWindow;
                gameWindow = FindGameWindow();
                return gameWindow;
            }
        }

        /// <summary>True when a window of this process is the foreground window.</summary>
        public static bool GameIsInFront()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            return pid == GetCurrentProcessId();
        }

        /// <summary>Id of the process whose window is in front (0 when none is).</summary>
        public static uint ForegroundProcessId()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            return pid;
        }

        /// <summary>Lets another process take the foreground (works while the game is in front).</summary>
        public static void AllowAnyProcessToComeToFront() => AllowSetForegroundWindow(ASFW_ANY);

        /// <summary>Puts the game window in front (restores it when minimized).</summary>
        public static void BringGameToFront() => BringToFront(GameWindow);

        /// <summary>
        /// Puts the top visible window of the named process (for example "Anchor Wallet") in front. False when the process
        /// has no visible window.
        /// </summary>
        public static bool BringProcessWindowToFront(string processName)
        {
            var ids = ProcessIds(processName);
            if (ids.Count == 0) return false;

            searchProcesses = ids;
            foundTopWindow = IntPtr.Zero;
            EnumWindows(OnProcessWindow, IntPtr.Zero);
            searchProcesses = null;
            if (foundTopWindow == IntPtr.Zero) return false;
            BringToFront(foundTopWindow);
            return true;
        }

        /// <summary>
        /// Ids of the running processes whose exe name without ".exe" is <paramref name="processName"/> (any case), as
        /// Process.GetProcessesByName. Win32 ToolHelp snapshot: System.Diagnostics.Process does not work under IL2CPP.
        /// </summary>
        internal static HashSet<uint> ProcessIds(string processName)
        {
            var ids = new HashSet<uint>();
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE) return ids;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(entry.szExeFile), processName, StringComparison.OrdinalIgnoreCase))
                        ids.Add(entry.th32ProcessID);
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return ids;
        }

        /// <summary>
        /// Puts the kit's wallet window and the wallet's own popup at <paramref name="area"/>, so the player sees one small
        /// window over the game. Both need it: a browser that is already running ignores --window-size / --window-position
        /// and opens the app window at the size of its last window (often large), and it also ignores the size the page
        /// asks for when an app window opens a popup. So the kit finds the new window with the bridge page's
        /// <paramref name="title"/>, moves it, then moves the next new browser window (the wallet popup, opened after
        /// Continue) to the same place. Further windows the wallet opens (its "Secure Vault") are left where they are.
        /// </summary>
        /// <param name="before">The browser windows that were open before the kit started the browser.</param>
        /// <param name="finished">True once the request ended (stops the search).</param>
        /// <param name="delay">Waits the given milliseconds (the game's own delay, which keeps running in the background).</param>
        public static async Task PlaceWalletWindows(HashSet<IntPtr> before, string title, RECT area, Func<bool> finished, Func<int, Task> delay)
        {
            var kitWindow = IntPtr.Zero;
            for (var waited = 0; kitWindow == IntPtr.Zero; waited += 100)
            {
                if (finished() || waited > 10000) return;
                kitWindow = FindNewBrowserWindow(before, title);
                if (kitWindow == IntPtr.Zero) await delay(100);
            }
            await Place(kitWindow, area, delay);

            var known = new HashSet<IntPtr>(before) { kitWindow };
            while (!finished())
            {
                var popup = FindNewBrowserWindow(known, null);
                if (popup != IntPtr.Zero)
                {
                    await Place(popup, area, delay);
                    return;
                }
                await delay(100);
            }
        }

        /// <summary>
        /// Moves the window to <paramref name="area"/> and checks for a moment that it stays there: a window that just
        /// appeared may still be put back at its remembered place by the browser.
        /// </summary>
        static async Task Place(IntPtr window, RECT area, Func<int, Task> delay)
        {
            for (var i = 0; i < 20 && IsWindow(window); i++)
            {
                GetWindowRect(window, out var now);
                if (now.Left != area.Left || now.Top != area.Top || now.Right != area.Right || now.Bottom != area.Bottom)
                    MoveWindow(window, area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
                await delay(100);
            }
        }

        /// <summary>The visible top-level windows of Chromium browsers (Chrome, Edge) right now.</summary>
        public static HashSet<IntPtr> BrowserWindows() => SearchBrowserWindows(null);

        /// <summary>A browser window that is not in <paramref name="before"/> and has this exact title (null: any), or zero.</summary>
        public static IntPtr FindNewBrowserWindow(HashSet<IntPtr> before, string title)
        {
            var found = IntPtr.Zero;
            foreach (var window in SearchBrowserWindows(title))
                if (!before.Contains(window)) found = window;
            return found;
        }

        static HashSet<IntPtr> SearchBrowserWindows(string title)
        {
            lock (searchLock)
            {
                foundBrowserWindows = new HashSet<IntPtr>();
                browserTitle = title;
                EnumWindows(OnBrowserWindow, IntPtr.Zero);
                var result = foundBrowserWindows;
                foundBrowserWindows = null;
                browserTitle = null;
                return result;
            }
        }

        /// <summary>Moves and sizes a window (screen pixels of this process); restores it first when maximized.</summary>
        public static void MoveWindow(IntPtr window, int x, int y, int width, int height)
        {
            if (IsZoomed(window) || IsIconic(window)) ShowWindow(window, SW_RESTORE);
            SetWindowPos(window, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        static void BringToFront(IntPtr window)
        {
            if (window == IntPtr.Zero || GetForegroundWindow() == window) return;

            if (IsIconic(window)) ShowWindow(window, SW_RESTORE);

            // A background process may not take the foreground. Joining the input queue of the window that has it lifts
            // the lock for SetForegroundWindow; if that is not enough, a synthetic key event makes this process the one
            // that received the last input.
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var thisThread = GetCurrentThreadId();
            var attached = foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
                if (GetForegroundWindow() != window)
                {
                    keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
#if UNITY_EDITOR
                    // A lone Alt press puts a window with a menu bar (the Unity Editor) in menu mode, and the player's
                    // next click only leaves it. An unassigned key between Alt down and up keeps the menu closed. Editor
                    // only: Windows lifts the foreground lock for a lone Alt press, so in a player (no menu bar) the
                    // extra key would keep the game behind the wallet.
                    keybd_event(VK_UNASSIGNED, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_UNASSIGNED, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
#endif
                    keybd_event(VK_MENU, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
                    SetForegroundWindow(window);
                }
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
            }

            if (GetForegroundWindow() != window)
            {
                var flash = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = window, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG };
                FlashWindowEx(ref flash);
            }
        }

        // EnumWindows state; IL2CPP only marshals static callbacks. The lock keeps searches from different threads apart.
        static readonly object searchLock = new object();
        static uint searchProcess;
        static IntPtr foundPlayerWindow, foundEditorWindow, foundLargest;
        static long foundEditorArea, foundLargestArea;
        static HashSet<uint> searchProcesses;
        static IntPtr foundTopWindow;
        static HashSet<IntPtr> foundBrowserWindows;
        static string browserTitle;

        static IntPtr FindGameWindow()
        {
            searchProcess = GetCurrentProcessId();
            foundPlayerWindow = foundEditorWindow = foundLargest = IntPtr.Zero;
            foundEditorArea = foundLargestArea = 0;
            EnumWindows(OnWindow, IntPtr.Zero);
            // Player: its UnityWndClass window. Editor: the largest UnityContainerWndClass window (the main window; the
            // others are floating panels).
            if (foundPlayerWindow != IntPtr.Zero) return foundPlayerWindow;
            return foundEditorWindow != IntPtr.Zero ? foundEditorWindow : foundLargest;
        }

        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool OnWindow(IntPtr hwnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != searchProcess || !IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;

            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            GetWindowRect(hwnd, out var rect);
            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);

            if (name.ToString() == "UnityWndClass") foundPlayerWindow = hwnd;
            if (name.ToString() == "UnityContainerWndClass" && area > foundEditorArea)
            {
                foundEditorArea = area;
                foundEditorWindow = hwnd;
            }
            if (area > foundLargestArea)
            {
                foundLargestArea = area;
                foundLargest = hwnd;
            }
            return true;
        }

        /// <summary>EnumWindows goes from the top of the window stack down, so the first match is the top window.</summary>
        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool OnProcessWindow(IntPtr hwnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!searchProcesses.Contains(pid) || !IsWindowVisible(hwnd)) return true;
            GetWindowRect(hwnd, out var rect);
            if (rect.Right - rect.Left < 50 || rect.Bottom - rect.Top < 50) return true;
            foundTopWindow = hwnd;
            return false;
        }

        /// <summary>Chrome and Edge top-level windows use this class (with the page title as the window title for app windows).</summary>
        [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool OnBrowserWindow(IntPtr hwnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hwnd)) return true;
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "Chrome_WidgetWin_1") return true;
            if (browserTitle != null)
            {
                var title = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                if (title.ToString() != browserTitle) return true;
            }
            foundBrowserWindows.Add(hwnd);
            return true;
        }

        const int SW_RESTORE = 9;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        const uint GW_OWNER = 4;
        const byte VK_MENU = 0x12;
        // Listed as unassigned in the Windows virtual-key codes: no app reacts to it.
        const byte VK_UNASSIGNED = 0xE8;
        const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;
        const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;
        const uint ASFW_ANY = 0xFFFFFFFF;
        const uint TH32CS_SNAPPROCESS = 2;
        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public UIntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            /// <summary>Window edges in screen pixels.</summary>
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO
        {
            /// <summary>Size of this struct.</summary>
            public uint cbSize;
            /// <summary>Window to flash.</summary>
            public IntPtr hwnd;
            /// <summary>What to flash.</summary>
            public uint dwFlags;
            /// <summary>How many times.</summary>
            public uint uCount;
            /// <summary>Flash rate (0 = default).</summary>
            public uint dwTimeout;
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }
}
#endif
