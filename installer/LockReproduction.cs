using System;
using System.IO;
using System.Linq;
using System.Reflection;
internal static class LockReproduction
{
    static int Main(string[] args)
    {
        string root=Path.GetFullPath(args[1]);Directory.CreateDirectory(Path.GetDirectoryName(root));
        var method=Assembly.LoadFile(Path.GetFullPath(args[0])).GetType("Setup").GetMethod("InstallFiles",BindingFlags.Static|BindingFlags.NonPublic);
        FileStream held=null;
        try
        {
            method.Invoke(null,new object[]{root,new Action<int>(n=> {
                if(n!=95)return;
                string stage=Directory.GetDirectories(Path.GetDirectoryName(root),Path.GetFileName(root)+"-setup-*").Single(p=>!p.EndsWith("-backup"));
                held=File.Open(Path.Combine(stage,"Uninstall.exe"),FileMode.Open,FileAccess.Read,FileShare.Read);
            })});
            throw new Exception("Expected old cleanup to fail.");
        }
        catch(TargetInvocationException ex)
        {
            File.WriteAllText(root+"-reproduction.txt",ex.InnerException+"\r\nInstalled files already exist: "+File.Exists(Path.Combine(root,"Flashback.exe")));
            return ex.InnerException is IOException && ex.InnerException.Message.Contains("Uninstall.exe") && File.Exists(Path.Combine(root,"Flashback.exe")) ? 0 : 1;
        }
        finally {if(held!=null)held.Dispose();}
    }
}
