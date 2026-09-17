param([int]$X = 720, [int]$Y = 1080, [int]$Mode = 1)
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class PP {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint c, uint t);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct UNION { [FieldOffset(0)] public MOUSEINPUT m; [FieldOffset(0)] public KEYBDINPUT k; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public UNION u; }

  public static List<string> Windows() {
    var list = new List<string>();
    var pids = new HashSet<uint>();
    foreach (var p in System.Diagnostics.Process.GetProcessesByName("FlightSimulator2024")) pids.Add((uint)p.Id);
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pids.Contains(pid) && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        RECT r; GetClientRect(h, out r); if (r.R > 64 && r.B > 64) list.Add(h.ToInt64() + " '" + sb + "' " + r.R + "x" + r.B); }
      return true; }, IntPtr.Zero);
    return list;
  }

  public static IntPtr SimWindow() {
    IntPtr found = IntPtr.Zero;
    var pids = new HashSet<uint>();
    foreach (var p in System.Diagnostics.Process.GetProcessesByName("FlightSimulator2024")) pids.Add((uint)p.Id);
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (pids.Contains(pid) && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        if (sb.ToString().StartsWith("Microsoft Flight")) { found = h; return false; } }
      return true; }, IntPtr.Zero);
    return found;
  }

  public static string Foreground() { var h = GetForegroundWindow(); var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }

  static INPUT Key(ushort vk, bool down) { var i = new INPUT(); i.type = 1;
    i.u.k = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = (uint)(0x0001 | (down ? 0 : 0x0002)) }; return i; }
  static INPUT Mouse(uint flags) { var i = new INPUT(); i.type = 0; i.u.m = new MOUSEINPUT { dwFlags = flags }; return i; }

  public static List<string> Click(IntPtr sim, int cx, int cy, int mode) {
    var log = new List<string>();
    var p = new POINT { X = cx, Y = cy };
    if (!ClientToScreen(sim, ref p)) { log.Add("ClientToScreen failed"); return log; }
    log.Add("foreground before: " + Foreground());
    ShowWindow(sim, 9); BringWindowToTop(sim); SetForegroundWindow(sim);
    System.Threading.Thread.Sleep(600);
    log.Add("foreground after raise: " + Foreground());
    SetCursorPos(p.X, p.Y);
    System.Threading.Thread.Sleep(250);
    // 1: a bare move event. 2: move with right-alt already held. 3: a real click, which
    // also presses whatever is under the cursor.
    if (mode == 1 || mode == 2) {
      if (mode == 2) { SendInput(1, new INPUT[] { Key(0xA5, true) }, Marshal.SizeOf(typeof(INPUT))); System.Threading.Thread.Sleep(150); }
      SetCursorPos(p.X + 1, p.Y);
      SendInput(1, new INPUT[] { Mouse(0x0001) }, Marshal.SizeOf(typeof(INPUT)));
      System.Threading.Thread.Sleep(150);
      SetCursorPos(p.X, p.Y);
      SendInput(1, new INPUT[] { Mouse(0x0001) }, Marshal.SizeOf(typeof(INPUT)));
      System.Threading.Thread.Sleep(250);
      log.Add("primed with a move event");
    }
    if (mode == 3) {
      SendInput(2, new INPUT[] { Mouse(0x0002), Mouse(0x0004) }, Marshal.SizeOf(typeof(INPUT)));
      System.Threading.Thread.Sleep(400);
      log.Add("primed with a plain click, foreground now: " + Foreground());
    }
    SendInput(1, new INPUT[] { Key(0xA5, true) }, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(250);
    var sent = SendInput(2, new INPUT[] { Mouse(0x0002), Mouse(0x0004) }, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(250);
    SendInput(1, new INPUT[] { Key(0xA5, false) }, Marshal.SizeOf(typeof(INPUT)));
    log.Add("cursor at screen " + p.X + "," + p.Y + "; right-alt click sent " + sent + " of 2");
    return log;
  } }
'@

$sim = [PP]::SimWindow()
if ($sim -eq [IntPtr]::Zero) { "no simulator window"; exit 1 }
"before:"; [PP]::Windows() | ForEach-Object { "  $_" }
[PP]::Click($sim, $X, $Y, $Mode) | ForEach-Object { "  $_" }
Start-Sleep -Seconds 4
"after:"; [PP]::Windows() | ForEach-Object { "  $_" }
