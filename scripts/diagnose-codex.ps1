
Add-Type -TypeDefinition @'
using System; using System.Text; using System.Diagnostics; using System.Collections.Generic; using System.Runtime.InteropServices;
public class WindowProbe {
 public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lp);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hw);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hw, StringBuilder sb, int max);
 [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hw);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hw, out uint pid);
 public static string Probe() {
   var sb = new StringBuilder();
   EnumWindows((hw,p)=>{
    var len=GetWindowTextLength(hw); if (len==0) return true;
    var text=new StringBuilder(len+4); GetWindowText(hw,text,text.Capacity);
    uint pid;GetWindowThreadProcessId(hw,out pid);
    string proc="?"; try{proc=Process.GetProcessById((int)pid).ProcessName;}catch {}
    var title=text.ToString();
    if(proc.ToLowerInvariant().Contains("codex") || title.ToLowerInvariant().Contains("codex") ||
       proc.ToLowerInvariant().Contains("openai") || proc.ToLowerInvariant().Contains("chatgpt") || title.ToLowerInvariant().Contains("ai cockpit"))
      sb.AppendLine("hw="+hw+" pid="+pid+" process="+proc+" visible="+IsWindowVisible(hw)+" len="+len+" title="+title);
    return true;
   },IntPtr.Zero);
   return sb.ToString();
 }
}
'@
Write-Output 'PROCESS_MATCHES:'
Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'codex|openai|chatgpt' -or $_.CommandLine -match 'Codex.exe|Codex.App|OpenAI.Codex' } | Select-Object ProcessId,Name,ExecutablePath,CommandLine | Format-List
Write-Output 'WINDOW_MATCHES:'
[WindowProbe]::Probe()
