using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using Readarr.Http.REST;

namespace Readarr.Api.V1.Author
{
    public class AuthorResource : RestResource
    {
        //Todo: Sorters should be done completely on the client
        //Todo: Is there an easy way to keep IgnoreArticlesWhenSorting in sync between, Series, History, Missing?
        //Todo: We should get the entire Profile instead of ID and Name separately
        [JsonIgnore]
        public int AuthorMetadataId { get; set; }
        public AuthorStatusType Status { get; set; }

        public bool Ended => Status == AuthorStatusType.Ended;

        public string AuthorName { get; set; }
        public string AuthorNameLastFirst { get; set; }
        public string ForeignAuthorId { get; set; }
        public string TitleSlug { get; set; }
        public string Overview { get; set; }
        public string Disambiguation { get; set; }

        // Search-result only; zero/null for anything loaded from the
        // database. See AuthorMetadata for why these aren't persisted.
        public int WorkCount { get; set; }
        public string TopWork { get; set; }
        public List<Links> Links { get; set; }

        public Book NextBook { get; set; }
        public Book LastBook { get; set; }

        public List<MediaCover> Images { get; set; }

        public string RemotePoster { get; set; }

        //View & Edit
        public string Path { get; set; }
        public int QualityProfileId { get; set; }

        // 0 = single-format author; every format uses QualityProfileId. Set
        // it and audiobook releases get their own profile and cutoff, which
        // is what lets one author hold both an ebook and an audiobook.
        //
        // Nullable so an update can tell "not sent" from "set to none". A
        // client written before the field existed reads an author, changes
        // something else and PUTs back a body without it; binding that as 0
        // would quietly make the author single-format again. Responses
        // always carry a number.
        public int? AudiobookQualityProfileId { get; set; }
        public int MetadataProfileId { get; set; }

        //Editing Only
        public bool Monitored { get; set; }
        public NewItemMonitorTypes MonitorNewItems { get; set; }

        public string RootFolderPath { get; set; }
        public string Folder { get; set; }
        public List<string> Genres { get; set; }
        public string CleanName { get; set; }
        public string SortName { get; set; }
        public string SortNameLastFirst { get; set; }

        public HashSet<int> Tags { get; set; }
        public DateTime Added { get; set; }
        public AddAuthorOptions AddOptions { get; set; }
        public Ratings Ratings { get; set; }

        public AuthorStatisticsResource Statistics { get; set; }
    }

    public static class AuthorResourceMapper
    {
        public static AuthorResource ToResource(this NzbDrone.Core.Books.Author model)
        {
            if (model == null)
            {
                return null;
            }

            return new AuthorResource
            {
                Id = model.Id,
                AuthorMetadataId = model.AuthorMetadataId,

                AuthorName = model.Name,
                AuthorNameLastFirst = model.Metadata.Value.NameLastFirst,

                //AlternateTitles
                SortName = model.Metadata.Value.SortName,
                SortNameLastFirst = model.Metadata.Value.SortNameLastFirst,

                Status = model.Metadata.Value.Status,
                Overview = model.Metadata.Value.Overview,
                Disambiguation = model.Metadata.Value.Disambiguation,
                WorkCount = model.Metadata.Value.WorkCount,
                TopWork = model.Metadata.Value.TopWork,

                Images = model.Metadata.Value.Images.JsonClone(),

                Path = model.Path,
                QualityProfileId = model.QualityProfileId,
                AudiobookQualityProfileId = model.AudiobookQualityProfileId,
                MetadataProfileId = model.MetadataProfileId,
                Links = model.Metadata.Value.Links,

                Monitored = model.Monitored,
                MonitorNewItems = model.MonitorNewItems,

                CleanName = model.CleanName,
                ForeignAuthorId = model.Metadata.Value.ForeignAuthorId,
                TitleSlug = model.Metadata.Value.TitleSlug,

                // Root folder path is now calculated from the author path
                // RootFolderPath = model.RootFolderPath,
                Genres = model.Metadata.Value.Genres,
                Tags = model.Tags,
                Added = model.Added,
                AddOptions = model.AddOptions,
                Ratings = model.Metadata.Value.Ratings,

                Statistics = new AuthorStatisticsResource()
            };
        }

        public static NzbDrone.Core.Books.Author ToModel(this AuthorResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new NzbDrone.Core.Books.Author
            {
                Id = resource.Id,

                Metadata = new NzbDrone.Core.Books.AuthorMetadata
                {
                    ForeignAuthorId = resource.ForeignAuthorId,
                    TitleSlug = resource.TitleSlug,
                    Name = resource.AuthorName,
                    NameLastFirst = resource.AuthorNameLastFirst,
                    SortName = resource.SortName,
                    SortNameLastFirst = resource.SortNameLastFirst,
                    Status = resource.Status,
                    Overview = resource.Overview,
                    Links = resource.Links,
                    Images = resource.Images,
                    Genres = resource.Genres,
                    Ratings = resource.Ratings,
                },

                //AlternateTitles
                Path = resource.Path,
                QualityProfileId = resource.QualityProfileId,
                AudiobookQualityProfileId = resource.AudiobookQualityProfileId ?? 0,
                MetadataProfileId = resource.MetadataProfileId,

                Monitored = resource.Monitored,
                MonitorNewItems = resource.MonitorNewItems,

                CleanName = resource.CleanName,
                RootFolderPath = resource.RootFolderPath,

                Tags = resource.Tags,
                Added = resource.Added,
                AddOptions = resource.AddOptions
            };
        }

        public static NzbDrone.Core.Books.Author ToModel(this AuthorResource resource, NzbDrone.Core.Books.Author author)
        {
            var updatedAuthor = resource.ToModel();

            // ApplyChanges copies the audiobook profile unconditionally, so
            // an absent field has to be turned back into the stored value
            // here, where "absent" is still visible. An explicit 0 from the
            // UI is not absent and still clears it.
            if (!resource.AudiobookQualityProfileId.HasValue)
            {
                updatedAuthor.AudiobookQualityProfileId = author.AudiobookQualityProfileId;
                updatedAuthor.AudiobookQualityProfile = author.AudiobookQualityProfile;
            }

            author.ApplyChanges(updatedAuthor);

            return author;
        }

        public static List<AuthorResource> ToResource(this IEnumerable<NzbDrone.Core.Books.Author> author)
        {
            return author.Select(ToResource).ToList();
        }

        public static List<NzbDrone.Core.Books.Author> ToModel(this IEnumerable<AuthorResource> resources)
        {
            return resources.Select(ToModel).ToList();
        }
    }
}
