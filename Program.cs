using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace TaskbarAppVolume
{
    static class Program
    {
        const int WH_MOUSE_LL = 14;
        const int WM_MOUSEWHEEL = 0x020A;
        const uint GA_ROOT = 2;
        const float Step = 0.05f; // 5% за один щелчок колёсика

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        static readonly HookProc _proc = Callback; // держим ссылку, чтобы GC не собрал делегат
        static IntPtr _hook;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData, flags, time;
            public IntPtr extra;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int n, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [STAThread]
        static void Main()
        {
            using var mutex = new System.Threading.Mutex(true, "TaskbarAppVolume_single", out bool isNew);
            if (!isNew) return;

            ApplicationConfiguration.Initialize();

            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                MessageBox.Show("Не удалось установить хук мыши.", "TaskbarAppVolume");
                return;
            }

            var menu = new ContextMenuStrip();
            var autostart = new ToolStripMenuItem("Запускать с Windows")
            {
                Checked = IsAutostart(),
                CheckOnClick = true
            };
            autostart.CheckedChanged += (s, e) => SetAutostart(autostart.Checked);
            menu.Items.Add(autostart);
            menu.Items.Add("Выход", null, (s, e) => Application.Exit());

            using var tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Taskbar App Volume",
                ContextMenuStrip = menu,
                Visible = true
            };

            Application.Run(); // цикл сообщений нужен для работы low-level хука

            tray.Visible = false;
            UnhookWindowsHookEx(_hook);
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "TaskbarAppVolume";

        static bool IsAutostart()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                var value = key?.GetValue(RunValueName) as string;
                return value != null && value.Trim('"').Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void SetAutostart(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (enable) key.SetValue(RunValueName, "\"" + Environment.ProcessPath + "\"");
                else key.DeleteValue(RunValueName, false);
            }
            catch (Exception ex) { Log("autostart error: " + ex.Message); }
        }

        static bool IsTaskbar(POINT p)
        {
            var hwnd = WindowFromPoint(p);
            if (hwnd == IntPtr.Zero) return false;
            var root = GetAncestor(hwnd, GA_ROOT);
            var sb = new StringBuilder(64);
            GetClassName(root, sb, 64);
            var cls = sb.ToString();
            return cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
        }

        static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEWHEEL)
            {
                try
                {
                    var d = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (IsTaskbar(d.pt))
                    {
                        int delta = (short)(d.mouseData >> 16); // +120 вверх / -120 вниз
                        var pt = d.pt;
                        float step = delta > 0 ? Step : -Step;
                        // Тяжёлую работу (UI Automation, аудио) делаем вне хука,
                        // иначе Windows отключает хук по таймауту.
                        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try
                            {
                                lock (_uiaLock)
                                {
                                    var el = FindButtonAt(pt);
                                    if (el != null) ChangeAppVolume(el, step);
                                }
                            }
                            catch (Exception ex) { Log("error: " + ex.Message); }
                        });
                        return (IntPtr)1; // колёсико над панелью задач съедаем
                    }
                }
                catch { /* хук не должен падать */ }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TaskbarAppVolume.log");

        static void Log(string msg)
        {
            try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n"); } catch { }
        }

        static readonly object _uiaLock = new object();

        // Ищем кнопку панели задач, прямоугольник которой содержит точку курсора.
        static AutomationElement FindButtonAt(POINT p)
        {
            var hwnd = WindowFromPoint(p);
            if (hwnd == IntPtr.Zero) { Log("no window at point"); return null; }
            var root = GetAncestor(hwnd, GA_ROOT);

            var rootEl = AutomationElement.FromHandle(root);
            var buttons = rootEl.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            Log($"wheel at {p.x},{p.y}; buttons in taskbar: {buttons.Count}");

            AutomationElement best = null;
            double bestArea = double.MaxValue;
            foreach (AutomationElement b in buttons)
            {
                try
                {
                    var r = b.Current.BoundingRectangle;
                    if (r.IsEmpty || !r.Contains(p.x, p.y)) continue;
                    double area = r.Width * r.Height;
                    if (area < bestArea) { best = b; bestArea = area; }
                }
                catch { }
            }

            if (best != null)
                Log($"  hit: name='{best.Current.Name}' id='{best.Current.AutomationId}'");
            else
            {
                Log("  no button under cursor; nearby descendants:");
                var all = rootEl.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                for (int i = 0; i < all.Count && i < 15; i++)
                {
                    var e = all[i];
                    Log($"    {e.Current.ControlType.ProgrammaticName} name='{e.Current.Name}' rect={e.Current.BoundingRectangle}");
                }
            }
            return best;
        }

        static bool ChangeAppVolume(AutomationElement button, float step)
        {
            string name = button.Current.Name; // например "Discord - 2 running windows"
            if (string.IsNullOrEmpty(name)) { Log("empty button name"); return false; }

            bool found = false;
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = dev.AudioSessionManager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var s = sessions[i];
                    uint pid = s.GetProcessID;
                    if (pid == 0) continue; // системные звуки

                    using var proc = Process.GetProcessById((int)pid);
                    bool match = name.IndexOf(proc.ProcessName, StringComparison.OrdinalIgnoreCase) >= 0;
                    Log($"  session: {proc.ProcessName} (pid {pid}) match={match}");
                    if (!match) continue;

                    var v = s.SimpleAudioVolume;
                    v.Volume = Math.Clamp(v.Volume + step, 0f, 1f);
                    found = true; // не выходим: у браузеров/Discord несколько сессий
                }
                catch (Exception ex) { Log("  session error: " + ex.Message); }
            }
            if (!found) Log("  no matching session");
            return found;
        }
    }
}