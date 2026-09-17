# Stands in for the pilot: point at the panel in the cockpit, then press F9.
param([int]$X = 720, [int]$Y = 1080)
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class F9 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static string Go(int cx, int cy) {
    IntPtr sim = IntPtr.Zero;
    EnumWindows((h, l) => { if (!IsWindowVisible(h)) return true; var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      if (sb.ToString().StartsWith("Microsoft Flight")) { sim = h; return false; } return true; }, IntPtr.Zero);
    if (sim == IntPtr.Zero) return "no simulator window";
    var p = new POINT { X = cx, Y = cy };
    ClientToScreen(sim, ref p);
    SetCursorPos(p.X, p.Y);
    System.Threading.Thread.Sleep(400);
    keybd_event(0x78, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(350);
    keybd_event(0x78, 0, 2, IntPtr.Zero);
    return "pointed at screen " + p.X + "," + p.Y + " and pressed F9";
  } }
'@
[F9]::Go($X, $Y)
