using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Flashback Setup")]
[assembly: AssemblyVersion("0.9.3.0")]
[assembly: AssemblyFileVersion("0.9.3.0")]

internal static class Setup
{
#if TEST
    // A test build, marked as one wherever setup shows its version.
    const string Version="0.9.4-TEST";
#else
    const string Version="0.9.3";
#endif
    const string Marker="Flashback-install-4a0fe501-ea28-4327-802f-21a68ab327e9";
    const string Manifest="installed-files.txt";
    const string RegistryPath=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Flashback";
    static string InstallPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Flashback"); } }
    static string StartLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Flashback.lnk"); } }
    static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Flashback.lnk"); } }
    // Builds with the FFmpeg preview player's test runner get a shortcut to it too.
    const string TestRunner="Test-FFmpeg-Player.cmd";
    static string TestsStartLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Flashback player tests.lnk"); } }
    static string TestsDesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Flashback player tests.lnk"); } }
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try
        {
#if !UNINSTALL
            if(args.Length==2 && args[0]=="--smoke-test") { SmokeTest(Path.GetFullPath(args[1]));return 0; }
            if(args.Length==2 && args[0]=="--lock-test") { LockTest(Path.GetFullPath(args[1]));return 0; }
#endif
            if(args.Length==2 && args[0]=="--render-preview")
            {
                using(var form=new SetupWindow())
                using(var bitmap=new Bitmap(form.Width,form.Height))
                {
                    form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();form.Update();
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);form.Close();
                }
                return 0;
            }
            if(args.Length==1 && args[0]=="--remove")
            {
                EnsureStopped();RemoveFiles(InstallPath);RemoveIntegration();
                MessageBox.Show("Flashback was uninstalled. Saved clips and preferences were kept.","Flashback",MessageBoxButtons.OK,MessageBoxIcon.Information);
                CleanupHelper();return 0;
            }
            // Started by Flashback's in-app updater: install without prompts, then relaunch.
            if(args.Length==1 && args[0]=="--update") { Application.Run(new SetupWindow(true));return 0; }
            Application.Run(new SetupWindow());return 0;
        }
        catch(Exception ex) { if(args.Length==2 && (args[0]=="--smoke-test" || args[0]=="--lock-test"))File.WriteAllText(args[1]+"-failure.txt",ex.ToString());else MessageBox.Show(ex.Message,"Flashback setup",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1; }
    }
    static void EnsureStopped()
    {
        var running=Process.GetProcessesByName("Flashback");
        try { if(running.Length>0)throw new IOException("Quit Flashback from its system-tray menu, then try again. Setup will not stop a recording for you."); }
        finally { foreach(var p in running)p.Dispose(); }
    }
    static string SafePath(string root,string relative)
    {
        string full=Path.GetFullPath(Path.Combine(root,relative));
        if(Path.IsPathRooted(relative) || relative.Contains(":") || !full.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe package path.");
        CheckLinks(full);return full;
    }
    static void CheckLinks(string path)
    {
        for(string p=path;!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p)) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Installation cannot use a redirected folder: "+p);
    }
    static string Hash(Stream stream) { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""); }
    static string FileHash(string path) { using(var stream=File.OpenRead(path))return Hash(stream); }
    static string[] ManifestPaths(string root)
    {
        var lines=File.ReadAllLines(Path.Combine(root,Manifest));
        if(lines.Length<2 || lines[0]!=Marker)throw new IOException("This folder does not contain a recognized Flashback installation.");
        return lines.Skip(1).Select(line=>SafePath(root,line)).ToArray();
    }
    static bool IsFileLock(IOException ex) { int code=ex.HResult & 0xffff;return code==32 || code==33; }
    static void RetryFile(Action operation)
    {
        var wait=Stopwatch.StartNew();
        for(;;)
        {
            try {operation();return;}
            catch(IOException ex) { if(!IsFileLock(ex) || wait.Elapsed.TotalSeconds>=4)throw;Thread.Sleep(200); }
        }
    }
    static void Log(string root,string message)
    {
        try {File.AppendAllText(root+"-setup.log",DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine);}catch { }
    }
    static void CleanupWork(string root,string folder)
    {
        try { CheckLinks(folder);RetryFile(()=> {if(Directory.Exists(folder))Directory.Delete(folder,true);}); }
        catch(Exception ex) {Log(root,"Temporary cleanup deferred: "+folder+Environment.NewLine+ex);}
    }
    static void InstallFiles(string root,Action<int> progress)
    {
        // Serialize setup attempts for this installation, including attempts from another window.
        string id;
        using(var bytes=new MemoryStream(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))id=Hash(bytes);
        using(var gate=new Mutex(false,"Local\\FlashbackSetup-"+id))
        {
            bool entered;
            try {entered=gate.WaitOne(0);}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("Another Flashback installer is working. Let it finish, then try again.");
            try {InstallFilesCore(root,progress);}finally {gate.ReleaseMutex();}
        }
    }
    static void InstallFilesCore(string root,Action<int> progress)
    {
        CheckLinks(root);
        if(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(Path.Combine(root,Manifest)))
            throw new IOException("The installation folder already contains files from another installation. Move that folder first: "+root);
        if(File.Exists(Path.Combine(root,Manifest)))ManifestPaths(root);
        string stage=root+"-setup-"+Guid.NewGuid().ToString("N");
        string backup=stage+"-backup";
        Directory.CreateDirectory(stage);Directory.CreateDirectory(backup);
        var touched=new System.Collections.Generic.List<string>();
        bool committed=false,preserveBackup=false;
        try
        {
            var assembly=Assembly.GetExecutingAssembly();
            using(var resource=assembly.GetManifestResourceStream("payload.zip"))
            {
                if(resource==null)throw new IOException("Installer payload is missing.");
                string expected;
                using(var reader=new StreamReader(assembly.GetManifestResourceStream("payload.sha256")))expected=reader.ReadToEnd().Trim();
                if(Hash(resource)!=expected)throw new IOException("Installer contents failed verification. Download the installer again.");
                resource.Position=0;
                using(var archive=new ZipArchive(resource,ZipArchiveMode.Read))
                {
                    int count=0;
                    foreach(var entry in archive.Entries)
                    {
                        if(String.IsNullOrEmpty(entry.Name))continue;
                        string output=SafePath(stage,entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        using(var input=entry.Open())
                        using(var destination=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(destination);
                        progress(++count*75/archive.Entries.Count);
                    }
                }
            }
            if(!File.Exists(Path.Combine(stage,"Flashback.exe")) || !File.Exists(Path.Combine(stage,"Uninstall.exe")))throw new IOException("Package is incomplete.");
            var files=Directory.GetFiles(stage,"*",SearchOption.AllDirectories).Select(p=>p.Substring(stage.Length+1)).ToArray();
            File.WriteAllLines(Path.Combine(stage,Manifest),new[]{Marker}.Concat(files).ToArray());
            Directory.CreateDirectory(root);
            foreach(var relative in files.Concat(new[]{Manifest}))
            {
                string target=SafePath(root,relative),old=SafePath(backup,relative);
                if(File.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(old));RetryFile(()=>File.Copy(target,old,true)); }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                try {RetryFile(()=>File.Copy(SafePath(stage,relative),target,true));}
                catch(IOException ex) {if(!IsFileLock(ex))touched.Add(relative);throw;}
                touched.Add(relative);
            }
            committed=true;progress(95);
        }
        catch(Exception ex)
        {
            Log(root,"Installation failed: "+ex);
            if(!committed)
                foreach(var relative in touched.AsEnumerable().Reverse())
                {
                    string old=SafePath(backup,relative),target=SafePath(root,relative);
                    try {RetryFile(()=> {if(File.Exists(old))File.Copy(old,target,true);else if(File.Exists(target))File.Delete(target);});}
                    catch(Exception rollback) {preserveBackup=true;Log(root,"Backup retained at "+backup+"; rollback failed: "+rollback);}
                }
            if(preserveBackup)throw new IOException(ex.Message+" Backup files were kept at "+backup+". Details: "+root+"-setup.log",ex);
            if(ex is IOException && IsFileLock((IOException)ex))throw new IOException("Windows is still holding an installation file. Close any Flashback uninstaller or other setup windows, then try Install again. Details: "+root+"-setup.log",ex);
            throw;
        }
        finally
        {
            // Both generated absolute paths are known siblings of this installation.
            // Temporary cleanup must never override a successful install or its original error.
            CleanupWork(root,stage);if(!preserveBackup)CleanupWork(root,backup);
        }
    }
    static void Shortcut(string link,string target,string icon=null,string description="Flashback replay recorder")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link));
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic shortcut=shell.CreateShortcut(link);
        try { shortcut.TargetPath=target;shortcut.WorkingDirectory=Path.GetDirectoryName(target);shortcut.IconLocation=(icon??target)+",0";shortcut.Description=description;shortcut.Save(); }
        finally { Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell); }
    }
    static void Register(bool desktop)
    {
        string exe=Path.Combine(InstallPath,"Flashback.exe");Shortcut(StartLink,exe);if(desktop)Shortcut(DesktopLink,exe);
        string tests=Path.Combine(InstallPath,TestRunner);
        if(File.Exists(tests)) { Shortcut(TestsStartLink,tests,exe,"Runs the FFmpeg preview player's tests and opens the results");if(desktop)Shortcut(TestsDesktopLink,tests,exe,"Runs the FFmpeg preview player's tests and opens the results"); }
        using(var key=Registry.CurrentUser.CreateSubKey(RegistryPath))
        {
            key.SetValue("DisplayName","Flashback");key.SetValue("DisplayVersion",Version);key.SetValue("Publisher","Flashback");key.SetValue("DisplayIcon",exe);
            key.SetValue("InstallLocation",InstallPath);key.SetValue("UninstallString","\""+Path.Combine(InstallPath,"Uninstall.exe")+"\"");
            key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
            key.SetValue("EstimatedSize",(int)(Directory.GetFiles(InstallPath,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length)/1024),RegistryValueKind.DWord);
        }
        // If the app already starts with Windows, point that existing preference at its installed location.
        using(var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true))
            if(run!=null && run.GetValue("Flashback")!=null)run.SetValue("Flashback","\""+exe+"\" --tray");
    }
    static void RemoveShortcut(string link,string target="Flashback.exe")
    {
        if(!File.Exists(link))return;
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shortcut=shell.CreateShortcut(link);
        try { if(String.Equals((string)shortcut.TargetPath,Path.Combine(InstallPath,target),StringComparison.OrdinalIgnoreCase))File.Delete(link); }
        finally { Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell); }
    }
    static void RemoveIntegration()
    {
        RemoveShortcut(StartLink);RemoveShortcut(DesktopLink);RemoveShortcut(TestsStartLink,TestRunner);RemoveShortcut(TestsDesktopLink,TestRunner);Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);
        using(var run=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true))
            if(run!=null && Convert.ToString(run.GetValue("Flashback")).StartsWith("\""+Path.Combine(InstallPath,"Flashback.exe")+"\"",StringComparison.OrdinalIgnoreCase))run.DeleteValue("Flashback",false);
    }
    static void RemoveFiles(string root)
    {
        CheckLinks(root);var files=ManifestPaths(root);
        foreach(var file in files)RetryFile(()=> {if(File.Exists(file))File.Delete(file);});
        File.Delete(Path.Combine(root,Manifest));
        // Never recursively remove an install folder: saved clips or added files stay intact.
        foreach(var dir in files.Select(Path.GetDirectoryName).Distinct().OrderByDescending(p=>p.Length))
            if(Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);
        if(Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())Directory.Delete(root);
    }
    static void BeginUninstall()
    {
        EnsureStopped();ManifestPaths(InstallPath);
        string helper=Path.Combine(Path.GetTempPath(),"Flashback-uninstall-"+Guid.NewGuid().ToString("N")+".exe");
        File.Copy(Assembly.GetExecutingAssembly().Location,helper);
        Process.Start(new ProcessStartInfo(helper,"--remove") { UseShellExecute=false });
    }
    static void CleanupHelper()
    {
        string own=Assembly.GetExecutingAssembly().Location;
        if(!own.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(own).StartsWith("Flashback-uninstall-"))return;
        // Only remove this exact temporary helper after it exits; no recursive shell operations.
        string script="Wait-Process -Id "+Process.GetCurrentProcess().Id+" -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '"+own.Replace("'","''")+"' -Force -ErrorAction SilentlyContinue";
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
    }
