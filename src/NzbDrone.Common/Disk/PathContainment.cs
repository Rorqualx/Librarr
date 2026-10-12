using System;
using System.IO;

namespace NzbDrone.Common.Disk
{
    public static class PathContainment
    {
        // True when path, once fully resolved, is a descendant of folder.
        //
        // Backslash is only a directory separator on Windows, but it is treated
        // as one here on every platform: a name that would climb out of the
        // folder on Windows is refused on Linux and macOS too, so the outcome
        // does not depend on where the application (or its test suite) runs.
        public static bool IsInside(string folder, string path, StringComparison comparison = StringComparison.Ordinal)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string root;
            string target;

            try
            {
                root = Resolve(folder);
                target = Resolve(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                // Not a path the OS can make sense of, so not one inside the folder.
                return false;
            }

            root = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return target.Length > root.Length && target.StartsWith(root, comparison);
        }

        private static string Resolve(string path)
        {
            return Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar));
        }
    }
}
