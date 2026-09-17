# p02: does a capture of an MSFS pop-out window map back to instrument rect fractions?
#
# Pop out the instrument first (Right-Alt + click). Captures the pop-out, places five
# coloured squares at known fractions of the instrument rect through the Coherent
# inspector (markers.mjs), captures again, and finds each square by diffing the two
# captures. Prints where each square landed as a fraction of the captured image.
#
#   powershell -File capture.ps1 -Title DISPLAYUNITS -Page DisplayUnits
#
# Also times PrintWindow on the pop-out, which is the capture cost the exerciser pays.

param([string]$Title = "DISPLAYUNITS", [string]$Page = "DisplayUnits", [string]$Out = ".")

Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Text; using System.Drawing; using System.Drawing.Imaging; using System.Diagnostics;
using System.Runtime.InteropServices;
public static class Pop {
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
  public static IntPtr Find(string title) { return FindWindow("AceApp", title); }

  // PW_CLIENTONLY | PW_RENDERFULLCONTENT: without the second flag a DirectX window comes back black.
  public static byte[] Grab(IntPtr h, out int w, out int hh, string save) {
    RECT r; GetClientRect(h, out r); w = r.R; hh = r.B;
    using (var bmp = new Bitmap(w, hh, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); PrintWindow(h, hdc, 3); g.ReleaseHdc(hdc); }
      if (save != null) using (var small = new Bitmap(bmp, w / 4, hh / 4)) small.Save(save, ImageFormat.Png);
      var d = bmp.LockBits(new Rectangle(0, 0, w, hh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      var buf = new byte[w * hh * 4]; Marshal.Copy(d.Scan0, buf, 0, buf.Length); bmp.UnlockBits(d); return buf;
    } }

  public static string Bench(IntPtr h, int n) {
    RECT r; GetClientRect(h, out r); var times = new double[n];
    using (var bmp = new Bitmap(r.R, r.B)) using (var g = Graphics.FromImage(bmp))
      for (int i = 0; i < n; i++) { var sw = Stopwatch.StartNew(); var hdc = g.GetHdc(); PrintWindow(h, hdc, 3); g.ReleaseHdc(hdc); times[i] = sw.Elapsed.TotalMilliseconds; }
    Array.Sort(times);
    return "PrintWindow " + r.R + "x" + r.B + ", n=" + n + ": median " + times[n/2].ToString("0.0") + " ms, p90 " + times[(int)(n*0.9)].ToString("0.0") + " ms";
  }

  // Order and colours match MARKS in markers.mjs.
  public static string Locate(byte[] a, byte[] b, int w, int h) {
    var cols = new[] { new[]{255,0,255}, new[]{0,255,0}, new[]{255,0,0}, new[]{0,0,255}, new[]{255,255,0} };
    var names = new[] { "magenta(0.02,0.05)", "green(0.10,0.20)", "red(0.50,0.50)", "blue(0.90,0.80)", "yellow(0.98,0.95)" };
    var sb = new StringBuilder();
    for (int c = 0; c < cols.Length; c++) {
      long sx = 0, sy = 0, n = 0; int minx = int.MaxValue, maxx = -1, miny = int.MaxValue, maxy = -1;
      for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
        int i = (y * w + x) * 4; int B = b[i], G = b[i+1], R = b[i+2];
        if (Math.Abs(R - cols[c][0]) > 40 || Math.Abs(G - cols[c][1]) > 40 || Math.Abs(B - cols[c][2]) > 40) continue;
        // A display can already contain pure green or yellow; only count pixels the marker changed.
        if (Math.Abs(a[i] - B) + Math.Abs(a[i+1] - G) + Math.Abs(a[i+2] - R) < 60) continue;
        sx += x; sy += y; n++; minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
      }
      if (n == 0) { sb.AppendLine(names[c] + ": not found"); continue; }
      double cx = (double)sx / n, cy = (double)sy / n;
      sb.AppendLine(string.Format("{0}: centroid px ({1:0.0},{2:0.0}) -> fraction ({3:0.0000},{4:0.0000})  box {5}x{6}  n={7}",
        names[c], cx, cy, (cx + 0.5) / w, (cy + 0.5) / h, maxx - minx + 1, maxy - miny + 1, n));
    }
    return sb.ToString();
  } }
'@

$h = [Pop]::Find($Title)
if ($h -eq [IntPtr]::Zero) { throw "No pop-out window titled '$Title'" }
$w = 0; $hh = 0
$before = [Pop]::Grab($h, [ref]$w, [ref]$hh, "$Out\popout-before.png")
node "$PSScriptRoot\markers.mjs" on $Page
Start-Sleep -Milliseconds 700
$after = [Pop]::Grab($h, [ref]$w, [ref]$hh, "$Out\popout-markers.png")
node "$PSScriptRoot\markers.mjs" off $Page
"pop-out client ${w}x${hh}"
[Pop]::Locate($before, $after, $w, $hh)
[Pop]::Bench($h, 40)
