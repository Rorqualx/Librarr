using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;

namespace Readarr.Http.Extensions
{
    public static class RequestExtensions
    {
        // See src/Readarr.Api.V1/Queue/QueueModule.cs
        private static readonly HashSet<string> VALID_SORT_KEYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "authors.sortname", //Workaround authors table properties not being added on isValidSortKey call
            "timeleft",
            "estimatedCompletionTime",
            "protocol",
            "indexer",
            "downloadClient",
            "quality",
            "status",
            "title",
            "progress"
        };

        private static readonly HashSet<string> EXCLUDED_KEYS = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "page",
            "pageSize",
            "sortKey",
            "sortDirection",
            "filterKey",
            "filterValue",
        };

        public static bool IsApiRequest(this HttpRequest request)
        {
            return request.Path.StartsWithSegments("/api", StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool GetBooleanQueryParameter(this HttpRequest request, string parameter, bool defaultValue = false)
        {
            var parameterValue = request.Query[parameter];

            if (parameterValue.Any())
            {
                return bool.Parse(parameterValue.ToString());
            }

            return defaultValue;
        }

        public static PagingResource<TResource> ApplyToPage<TResource, TModel>(this PagingSpec<TModel> pagingSpec, Func<PagingSpec<TModel>, PagingSpec<TModel>> function, Converter<TModel, TResource> mapper)
        {
            pagingSpec = function(pagingSpec);

            return new PagingResource<TResource>
            {
                Page = pagingSpec.Page,
                PageSize = pagingSpec.PageSize,
                SortDirection = pagingSpec.SortDirection,
                SortKey = pagingSpec.SortKey,
                TotalRecords = pagingSpec.TotalRecords,
                Records = pagingSpec.Records.ConvertAll(mapper)
            };
        }

        public static string GetRemoteIP(this HttpContext context)
        {
            return context?.Request?.GetRemoteIP() ?? "Unknown";
        }

        public static string GetRemoteIP(this HttpRequest request)
        {
            if (request == null)
            {
                return "Unknown";
            }

            var remoteIP = request.HttpContext.Connection.RemoteIpAddress;

            if (remoteIP.IsIPv4MappedToIPv6)
            {
                remoteIP = remoteIP.MapToIPv4();
            }

            return remoteIP.ToString();
        }

        // Whether the request comes from this host or a private network, for the
        // "authentication disabled for local addresses" setting.
        //
        // RemoteIpAddress is the connection's peer unless a trusted proxy vouched
        // for another address (see TrustedForwardedHeaders). An X-Forwarded-For
        // that is still on the request at this point was not consumed by that
        // middleware: it came from a peer we do not trust, or it names a client
        // that could not be resolved. Either way we do not know who is really on
        // the other end, so the request is not local.
        public static bool IsLocalRequest(this HttpContext context, bool trustCgnatIpAddresses)
        {
            var remoteIP = context?.Connection?.RemoteIpAddress;

            if (remoteIP == null)
            {
                return false;
            }

            if (context.Request.Headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName))
            {
                return false;
            }

            if (remoteIP.IsIPv4MappedToIPv6)
            {
                remoteIP = remoteIP.MapToIPv4();
            }

            return remoteIP.IsLocalAddress() ||
                   (trustCgnatIpAddresses && remoteIP.IsCgnatIpAddress());
        }

        public static void DisableCache(this IHeaderDictionary headers)
        {
            headers.Remove("Last-Modified");
            headers["Cache-Control"] = "no-cache, no-store";
            headers["Expires"] = "-1";
            headers["Pragma"] = "no-cache";
        }

        public static void EnableCache(this IHeaderDictionary headers)
        {
            headers["Cache-Control"] = "max-age=31536000, public";
            headers["Last-Modified"] = BuildInfo.BuildDateTime.ToString("r");
        }
    }
}
