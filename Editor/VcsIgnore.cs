using System.IO;
using System.Linq;

namespace iDreams.IOSLocalPods
{
  /// <summary>
  /// Adds the pods directory to the version control ignore file, so each machine regenerates the pods on its
  /// first iOS build instead of checking them in. Finds the workspace root by walking up from the project:
  /// .plastic -> ignore.conf (Plastic SCM), .git -> .gitignore.
  /// </summary>
  public static class VcsIgnore
  {
    private const string Comment = "# iOS Local Pods (pl.idreams.ios-local-pods): regenerated on the first iOS build";

    public class cTarget
    {
      public string _Vcs;
      public string _IgnoreFile;
      public string _Pattern;
    }

    /// <summary>Null when the project is not inside a Plastic SCM workspace or git repository.</summary>
    public static cTarget Find(string directory)
    {
      string full = Path.GetFullPath(directory).Replace('\\', '/').TrimEnd('/');
      DirectoryInfo root = new DirectoryInfo(Path.GetFullPath("."));
      while (root != null)
      {
        bool plastic = Directory.Exists(Path.Combine(root.FullName, ".plastic"));
        string gitPath = Path.Combine(root.FullName, ".git");
        bool git = Directory.Exists(gitPath) || File.Exists(gitPath);
        if (plastic || git)
        {
          string rootPath = root.FullName.Replace('\\', '/').TrimEnd('/');
          if (!full.StartsWith(rootPath + "/"))
          {
            return null;
          }
          string relative = full.Substring(rootPath.Length + 1);
          return plastic
            ? new cTarget { _Vcs = "Plastic SCM", _IgnoreFile = Path.Combine(root.FullName, "ignore.conf"), _Pattern = "/" + relative }
            : new cTarget { _Vcs = "git", _IgnoreFile = Path.Combine(root.FullName, ".gitignore"), _Pattern = "/" + relative + "/" };
        }
        root = root.Parent;
      }
      return null;
    }

    public static bool IsIgnored(cTarget target)
    {
      if (target == null || !File.Exists(target._IgnoreFile))
      {
        return false;
      }
      string bare = target._Pattern.TrimEnd('/');
      return File.ReadAllLines(target._IgnoreFile).Select(l => l.Trim().TrimEnd('/')).Any(l => l == bare);
    }

    /// <summary>Returns a message for the log / dialog.</summary>
    public static string Add(cTarget target)
    {
      if (target == null)
      {
        return "No Plastic SCM workspace or git repository found; nothing to ignore.";
      }
      if (IsIgnored(target))
      {
        return target._Pattern + " is already in " + target._IgnoreFile + ".";
      }
      string existing = File.Exists(target._IgnoreFile) ? File.ReadAllText(target._IgnoreFile) : "";
      string prefix = existing.Length == 0 || existing.EndsWith("\n") ? "" : "\n";
      File.AppendAllText(target._IgnoreFile, prefix + Comment + "\n" + target._Pattern + "\n");
      return "Added " + target._Pattern + " to " + target._IgnoreFile + " (" + target._Vcs + "). If the folder is already checked in, "
        + "remove it from version control while keeping the local files.";
    }
  }
}