#if !UNINSTALL
    static void LockTest(string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(root));
        string results=root+"-results.txt";
        foreach(bool persistent in new[]{false,true})
        {
            string install=root+(persistent ? "-persistent" : "-transient");
            if(Directory.Exists(install))throw new IOException("Lock-test folder must be new.");
            FileStream held=null;Task release=null;string stage=null;
            try
            {
                InstallFiles(install,n=> {
                    if(n!=95)return;
                    stage=Directory.GetDirectories(Path.GetDirectoryName(install),Path.GetFileName(install)+"-setup-*").Single(p=>!p.EndsWith("-backup"));
                    held=File.Open(Path.Combine(stage,"Uninstall.exe"),FileMode.Open,FileAccess.Read,FileShare.Read);
                    if(!persistent)release=Task.Run(()=> {Thread.Sleep(800);held.Dispose();});
                });
                if(!File.Exists(Path.Combine(install,"Flashback.exe")) || !File.Exists(Path.Combine(install,Manifest)))throw new Exception("Cleanup lock prevented installation.");
                if(persistent && !File.ReadAllText(install+"-setup.log").Contains("Temporary cleanup deferred"))throw new Exception("Deferred cleanup was not logged.");
                File.AppendAllText(results,"PASS "+(persistent ? "Persistent" : "Transient")+" temporary Uninstall.exe lock does not turn a completed installation into failure.\r\n");
            }
            finally {if(release!=null)release.Wait();if(held!=null)held.Dispose();if(stage!=null)CleanupWork(install,stage);}
            File.WriteAllText(Path.Combine(install,"user-clip.txt"),"keep");
            string dllHash=FileHash(Path.Combine(install,"Flashback.dll"));
            using(var blocked=File.Open(Path.Combine(install,"Uninstall.exe"),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                bool failed=false;
                try {InstallFiles(install,_=>{});}catch(IOException ex) {failed=ex.Message.Contains("Windows is still holding");}
                if(!failed || FileHash(Path.Combine(install,"Flashback.dll"))!=dllHash)throw new Exception("Locked destination did not preserve the previous installation.");
            }
            InstallFiles(install,_=>{});
            File.AppendAllText(results,"PASS A locked installed uninstaller reports a useful error, rolls back, and succeeds after the lock is released.\r\n");
            RemoveFiles(install);
            if(!File.Exists(Path.Combine(install,"user-clip.txt")))throw new Exception("User content was removed.");
        }
    }
    static void SmokeTest(string root)
    {
        if(Directory.Exists(root))throw new IOException("Smoke-test folder must be new.");
        InstallFiles(root,_=>{});
        string expected=FileHash(Path.Combine(root,"Flashback.dll"));
        File.WriteAllText(Path.Combine(root,"user-clip.txt"),"Preserve me");
        InstallFiles(root,_=>{});
        if(expected!=FileHash(Path.Combine(root,"Flashback.dll")))throw new Exception("Upgrade changed payload unexpectedly.");
        string link=root+".lnk";Shortcut(link,Path.Combine(root,"Flashback.exe"));
        dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));dynamic shortcut=shell.CreateShortcut(link);
        try { if((string)shortcut.TargetPath!=Path.Combine(root,"Flashback.exe"))throw new Exception("Shortcut target mismatch."); }
        finally { Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);File.Delete(link); }
        File.WriteAllText(root+"-results.txt","PASS payload hash, extraction, repeat upgrade, shortcut creation and target.\r\n");
        RemoveFiles(root);
        if(!File.Exists(Path.Combine(root,"user-clip.txt")) || File.Exists(Path.Combine(root,"Flashback.exe")))throw new Exception("Uninstall did not preserve user files or remove application files.");
        File.AppendAllText(root+"-results.txt","PASS uninstall removes only manifest files and preserves user-added content.\r\n");
    }
