#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityWaxKit.Platform
{
    /// <summary>
    /// Windows player and Editor: Anchor desktop through the esr: protocol, My Cloud Wallet in the player's own browser
    /// (Chrome, else Edge) as a small app window over the game, so their saved sign-ins and passkeys work.
    /// </summary>
    internal sealed class WaxWindowsPlatform : WaxUnityPlatformBase
    {
        /// <summary>Size of the wallet window in device-independent pixels (scaled with the screen's DPI).</summary>
        const int WalletWidth = 560, WalletHeight = 860;
        /// <summary>WebAuth's window size (@proton/web-sdk opens it at 400 x 600).</summary>
        const int WebAuthWidth = 400, WebAuthHeight = 600;

        static (int Width, int Height) WalletSize(WaxPopupProtocol protocol) =>
            protocol == WaxPopupProtocol.WebAuth ? (WebAuthWidth, WebAuthHeight) : (WalletWidth, WalletHeight);

        /// <summary>Local tests only: a separate browser profile, so test runs never touch the player's own browser data.</summary>
        internal static string BrowserProfileForTests;
        /// <summary>Local tests only: extra browser arguments.</summary>
        internal static string BrowserArgsForTests;
        /// <summary>Local tests only: the process that stands in for the wallet app.</summary>
        internal static string WalletProcessForTests;

        public override bool IsMobile => false;
        public override string UserAgent => $"Windows; Unity {Application.unityVersion}";

        /// <summary>A protocol handler is registered when HKEY_CLASSES_ROOT\{scheme} has a "URL Protocol" value.</summary>
        public override bool? CanOpenScheme(string scheme)
        {
            if (RegOpenKeyEx(HKEY_CLASSES_ROOT, scheme, 0, KEY_READ, out var key) != 0) return false;
            try
            {
                var size = 0;
                return RegQueryValueEx(key, "URL Protocol", IntPtr.Zero, IntPtr.Zero, null, ref size) == 0;
            }
            finally
            {
                RegCloseKey(key);
            }
        }

        // Increased each time the game gives the front away or takes it back: a delayed step of an earlier one stops.
        int foregroundStep;
        // The wallet app's process name from the last LetWalletAppComeToFront, or null.
        string walletProcess;

        public override void ReturnToGame()
        {
            WaxWindowsForeground.BringGameToFront();
            if (walletProcess != null) KeepGameInFront(walletProcess, ++foregroundStep);
            walletProcess = null;
        }

        /// <summary>
        /// The wallet app can take the front back after the game (it got the player's last click and may activate its
        /// main window once its prompt closes, as Anchor does after a Cancel): for a moment, put the game back in front
        /// when that app comes in front. Other apps the player switches to are left alone.
        /// </summary>
        async void KeepGameInFront(string process, int step)
        {
            // Keep frames (and so this check) running while the game is behind the wallet.
            BeginWalletRequest();
            try
            {
                for (var waited = 0; waited < 2000 && step == foregroundStep; waited += 200)
                {
                    await Delay(200, default);
                    if (step != foregroundStep) return;
                    if (WaxWindowsForeground.ProcessIds(process).Contains(WaxWindowsForeground.ForegroundProcessId()))
                        WaxWindowsForeground.BringGameToFront();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Unity WAX Kit: could not keep the game in front: " + e.Message);
            }
            finally
            {
                EndWalletRequest();
            }
        }

        public override void LetWalletAppComeToFront(string scheme, bool force)
        {
            var step = ++foregroundStep;
            walletProcess = WalletProcessName(scheme);
            // Windows only lets the app that is in front give the foreground away. The game is in front now, so it allows
            // any process (the wallet app) to take it when the request arrives there.
            WaxWindowsForeground.AllowAnyProcessToComeToFront();
            if (force) BringWalletAppForward(scheme, step);
        }

        string WalletProcessName(string scheme)
        {
            var process = WalletProcessForTests ?? Path.GetFileNameWithoutExtension(HandlerExecutable(scheme) ?? "");
            return string.IsNullOrEmpty(process) ? null : process;
        }

        public override bool BringWalletAppToFront(string scheme)
        {
            var process = WalletProcessForTests ?? Path.GetFileNameWithoutExtension(HandlerExecutable(scheme) ?? "");
            return !string.IsNullOrEmpty(process) && WaxWindowsForeground.BringProcessWindowToFront(process);
        }

        /// <summary>
        /// If the wallet app did not come in front by itself after a moment, put its newest window in front. Not when the
        /// request ended meanwhile (the game took the front back): that would put the wallet over the game again.
        /// </summary>
        async void BringWalletAppForward(string scheme, int step)
        {
            try
            {
                var process = WalletProcessName(scheme);
                if (process == null) return;
                await Delay(1500, default);
                if (step != foregroundStep || !WaxWindowsForeground.GameIsInFront()) return;
                WaxWindowsForeground.BringProcessWindowToFront(process);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Unity WAX Kit: could not bring the wallet app to the front: " + e.Message);
            }
        }

        public override IWaxPopupFlow OpenWalletPopup(string url, JObject post, WaxPopupProtocol protocol)
        {
            var flow = new WaxNativePopupFlow(url, post, protocol, null) { CloseBrowserWindowWhenDone = true, WatchBrowserWindow = true };
            var size = WalletSize(protocol);
            var browser = FindBrowser();
            if (browser == null)
            {
                // Neither Chrome nor Edge: the default browser still runs the page; the player comes back by hand.
                Debug.LogWarning("Unity WAX Kit: Chrome and Edge were not found, opening the wallet in the default browser.");
                Application.OpenURL(flow.BridgeUrl);
                return flow;
            }

            var args = $"--app=\"{flow.BridgeUrl}\" {WindowPlacement(size)}";
            if (!string.IsNullOrEmpty(BrowserProfileForTests))
                args += $" --user-data-dir=\"{BrowserProfileForTests}\" --no-first-run --no-default-browser-check " + BrowserArgsForTests;
            var browserWindowsBefore = WaxWindowsForeground.BrowserWindows();
            try
            {
                // With the player's normal profile, a running browser opens the window in its own process and this
                // process exits at once; the page tells the game when it is done, so the process is not needed.
                StartProcess(browser, args);
                PlaceWalletWindow(browserWindowsBefore, flow, size);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Unity WAX Kit: could not start {browser} ({e.Message}), opening the wallet in the default browser.");
                Application.OpenURL(flow.BridgeUrl);
            }
            return flow;
        }

        /// <summary>
        /// Starts <paramref name="exe"/> with <paramref name="args"/> (as Process.Start without shell execute). Win32
        /// CreateProcessW: System.Diagnostics.Process cannot start processes under IL2CPP.
        /// </summary>
        static void StartProcess(string exe, string args)
        {
            var commandLine = new StringBuilder($"\"{exe}\" {args}");
            var startup = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
            if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref startup, out var info))
                throw new InvalidOperationException($"CreateProcess failed with error {Marshal.GetLastWin32Error()}");
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }

        /// <summary>Puts the kit's browser window and then the wallet's popup at the wallet size over the game.</summary>
        async void PlaceWalletWindow(HashSet<IntPtr> before, WaxNativePopupFlow flow, (int Width, int Height) size)
        {
            try
            {
                if (!WalletRect(size, out var x, out var y, out var width, out var height)) return;
                var area = new WaxWindowsForeground.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
                await WaxWindowsForeground.PlaceWalletWindows(before, flow.PageTitle, area,
                    () => flow.IsFinished, milliseconds => Delay(milliseconds, default));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Unity WAX Kit: could not place the wallet window: " + e.Message);
            }
        }

        /// <summary>The wallet window's rectangle in this process's screen pixels: wallet size, centered over the game.</summary>
        static bool WalletRect((int Width, int Height) size, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            var game = WaxWindowsForeground.GameWindow;
            if (game == IntPtr.Zero || !WaxWindowsForeground.GetWindowRect(game, out var area) || area.Right <= area.Left) return false;

            var scale = Math.Max(1f, GetDpiForWindow(game) / 96f);
            width = (int)Math.Min(size.Width * scale, (area.Right - area.Left) * 0.95f);
            height = (int)Math.Min(size.Height * scale, (area.Bottom - area.Top) * 0.95f);
            x = area.Left + (area.Right - area.Left - width) / 2;
            y = area.Top + (area.Bottom - area.Top - height) / 2;
            return true;
        }

        /// <summary>--window-size / --window-position arguments that center the wallet window over the game.</summary>
        static string WindowPlacement((int Width, int Height) size)
        {
            var game = WaxWindowsForeground.GameWindow;
            if (!WalletRect(size, out var x, out var y, out var width, out var height))
                return $"--window-size={size.Width},{size.Height}";

            // Chrome takes these in device-independent pixels; the window rectangle is in physical pixels.
            var scale = Math.Max(1f, GetDpiForWindow(game) / 96f);
            return $"--window-size={(int)(width / scale)},{(int)(height / scale)} --window-position={(int)(x / scale)},{(int)(y / scale)}";
        }

        /// <summary>Chrome when installed, else Edge (part of every Windows 10 / 11), else null.</summary>
        internal static string FindBrowser()
        {
            foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
            {
                var path = AppPath(exe);
                if (path != null) return path;

                var folders = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                };
                var sub = exe == "chrome.exe" ? @"Google\Chrome\Application" : @"Microsoft\Edge\Application";
                foreach (var folder in folders)
                {
                    if (string.IsNullOrEmpty(folder)) continue;
                    var candidate = Path.Combine(folder, sub, exe);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        /// <summary>The path an installed program registers under App Paths (per user first, then per machine).</summary>
        static string AppPath(string exe)
        {
            foreach (var root in new[] { HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE })
            {
                var path = ReadDefaultValue(root, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                path = path?.Trim('"');
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>The program that opens <paramref name="scheme"/> links (HKCR\{scheme}\shell\open\command), or null.</summary>
        static string HandlerExecutable(string scheme)
        {
            var command = ReadDefaultValue(HKEY_CLASSES_ROOT, scheme + @"\shell\open\command");
            if (string.IsNullOrEmpty(command)) return null;
            command = command.Trim();
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : null;
            }
            var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd > 0 ? command.Substring(0, exeEnd + 4) : null;
        }

        static string ReadDefaultValue(UIntPtr root, string subKey)
        {
            if (RegOpenKeyEx(root, subKey, 0, KEY_READ, out var key) != 0) return null;
            try
            {
                var size = 0;
                if (RegQueryValueEx(key, null, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size <= 0) return null;
                var data = new byte[size];
                if (RegQueryValueEx(key, null, IntPtr.Zero, IntPtr.Zero, data, ref size) != 0) return null;
                return Encoding.Unicode.GetString(data, 0, size).TrimEnd('\0');
            }
            finally
            {
                RegCloseKey(key);
            }
        }

        static readonly UIntPtr HKEY_CLASSES_ROOT = new UIntPtr(0x80000000u);
        static readonly UIntPtr HKEY_CURRENT_USER = new UIntPtr(0x80000001u);
        static readonly UIntPtr HKEY_LOCAL_MACHINE = new UIntPtr(0x80000002u);
        const int KEY_READ = 0x20019;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, int options, int samDesired, out UIntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryValueEx(UIntPtr hKey, string valueName, IntPtr reserved, IntPtr type, byte[] data, ref int size);

        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(UIntPtr hKey);

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFOW
        {
            public uint cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public ushort wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }
}
#endif
