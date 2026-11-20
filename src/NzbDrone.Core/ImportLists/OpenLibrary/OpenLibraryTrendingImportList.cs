using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.OpenLibrary
{
    // Phase 6 import list: OL trending works for the configured period.
    //   GET /trending/{period}.json?limit={n}
    // Period is one of: now, daily, weekly, monthly, yearly, forever.
    // Each entry is OpenLibraryTrendingWork (search-doc shape, not the
    // canonical work resource).
    public class OpenLibraryTrendingImportList : ImportListBase<OpenLibraryTrendingImportListSettings>
    {
        private static readonly HashSet<string> ValidPeriods = new(StringComparer.OrdinalIgnoreCase)
        {
            "now",
            "daily",
            "weekly",
            "monthly",
            "yearly",
            "forever"
        };

        private readonly IHttpClient _httpClient;
        private readonly IOpenLibraryRequestBuilder _requestBuilder;

        public override string Name => "Open Library Trending";
        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        public OpenLibraryTrendingImportList(IHttpClient httpClient,
                                             IOpenLibraryRequestBuilder requestBuilder,
                                             IImportListStatusService importListStatusService,
                                             IConfigService configService,
                                             IParsingService parsingService,
                                             Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _httpClient = httpClient;
            _requestBuilder = requestBuilder;
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var result = new List<ImportListItemInfo>();

            try
            {
                var period = (Settings.Period ?? "weekly").Trim().ToLowerInvariant();
                if (!ValidPeriods.Contains(period))
                {
                    _logger.Warn("OL trending period '{0}' is not one of {1}; skipping fetch", period, string.Join(", ", ValidPeriods));
                    _importListStatusService.RecordFailure(Definition.Id);
                    return result;
                }

                var request = _requestBuilder.For($"trending/{period}.json?limit={Settings.Limit}").Build();
                var response = _httpClient.Get<OpenLibraryTrendingResource>(request);

                if (response?.Resource?.Works == null)
                {
                    _importListStatusService.RecordSuccess(Definition.Id);
                    return result;
                }

                foreach (var work in response.Resource.Works)
                {
                    if (work?.Key.IsNullOrWhiteSpace() != false)
                    {
                        continue;
                    }

                    var authorName = work.AuthorName?.FirstOrDefault();
                    var authorKey = work.AuthorKey?.FirstOrDefault();

                    result.Add(new ImportListItemInfo
                    {
                        BookGoodreadsId = ExtractKey(work.Key),
                        Book = work.Title,
                        EditionGoodreadsId = null,
                        Author = authorName,
                        AuthorGoodreadsId = authorKey
                    });
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "OL trending import list failed");
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(result);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
        }

        private ValidationFailure TestConnection()
        {
            try
            {
                var period = (Settings.Period ?? "weekly").Trim().ToLowerInvariant();
                if (!ValidPeriods.Contains(period))
                {
                    return new ValidationFailure(nameof(Settings.Period), $"Period must be one of: {string.Join(", ", ValidPeriods)}");
                }

                var request = _requestBuilder.For($"trending/{period}.json?limit=1").Build();
                _httpClient.Get<OpenLibraryTrendingResource>(request);
                return null;
            }
            catch (HttpException e)
            {
                _logger.Warn(e, "OL trending probe failed");
                if (e.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new ValidationFailure(nameof(Settings.Period), $"OL trending endpoint not found for period '{Settings.Period}'");
                }

                return new ValidationFailure(nameof(Settings.Period), "Could not reach Open Library");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Open Library");
                return new ValidationFailure(string.Empty, "Unable to connect to import list, check the log for more details");
            }
        }

        private static string ExtractKey(string olKey)
        {
            if (olKey.IsNullOrWhiteSpace())
            {
                return olKey;
            }

            var slash = olKey.LastIndexOf('/');
            return slash >= 0 ? olKey.Substring(slash + 1) : olKey;
        }
    }
}
