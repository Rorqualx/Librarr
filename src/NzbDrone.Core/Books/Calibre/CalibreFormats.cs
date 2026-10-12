using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Books.Calibre
{
    // Which formats of a Calibre record a given BookFile answers for.
    //
    // One Calibre "book" holds every format of a title, and Librarr only ever
    // tracks one of them as a BookFile: the file it imported. The others are
    // either conversions Librarr asked Calibre to make from that file, or
    // formats the user put there themselves. Upgrade and delete both used to
    // treat the whole record as theirs, which is how replacing or deleting one
    // file took formats nobody had asked about along with it.
    public static class CalibreFormats
    {
        // Calibre roots hold ebooks only.
        //
        // Calibre will store any file as a "format", so an audiobook can be
        // posted to it, but nothing downstream works: a record holds one file
        // per format, so a multi-part audiobook cannot be represented at all;
        // GetOriginalFormat only reports text formats, so the path of an audio
        // file is never learned and the library scan never sees it again; and
        // with no ebook to attach to it becomes a second Calibre book for the
        // same title. Audio is therefore kept out of every Calibre call.
        //
        // Either signal is enough. A file whose quality was never detected
        // reports as text, and a row carried over from before formats were
        // classified may have an extension that says otherwise.
        public static bool IsAudio(BookFile file)
        {
            return file.Quality.Format() == QualityFormat.Audio || IsAudio(Format(file.Path));
        }

        public static bool IsAudio(string format)
        {
            return format.IsNotNullOrWhiteSpace() && MediaFileExtensions.AudioExtensions.Contains("." + format);
        }

        // The format name Calibre uses for a file: its extension, no dot.
        public static string Format(string path)
        {
            return path.IsNullOrWhiteSpace() ? string.Empty : Path.GetExtension(path).TrimStart('.');
        }

        public static IEnumerable<string> OutputFormats(CalibreSettings settings)
        {
            if (settings?.OutputFormat == null)
            {
                return Enumerable.Empty<string>();
            }

            return settings.OutputFormat.Split(',').Select(x => x.Trim()).Where(x => x.IsNotNullOrWhiteSpace());
        }

        // The formats of `book` that go when `file` goes: its own, plus the
        // conversions configured for the root folder, since those were made
        // from it and are not tracked as files in their own right.
        //
        // Never an audio format, and never one that another BookFile sharing
        // the record points at. Anything else on the record is not Librarr's.
        //
        // Returned as Calibre spelled them, so they can be handed straight back.
        public static List<string> OwnedBy(BookFile file, CalibreBook book, CalibreSettings settings, IEnumerable<BookFile> siblings = null)
        {
            var own = Format(file.Path);

            var owned = OutputFormats(settings)
                .Append(own)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var kept = (siblings ?? Enumerable.Empty<BookFile>())
                .Select(x => Format(x.Path))
                .Where(x => !x.Equals(own, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return (book?.Formats?.Keys ?? Enumerable.Empty<string>())
                .Where(x => owned.Contains(x) && !kept.Contains(x) && !IsAudio(x))
                .ToList();
        }
    }
}
