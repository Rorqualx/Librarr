using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Update
{
    // Pure helpers that turn what a GitHub release carries (a tag, a markdown
    // body, a SHA256SUMS file) into what the updater works with.
    public static class UpdateReleaseParser
    {
        private static readonly Regex PrereleaseLabelRegex = new Regex(@"-[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);
        private static readonly Regex ChecksumLineRegex = new Regex(@"^(?<hash>[0-9a-fA-F]{64})[ \t]+[*]?(?<file>.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex MarkdownHeadingRegex = new Regex(@"^#{1,6}\s+(?<title>.+?)\s*#*$", RegexOptions.Compiled);
        private static readonly Regex BoldHeadingRegex = new Regex(@"^(\*\*|__)(?<title>[^*_]+?):?(\*\*|__):?$", RegexOptions.Compiled);
        private static readonly Regex HorizontalRuleRegex = new Regex(@"^([-*_]\s*){3,}$", RegexOptions.Compiled);
        private static readonly Regex BulletRegex = new Regex(@"^[-*+]\s+(?<text>.+)$", RegexOptions.Compiled);

        private static readonly Regex FixedHeadingRegex = new Regex(@"\bfix(ed|es)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NewHeadingRegex = new Regex(@"\b(added|new|highlights?|changed|changes|features?|improved|improvements?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Maps a release tag onto the version the build of that tag reports.
        //
        // The running app only knows its AssemblyVersion, and build.sh
        // (UpdateVersionNumber) derives that from the tag by dropping the
        // pre-release label: v1.2.3-beta is stamped 1.2.3.0 and v1.0.0-beta.4 is
        // stamped 1.0.0.4. The same transformation is applied here so that a tag
        // and the build made from it always compare equal, whatever the label.
        //
        // The price is that the label itself carries no ordering: 1.2.3-beta and a
        // later 1.2.3 both come out as 1.2.3.0, so the stable one is not seen as
        // newer by an install of the beta. Bump the number when promoting.
        public static bool TryParseVersion(string tagName, out Version version)
        {
            version = null;

            if (tagName.IsNullOrWhiteSpace())
            {
                return false;
            }

            var text = GetReleaseVersion(tagName);

            // Build metadata never takes part in ordering.
            var plus = text.IndexOf('+');
            if (plus >= 0)
            {
                text = text.Substring(0, plus);
            }

            text = PrereleaseLabelRegex.Replace(text, string.Empty, 1);

            if (!Version.TryParse(text, out var parsed))
            {
                return false;
            }

            version = Normalize(parsed);

            return true;
        }

        // The tag without its leading "v", which is how the asset names spell it.
        public static string GetReleaseVersion(string tagName)
        {
            var text = tagName.Trim();

            return text.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? text.Substring(1) : text;
        }

        // System.Version treats 1.2.3 as lower than 1.2.3.0 because an absent
        // component is -1. Always compare, and display, four components.
        public static Version Normalize(Version version)
        {
            if (version == null)
            {
                return null;
            }

            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        }

        // Looks a file up in the output of `sha256sum`: "<hash>  <name>" per line,
        // with "*" before the name in binary mode.
        public static string FindChecksum(string checksums, string fileName)
        {
            if (checksums.IsNullOrWhiteSpace() || fileName.IsNullOrWhiteSpace())
            {
                return null;
            }

            foreach (var line in checksums.Split('\n'))
            {
                var match = ChecksumLineRegex.Match(line.Trim());

                if (!match.Success)
                {
                    continue;
                }

                var file = match.Groups["file"].Value;

                if (file.StartsWith("./"))
                {
                    file = file.Substring(2);
                }

                if (file.Equals(fileName, StringComparison.Ordinal))
                {
                    return match.Groups["hash"].Value.ToLowerInvariant();
                }
            }

            return null;
        }

        // Release notes are free-form markdown, while System: Updates renders two
        // lists, "New" and "Fixed". Only what sits under a heading that plainly
        // means one or the other is taken; installation boilerplate and anything
        // else is dropped. A release with nothing recognisable returns null, which
        // the UI already shows as a maintenance release.
        public static UpdateChanges ParseChanges(string body)
        {
            if (body.IsNullOrWhiteSpace())
            {
                return null;
            }

            var changes = new UpdateChanges();
            List<string> section = null;
            var item = new List<string>();
            var inCodeBlock = false;

            void Flush()
            {
                if (section != null && item.Any())
                {
                    section.Add(string.Join(" ", item));
                }

                item.Clear();
            }

            foreach (var rawLine in body.Replace("\r", string.Empty).Split('\n'))
            {
                var line = rawLine.Trim();

                if (line.StartsWith("```"))
                {
                    Flush();
                    inCodeBlock = !inCodeBlock;
                    continue;
                }

                if (inCodeBlock)
                {
                    continue;
                }

                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }

                if (HorizontalRuleRegex.IsMatch(line))
                {
                    Flush();
                    section = null;
                    continue;
                }

                var heading = MarkdownHeadingRegex.Match(line);

                if (!heading.Success)
                {
                    heading = BoldHeadingRegex.Match(line);
                }

                if (heading.Success)
                {
                    Flush();
                    section = GetSection(changes, heading.Groups["title"].Value);
                    continue;
                }

                if (section == null)
                {
                    continue;
                }

                var bullet = BulletRegex.Match(line);

                // A top-level bullet starts a new entry; a nested one, or a wrapped
                // line, belongs to the entry above it.
                if (bullet.Success && !char.IsWhiteSpace(rawLine[0]))
                {
                    Flush();
                    item.Add(bullet.Groups["text"].Value);
                }
                else
                {
                    item.Add(bullet.Success ? bullet.Groups["text"].Value : line);
                }
            }

            Flush();

            if (changes.New.Empty() && changes.Fixed.Empty())
            {
                return null;
            }

            return changes;
        }

        private static List<string> GetSection(UpdateChanges changes, string title)
        {
            if (FixedHeadingRegex.IsMatch(title))
            {
                return changes.Fixed;
            }

            if (NewHeadingRegex.IsMatch(title))
            {
                return changes.New;
            }

            return null;
        }
    }
}