#endif
    sealed class SetupWindow : Form
    {
        readonly Button action=new Button();readonly Label status=new Label();readonly CheckBox desktop=new CheckBox();readonly ProgressBar progress=new ProgressBar();bool busy,done;
        public SetupWindow(bool update=false)
        {
#if UNINSTALL
            bool uninstall=true;
#else
            bool uninstall=false;
#endif
#if TEST
            Text=uninstall ? "Uninstall Flashback" : "Flashback TEST Setup (FFmpeg player test build)";
#else
            Text=uninstall ? "Uninstall Flashback" : "Flashback Setup";
#endif
            ClientSize=new Size(570,315);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(10,12,15);ForeColor=Color.WhiteSmoke;Font=new Font("Segoe UI",10);Icon=Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            var title=new Label {Text=(uninstall ? "Uninstall Flashback" : "Install Flashback "+Version),Location=new Point(28,25),Size=new Size(500,40),Font=new Font("Segoe UI",20,FontStyle.Bold)};Controls.Add(title);
            status.Text=uninstall ? "Saved clips and preferences will be kept." : "A lightweight replay recorder, available from your Start menu.\r\n\r\nInstall for your account:\r\n"+InstallPath;
            status.Location=new Point(30,80);status.Size=new Size(510,115);Controls.Add(status);
            desktop.Text="Add a desktop shortcut";desktop.Checked=true;desktop.Location=new Point(30,190);desktop.Size=new Size(350,30);desktop.Visible=!uninstall;Controls.Add(desktop);
            progress.Location=new Point(30,229);progress.Size=new Size(350,8);progress.Visible=false;Controls.Add(progress);
            action.Text=uninstall ? "Uninstall" : "Install";action.Location=new Point(405,253);action.Size=new Size(130,38);action.FlatStyle=FlatStyle.Flat;action.BackColor=Color.FromArgb(156,226,193);action.ForeColor=Color.FromArgb(10,20,15);Controls.Add(action);AcceptButton=action;
            action.Click+=async delegate {
                if(update) return;
                if(done) { Process.Start(Path.Combine(InstallPath,"Flashback.exe"));Close();return; }
                try
                {
                    EnsureStopped();
                    if(uninstall) { BeginUninstall();Close();return; }
                    busy=true;action.Enabled=false;desktop.Enabled=false;progress.Visible=true;status.Text="Installing Flashback…";
                    await Task.Run(()=>InstallFiles(InstallPath,n=>BeginInvoke(new Action(()=>progress.Value=n))));
                    Register(desktop.Checked);progress.Value=100;status.Text="Flashback is installed.\r\n\r\nLaunch it here or from your Start menu.\r\nUninstall anytime from Windows Settings → Apps.";
                    done=true;action.Text="Launch";
                }
                catch(Exception ex) {status.Text="Setup could not finish.\r\n"+ex.Message;desktop.Enabled=true;}
                finally {busy=false;action.Enabled=true;}
            };
            FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(busy)e.Cancel=true; };
            if(update && !uninstall)
            {
                title.Text="Updating Flashback";desktop.Visible=false;action.Enabled=false;action.Text="Try again";
                Shown+=async delegate { await RunUpdate(); };
                action.Click+=async delegate { if(!busy && !done) await RunUpdate(); };
            }
        }
        async Task RunUpdate()
        {
            busy=true;action.Enabled=false;progress.Visible=true;progress.Value=0;status.Text="Waiting for Flashback to close…";
            try
            {
                // The app quits itself before starting this; give it a moment to release its files.
                var wait=Stopwatch.StartNew();
                while(Process.GetProcessesByName("Flashback").Length>0)
                {
                    if(wait.Elapsed.TotalSeconds>20)throw new IOException("Flashback is still running. Quit it from the system tray, then click Try again.");
                    await Task.Delay(250);
                }
                status.Text="Installing Flashback "+Version+"…";
                await Task.Run(()=>InstallFiles(InstallPath,n=>BeginInvoke(new Action(()=>progress.Value=n))));
                Register(File.Exists(DesktopLink));progress.Value=100;done=true;
                Process.Start(Path.Combine(InstallPath,"Flashback.exe"));busy=false;Close();
            }
            catch(Exception ex) {status.Text="The update could not finish. Your previous version is unchanged.\r\n"+ex.Message;busy=false;action.Enabled=true;}
        }
    }
}
