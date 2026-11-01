#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Gulkonster.WaxKit.Platform
{
    /// <summary>
    /// Windows player and Editor: Anchor desktop through the esr: protocol, My Cloud Wallet in the player's own browser
    /// (Chrome, else Edge) as a small app window over the game, so their saved sign-ins and passkeys work.
    /// </summary>
    internal sealed class WaxWindowsPlatform : WaxUnityPlatformBase
    {
        /// <summary>Size of the wallet window in device-independent pixels (scaled with the screen's DPI).</summary>
        const int WalletWidth = 560, WalletHeight = 860;

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

        public override void ReturnToGame() => WaxWindowsForeground.BringGameToFront();

        public override void LetWalletAppComeToFront(string scheme, bool force)
        {
            // Windows only lets the app that is in front give the foreground away. The game is in front now, so it allows
            // any process (the wallet app) to take it when the request arrives there.
            WaxWindowsForeground.AllowAnyProcessToComeToFront();
            if (force) BringWalletAppForward(scheme);
        }

        public override bool BringWalletAppToFront(string scheme)
        {
            var process = WalletProcessForTests ?? Path.GetFileNameWithoutExtension(HandlerExecutable(scheme) ?? "");
            return !string.IsNullOrEmpty(process) && WaxWindowsForeground.BringProcessWindowToFront(process);
        }

        /// <summary>If the wallet app did not come in front by itself after a moment, put its newest window in front.</summary>
        async void BringWalletAppForward(string scheme)
        {
            try
            {
                var process = WalletProcessForTests ?? Path.GetFileNameWithoutExtension(HandlerExecutable(scheme) ?? "");
                if (string.IsNullOrEmpty(process)) return;
                await Delay(1500, default);
                if (!WaxWindowsForeground.GameIsInFront()) return;
                WaxWindowsForeground.BringProcessWindowToFront(process);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Unity WAX Kit: could not bring the wallet app to the front: " + e.Message);
            }
        }

        public override IWaxPopupFlow OpenWalletPopup(string url, JObject post)
        {
            var flow = new WaxNativePopupFlow(url, post, null) { CloseBrowserWindowWhenDone = true, WatchBrowserWindow = true };
            var browser = FindBrowser();
            if (browser == null)
            {
                // Neither Chrome nor Edge: the default browser still runs the page; the player comes back by hand.
                Debug.LogWarning("Unity WAX Kit: Chrome and Edge were not found, opening the wallet in the default browser.");
                Application.OpenURL(flow.BridgeUrl);
                return flow;
            }

            var args = $"--app=\"{flow.BridgeUrl}\" {WindowPlacement()}";
            if (!string.IsNullOrEmpty(BrowserProfileForTests))
                args += $" --user-data-dir=\"{BrowserProfileForTests}\" --no-first-run --no-default-browser-check " + BrowserArgsForTests;
            var browserWindowsBefore = WaxWindowsForeground.BrowserWindows();
            try
            {
                // With the player's normal profile, a running browser opens the window in its own process and this
                // process exits at once; the page tells the game when it is done, so the process is not needed.
                Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false })?.Dispose();
                PlaceWalletWindow(browserWindowsBefore, flow);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Unity WAX Kit: could not start {browser} ({e.Message}), opening the wallet in the default browser.");
                Application.OpenURL(flow.BridgeUrl);
            }
            return flow;
        }

        /// <summary>Puts the kit's browser window and then the wallet's popup at the wallet size over the game.</summary>
        async void PlaceWalletWindow(HashSet<IntPtr> before, WaxNativePopupFlow flow)
        {
            try
            {
                if (!WalletRect(out var x, out var y, out var width, out var height)) return;
                var area = new WaxWindowsForeground.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
                await WaxWindowsForeground.PlaceWalletWindows(before, WaxNativePopupFlow.PageTitle, area,
                    () => flow.IsFinished, milliseconds => Delay(milliseconds, default));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Unity WAX Kit: could not place the wallet window: " + e.Message);
            }
        }

        /// <summary>The wallet window's rectangle in this process's screen pixels: wallet size, centered over the game.</summary>
        static bool WalletRect(out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            var game = WaxWindowsForeground.GameWindow;
            if (game == IntPtr.Zero || !WaxWindowsForeground.GetWindowRect(game, out var area) || area.Right <= area.Left) return false;

            var scale = Math.Max(1f, GetDpiForWindow(game) / 96f);
            width = (int)Math.Min(WalletWidth * scale, (area.Right - area.Left) * 0.95f);
            height = (int)Math.Min(WalletHeight * scale, (area.Bottom - area.Top) * 0.95f);
            x = area.Left + (area.Right - area.Left - width) / 2;
            y = area.Top + (area.Bottom - area.Top - height) / 2;
            return true;
        }

        /// <summary>--window-size / --window-position arguments that center the wallet window over the game.</summary>
        static string WindowPlacement()
        {
            var game = WaxWindowsForeground.GameWindow;
            if (!WalletRect(out var x, out var y, out var width, out var height))
                return $"--window-size={WalletWidth},{WalletHeight}";

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
    }
}
#endif
