﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using System.Web.UI;
using Orchard.Caching;
using Orchard.ContentManagement;
using Orchard.Environment.Configuration;
using Orchard.Logging;
using Orchard.Mvc.Extensions;
using Orchard.Mvc.Filters;
using Orchard.Mvc.Html;
using Orchard.OutputCache.Helpers;
using Orchard.OutputCache.Models;
using Orchard.OutputCache.Services;
using Orchard.Services;
using Orchard.UI.Admin;
using Orchard.Utility.Extensions;

namespace Orchard.OutputCache.Filters {
    public class OutputCacheFilter : FilterProvider, IActionFilter, IResultFilter, IDisposable {
        private const string REQUEST_VERIFICATION_TOKEN_BEACON_TAG = "<$request-verification-token-beacon-for-hidden-field />";
        private const string REQUEST_VERIFICATION_TOKEN_INVARIANT_TAG = "<input name=\"__RequestVerificationToken\"";
        private static string _refreshKey = "__r";
        private static long _epoch = new DateTime(2014, DateTimeKind.Utc).Ticks;

        // Dependencies.
        private readonly ICacheManager _cacheManager;
        private readonly IOutputCacheStorageProvider _cacheStorageProvider;
        private readonly IWorkContextAccessor _workContextAccessor;
        private readonly IClock _clock;
        private readonly ICacheService _cacheService;
        private readonly ISignals _signals;
        private readonly ShellSettings _shellSettings;
        private readonly IEnumerable<ICacheTagProvider> _cacheTagProviders;
        private readonly ICachingEventHandler _cachingEvents;
        private bool _isDisposed = false;

        public ILogger Logger { get; set; }

        public OutputCacheFilter(
            ICacheManager cacheManager,
            IOutputCacheStorageProvider cacheStorageProvider,
            IWorkContextAccessor workContextAccessor,
            IClock clock,
            ICacheService cacheService,
            ISignals signals,
            ShellSettings shellSettings,
            IEnumerable<ICacheTagProvider> cacheTagProviders,
            ICachingEventHandler cachingEvents) {

            _cacheManager = cacheManager;
            _cacheStorageProvider = cacheStorageProvider;
            _workContextAccessor = workContextAccessor;
            _clock = clock;
            _cacheService = cacheService;
            _signals = signals;
            _shellSettings = shellSettings;
            _cacheTagProviders = cacheTagProviders;
            _cachingEvents = cachingEvents;

            Logger = NullLogger.Instance;
        }

        // State.
        private CacheSettings _cacheSettings;
        private CacheRouteConfig _cacheRouteConfig;
        private DateTime _now;
        private WorkContext _workContext;
        private string _cacheKey;
        private string _invariantCacheKey;
        private bool _transformRedirect;
        private bool _isCachingRequest;
        private bool _etagNeedsRefresh;
        private IEnumerable<string> _ignoredRelativePaths;

        public void OnActionExecuting(ActionExecutingContext filterContext) {

            Logger.Debug("Incoming request for URL '{0}'.", filterContext.RequestContext.HttpContext.Request.RawUrl);

            // This filter is not reentrant (multiple executions within the same request are
            // not supported) so child actions are ignored completely.
            if (filterContext.IsChildAction) {
                Logger.Debug("Action '{0}' ignored because it's a child action.", filterContext.ActionDescriptor.ActionName);
                return;
            }

            _now = _clock.UtcNow;
            _workContext = _workContextAccessor.GetContext();

            var configurations = _cacheService.GetRouteConfigs();
            if (configurations.Any()) {
                var route = filterContext.Controller.ControllerContext.RouteData.Route;
                var key = _cacheService.GetRouteDescriptorKey(filterContext.HttpContext, route);
                _cacheRouteConfig = configurations.FirstOrDefault(c => c.RouteKey == key);
            }

            if (!RequestIsCacheable(filterContext))
                return;

            // Computing the cache key after we know that the request is cacheable means that we are only performing this calculation on requests that require it
            _cacheKey = String.Intern(ComputeCacheKey(filterContext, GetCacheKeyParameters(filterContext)));
            _invariantCacheKey = ComputeCacheKey(filterContext, null);

            Logger.Debug("Cache key '{0}' was created.", _cacheKey);

            try {

                // Is there a cached item, and are we allowed to serve it?
                var allowServeFromCache = filterContext.RequestContext.HttpContext.Request.Headers["Cache-Control"] != "no-cache" || CacheSettings.IgnoreNoCache;
                var cacheItem = GetCacheItem(_cacheKey);
                if (allowServeFromCache && cacheItem != null) {

                    Logger.Debug("Item '{0}' was found in cache.", _cacheKey);

                    // Is the cached item in its grace period?
                    if (cacheItem.IsInGracePeriod(_now)) {

                        // Render the content unless another request is already doing so.
                        if (Monitor.TryEnter(_cacheKey)) {
                            Logger.Debug("Item '{0}' is in grace period and not currently being rendered; rendering item...", _cacheKey);
                            BeginRenderItem(filterContext);
                            return;
                        }
                    }

                    // Cached item is not yet in its grace period, or is already being
                    // rendered by another request; serve it from cache.
                    Logger.Debug("Serving item '{0}' from cache.", _cacheKey);
                    ServeCachedItem(filterContext, cacheItem);
                    return;
                }

                // No cached item found, or client doesn't want it; acquire the cache key
                // lock to render the item.
                Logger.Debug("Item '{0}' was not found in cache or client refuses it. Acquiring cache key lock...", _cacheKey);
                if (Monitor.TryEnter(_cacheKey)) {
                    Logger.Debug("Cache key lock for item '{0}' was acquired.", _cacheKey);

                    // Item might now have been rendered and cached by another request; if so serve it from cache.
                    if (allowServeFromCache) {
                        cacheItem = GetCacheItem(_cacheKey);
                        if (cacheItem != null) {
                            Logger.Debug("Item '{0}' was now found; releasing cache key lock and serving from cache.", _cacheKey);
                            Monitor.Exit(_cacheKey);
                            ServeCachedItem(filterContext, cacheItem);
                            return;
                        }
                    }
                }

                // Either we acquired the cache key lock and the item was still not in cache, or
                // the lock acquisition timed out. In either case render the item.
                Logger.Debug("Rendering item '{0}'...", _cacheKey);
                BeginRenderItem(filterContext);

            }
            catch {
                // Remember to release the cache key lock in the event of an exception!
                Logger.Debug("Exception occurred for item '{0}'; releasing any acquired lock.", _cacheKey);
                ReleaseCacheKeyLock();
                throw;
            }
        }

        public void OnActionExecuted(ActionExecutedContext filterContext) {
            _transformRedirect = TransformRedirect(filterContext);
        }

        public void OnResultExecuting(ResultExecutingContext filterContext) {
        }

        public void OnResultExecuted(ResultExecutedContext filterContext) {
            // This filter is not reentrant (multiple executions within the same request are
            // not supported) so child actions are ignored completely.
            if (filterContext.IsChildAction)
                return;

            var captureHandlerIsAttached = false;

            try {
                if (!_isCachingRequest)
                    return;

                Logger.Debug("Item '{0}' was rendered.", _cacheKey);


                if (!ResponseIsCacheable(filterContext)) {
                    filterContext.HttpContext.Response.Cache.SetCacheability(HttpCacheability.NoCache);
                    filterContext.HttpContext.Response.Cache.SetNoStore();
                    filterContext.HttpContext.Response.Cache.SetMaxAge(new TimeSpan(0));
                    return;
                }

                // Determine duration and grace time.
                var cacheDuration = _cacheRouteConfig != null && _cacheRouteConfig.Duration.HasValue ? _cacheRouteConfig.Duration.Value : CacheSettings.DefaultCacheDuration;
                var cacheGraceTime = _cacheRouteConfig != null && _cacheRouteConfig.GraceTime.HasValue ? _cacheRouteConfig.GraceTime.Value : CacheSettings.DefaultCacheGraceTime;

                // Get the tags for this cache item from.
                var cacheItemTags = new List<string>();

                foreach (var cacheTagProvider in _cacheTagProviders) {
                    try {
                        cacheItemTags.AddRange(cacheTagProvider.GetTags());
                    }
                    catch (Exception ex) {
                        Logger.Warning(ex, "Cache tags from provider {0} will not be added to the cached item ({1}) because the provider threw an exception when asked to provide tags.", cacheTagProvider.GetType().FullName, _cacheKey);
                    }
                }

                // Capture the response output using a custom filter stream.
                var response = filterContext.HttpContext.Response;
                var captureStream = new CaptureStream(response.Filter);
                response.Filter = captureStream;

                // Add ETag header for the newly created item
                var etag = Guid.NewGuid().ToString("n");
                if (HttpRuntime.UsingIntegratedPipeline) {
                    if (response.Headers.Get("ETag") == null) {
                        response.Headers["ETag"] = etag;
                    }
                }

                captureStream.Captured += (output) => {
                    try {
                        // Since this is a callback any call to injected dependencies can result in an Autofac exception: "Instances 
                        // cannot be resolved and nested lifetimes cannot be created from this LifetimeScope as it has already been disposed."
                        // To prevent access to the original lifetime scope a new work context scope should be created here and dependencies
                        // should be resolved from it.

                        // Recheck the response status code incase it was modified before the callback.
                        if (response.StatusCode != 200) {
                            Logger.Debug("Response for item '{0}' will not be cached because status code was set to {1} during rendering.", _cacheKey, response.StatusCode);
                            return;
                        }
                        using (var scope = _workContextAccessor.CreateWorkContextScope()) {
                            // ask for the WorkContext again so it refreshes in case the scope has been disposed
                            _workContext = _workContextAccessor.GetContext(filterContext.HttpContext);
                            var cachedOutput = ReplaceRequestVerificationTokenWithBeaconTag(output, response.ContentEncoding);

                            var cacheItem = new CacheItem() {
                                CachedOnUtc = _now,
                                Duration = cacheDuration,
                                GraceTime = cacheGraceTime,
                                Output = cachedOutput,
                                ContentType = response.ContentType,
                                QueryString = filterContext.HttpContext.Request.Url.Query,
                                CacheKey = _cacheKey,
                                InvariantCacheKey = _invariantCacheKey,
                                Url = filterContext.HttpContext.Request.Url.AbsolutePath,
                                Tenant = scope.Resolve<ShellSettings>().Name,
                                StatusCode = response.StatusCode,
                                Tags = new[] { _invariantCacheKey }.Union(cacheItemTags.Distinct()).ToArray(),
                                ETag = etag
                            };

                            // Write the rendered item to the cache.
                            var cacheStorageProvider = scope.Resolve<IOutputCacheStorageProvider>();
                            cacheStorageProvider.Set(_cacheKey, cacheItem);

                            Logger.Debug("Item '{0}' was written to cache.", _cacheKey);

                            // Also add the item tags to the tag cache.
                            var tagCache = scope.Resolve<ITagCache>();
                            foreach (var tag in cacheItem.Tags) {
                                tagCache.Tag(tag, _cacheKey);
                            }
                        }
                    }
                    finally {
                        // Always release the cache key lock when the request ends.
                        ReleaseCacheKeyLock();
                    }
                };

                captureHandlerIsAttached = true;
            }
            finally {
                // If the response filter stream capture handler was attached then we'll trust
                // it to release the cache key lock at some point in the future when the stream
                // is flushed; otherwise we'll make sure we'll release it here.
                if (!captureHandlerIsAttached)
                    ReleaseCacheKeyLock();
            }
        }

        protected virtual bool RequestIsCacheable(ActionExecutingContext filterContext) {

            var itemDescriptor = string.Empty;

            if (Logger.IsEnabled(LogLevel.Debug)) {
                var url = filterContext.RequestContext.HttpContext.Request.RawUrl;
                var area = filterContext.RequestContext.RouteData.Values["area"];
                var controller = filterContext.ActionDescriptor.ControllerDescriptor.ControllerName;
                var action = filterContext.ActionDescriptor.ActionName;
                var culture = _workContext.CurrentCulture.ToLowerInvariant();
                var auth = filterContext.HttpContext.User.Identity.IsAuthenticated.ToString().ToLowerInvariant();
                var theme = _workContext.CurrentTheme.Id.ToLowerInvariant();

                itemDescriptor = string.Format("{0} (Area: {1}, Controller: {2}, Action: {3}, Culture: {4}, Theme: {5}, Auth: {6})", url, area, controller, action, culture, theme, auth);
            }

            // Respect OutputCacheAttribute if applied.
            var actionAttributes = filterContext.ActionDescriptor.GetCustomAttributes(typeof(OutputCacheAttribute), true);
            var controllerAttributes = filterContext.ActionDescriptor.ControllerDescriptor.GetCustomAttributes(typeof(OutputCacheAttribute), true);
            var outputCacheAttribute = actionAttributes.Concat(controllerAttributes).Cast<OutputCacheAttribute>().FirstOrDefault();
            if (outputCacheAttribute != null) {
                if (outputCacheAttribute.Duration <= 0 || outputCacheAttribute.NoStore || outputCacheAttribute.LocationIsIn(OutputCacheLocation.Downstream, OutputCacheLocation.Client, OutputCacheLocation.None)) {
                    Logger.Debug("Request for item '{0}' ignored based on OutputCache attribute.", itemDescriptor);
                    return false;
                }
            }

            // Don't cache POST requests.
            if (filterContext.HttpContext.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)) {
                Logger.Debug("Request for item '{0}' ignored because HTTP method is POST.", itemDescriptor);
                return false;
            }

            // Don't cache admin section requests.
            if (AdminFilter.IsApplied(new RequestContext(filterContext.HttpContext, new RouteData()))) {
                Logger.Debug("Request for item '{0}' ignored because it's in admin section.", itemDescriptor);
                return false;
            }

            // Ignore authenticated requests unless the setting to cache them is true.
            if (_workContext.CurrentUser != null && !CacheSettings.CacheAuthenticatedRequests) {
                Logger.Debug("Request for item '{0}' ignored because user is authenticated.", itemDescriptor);
                return false;
            }

            // Don't cache ignored URLs.
            if (IsIgnoredUrl(filterContext.RequestContext.HttpContext.Request.AppRelativeCurrentExecutionFilePath)) {
                Logger.Debug("Request for item '{0}' ignored because the URL is configured as ignored.", itemDescriptor);
                return false;
            }

            // Don't cache if individual route configuration says no.
            if (_cacheRouteConfig != null && _cacheRouteConfig.Duration == 0) {
                Logger.Debug("Request for item '{0}' ignored because route is configured to not be cached.", itemDescriptor);
                return false;
            }

            // Ignore requests with the refresh key on the query string.
            foreach (var key in filterContext.RequestContext.HttpContext.Request.QueryString.AllKeys) {
                if (String.Equals(_refreshKey, key, StringComparison.OrdinalIgnoreCase)) {
                    Logger.Debug("Request for item '{0}' ignored because refresh key was found on query string.", itemDescriptor);
                    return false;
                }
            }

            return true;
        }

        protected virtual bool ResponseIsCacheable(ResultExecutedContext filterContext) {

            if (filterContext.HttpContext.Request.Url == null) {
                return false;
            }

            // Don't cache non-200 responses or results of a redirect.
            var response = filterContext.HttpContext.Response;
            if (response.StatusCode != (int)HttpStatusCode.OK || _transformRedirect) {
                return false;
            }

            // Don't cache if request created notifications.
            var hasNotifications = !String.IsNullOrEmpty(Convert.ToString(filterContext.Controller.TempData["messages"]));
            if (hasNotifications) {
                Logger.Debug("Response for item '{0}' will not be cached because one or more notifications were created.", _cacheKey);
                return false;
            }

            return true;
        }

        protected virtual IDictionary<string, object> GetCacheKeyParameters(ActionExecutingContext filterContext) {
            var result = new Dictionary<string, object>();

            // Vary by action parameters.
            foreach (var p in filterContext.ActionParameters)
                result.Add("PARAM:" + p.Key, p.Value);

            // Vary by scheme.
            result.Add("scheme", filterContext.RequestContext.HttpContext.Request.Url.Scheme);

            // Vary by theme.
            result.Add("theme", _workContext.CurrentTheme.Id.ToLowerInvariant());

            // Vary for ajax vs "normal" calls
            result.Add("isajax", filterContext.HttpContext.Request.IsAjaxRequest().ToString());

            // Vary by configured query string parameters.
            var queryString = filterContext.RequestContext.HttpContext.Request.QueryString;
            foreach (var key in queryString.AllKeys) {
                if (key == null)
                    continue;

                // In exclusive mode, don't vary if the key matches
                if (CacheSettings.VaryByQueryStringIsExclusive && (CacheSettings.VaryByQueryStringParameters != null && CacheSettings.VaryByQueryStringParameters.Contains(key)))
                    continue;

                // In inclusive mode, don't vary if the key doesn't match
                if(!CacheSettings.VaryByQueryStringIsExclusive && (CacheSettings.VaryByQueryStringParameters == null || !CacheSettings.VaryByQueryStringParameters.Contains(key)))
                    continue;

                result[key] = queryString[key];
            }

            // Vary by configured request headers.
            var requestHeaders = filterContext.RequestContext.HttpContext.Request.Headers;
            foreach (var varyByRequestHeader in CacheSettings.VaryByRequestHeaders) {
                if (requestHeaders[varyByRequestHeader] != null)
                    result["HEADER:" + varyByRequestHeader] = requestHeaders[varyByRequestHeader];
            }

            // Vary by configured cookies.
            var requestCookies = filterContext.RequestContext.HttpContext.Request.Cookies;
            foreach (var varyByRequestCookies in CacheSettings.VaryByRequestCookies) {
                if (requestCookies[varyByRequestCookies] != null)
                    result["COOKIE:" + varyByRequestCookies] = requestCookies[varyByRequestCookies].Value;
            }

            // Vary by request culture if configured.
            if (CacheSettings.VaryByCulture) {
                result["culture"] = _workContext.CurrentCulture.ToLowerInvariant();
            }

            // Vary by authentication state if configured.
            if (CacheSettings.VaryByAuthenticationState) {
                result["auth"] = filterContext.HttpContext.User.Identity.IsAuthenticated.ToString().ToLowerInvariant();
            }

            return result;
        }

        protected virtual bool TransformRedirect(ActionExecutedContext filterContext) {

            // Removes the target of the redirection from cache after a POST.

            if (filterContext.Result == null) {
                throw new ArgumentNullException();
            }

            if (AdminFilter.IsApplied(new RequestContext(filterContext.HttpContext, new RouteData()))) {
                return false;
            }

            var redirectResult = filterContext.Result as RedirectResult;

            // status code can't be tested at this point, so test the result type instead
            if (redirectResult == null || !filterContext.HttpContext.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            Logger.Debug("Redirect on POST detected; removing from cache and adding refresh key.");

            var redirectUrl = redirectResult.Url;

            if (filterContext.HttpContext.Request.IsLocalUrl(redirectUrl)) {
                // Remove all cached versions of the same item.
                var helper = new UrlHelper(filterContext.HttpContext.Request.RequestContext);
                var absolutePath = new Uri(helper.MakeAbsolute(redirectUrl)).AbsolutePath;
                var invariantCacheKey = ComputeCacheKey(_shellSettings.Name, absolutePath, null);
                _cacheService.RemoveByTag(invariantCacheKey);
            }

            // Adding a refresh key so that the redirection doesn't get restored
            // from a cached version on a proxy. This can happen when using public
            // caching, we want to force the client to get a fresh copy of the
            // redirectUrl content.

            if (CacheSettings.DefaultMaxAge > 0) {
                var epIndex = redirectUrl.IndexOf('?');
                var qs = new NameValueCollection();
                if (epIndex > 0) {
                    qs = HttpUtility.ParseQueryString(redirectUrl.Substring(epIndex));
                }

                // Substract Epoch to get a smaller number.
                var refresh = _now.Ticks - _epoch;
                qs.Remove(_refreshKey);

                qs.Add(_refreshKey, refresh.ToString("x"));
                var querystring = "?" + string.Join("&", Array.ConvertAll(qs.AllKeys, k => string.Format("{0}={1}", HttpUtility.UrlEncode(k), HttpUtility.UrlEncode(qs[k]))));

                if (epIndex > 0) {
                    redirectUrl = redirectUrl.Substring(0, epIndex) + querystring;
                }
                else {
                    redirectUrl = redirectUrl + querystring;
                }
            }

            filterContext.Result = new RedirectResult(redirectUrl, redirectResult.Permanent);
            filterContext.HttpContext.Response.Cache.SetCacheability(HttpCacheability.NoCache);

            return true;
        }

        private CacheSettings CacheSettings {
            get {
                return _cacheSettings ?? (_cacheSettings = _cacheManager.Get(CacheSettings.CacheKey, true, context => {
                    context.Monitor(_signals.When(CacheSettings.CacheKey));
                    return new CacheSettings(_workContext.CurrentSite.As<CacheSettingsPart>());
                }));
            }
        }

        private IEnumerable<string> IgnoredRelativePaths {
            get {
                return _ignoredRelativePaths
                    ?? (_ignoredRelativePaths = _cacheManager.Get($"{CacheSettings.CacheKey}_IgnoredUrls", true, ContextBoundObject => {
                        ContextBoundObject.Monitor(_signals.When(CacheSettings.CacheKey));
                        return CacheSettings.IgnoredUrls
                            ?.Select(s => s.TrimStart(new[] { '~' }).Trim())
                            ?.Where(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("#"))
                            ?? Enumerable.Empty<string>();
                    }));
            }
        }

        private void ServeCachedItem(ActionExecutingContext filterContext, CacheItem cacheItem) {
            var response = filterContext.HttpContext.Response;
            var request = filterContext.HttpContext.Request;

            // Fix for missing charset in response headers
            response.Charset = response.Charset;

            // Adds some caching information to the output if requested.
            if (CacheSettings.DebugMode) {
                response.AddHeader("X-Cached-On", cacheItem.CachedOnUtc.ToString("r"));
                response.AddHeader("X-Cached-Until", cacheItem.ValidUntilUtc.ToString("r"));
            }

            // Shorcut action execution.
            filterContext.Result = new FileContentResult(
                ReplaceBeaconTagWithFreshRequestVerificationToken(cacheItem.Output, response.ContentEncoding), // replace the beacon created by the ReplaceRequestVerificationTokenWithBeacon method witha fresh new one
                cacheItem.ContentType);
            response.StatusCode = cacheItem.StatusCode;

            // Add ETag header
            var itemETag = cacheItem.ETag;
            if (HttpRuntime.UsingIntegratedPipeline && response.Headers.Get("ETag") == null && cacheItem.ETag != null) {
                if (_etagNeedsRefresh) {
                    // Add ETag header for the newly created item
                    var newEtag = Guid.NewGuid().ToString("n");
                    itemETag = "";
                    if (HttpRuntime.UsingIntegratedPipeline) {
                        if (response.Headers.Get("ETag") == null) {
                            response.Headers["ETag"] = newEtag;
                            itemETag = newEtag;
                        }
                    }
                }
                else {
                    response.Headers["ETag"] = itemETag;
                }
            }

            // Check ETag in request
            // https://www.w3.org/2005/MWI/BPWG/techs/CachingWithETag.html
            var etag = request.Headers["If-None-Match"];
            if (!String.IsNullOrEmpty(etag) && !_etagNeedsRefresh) {
                if (String.Equals(etag, itemETag, StringComparison.Ordinal)) {
                    // ETag matches the cached item, we return a 304
                    filterContext.Result = new HttpStatusCodeResult(HttpStatusCode.NotModified);
                    return;
                }
            }
            ApplyCacheControl(response);
        }

        private void BeginRenderItem(ActionExecutingContext filterContext) {

            var response = filterContext.HttpContext.Response;

            ApplyCacheControl(response);

            // Remember that we should intercept the rendered response output.
            _isCachingRequest = true;
        }

        private void ApplyCacheControl(HttpResponseBase response) {

            if (CacheSettings.DefaultMaxAge > 0) {
                var maxAge = TimeSpan.FromSeconds(CacheSettings.DefaultMaxAge); //cacheItem.ValidUntilUtc - _clock.UtcNow;
                if (maxAge.TotalMilliseconds < 0) {
                    maxAge = TimeSpan.Zero;
                }
                response.Cache.SetCacheability(HttpCacheability.Public);
                response.Cache.SetOmitVaryStar(true);
                response.Cache.SetMaxAge(maxAge);
            }

            // Keeping this example for later usage.
            // response.DisableUserCache();
            // response.DisableKernelCache();

            if (CacheSettings.VaryByQueryStringParameters == null) {
                response.Cache.VaryByParams["*"] = true;
            }
            else {
                foreach (var queryStringParam in CacheSettings.VaryByQueryStringParameters) {
                    response.Cache.VaryByParams[queryStringParam] = true;
                }
            }

            foreach (var varyRequestHeader in CacheSettings.VaryByRequestHeaders) {
                response.Cache.VaryByHeaders[varyRequestHeader] = true;
            }
        }

        private void ReleaseCacheKeyLock() {
            if (_cacheKey != null && Monitor.IsEntered(_cacheKey)) {
                Logger.Debug("Releasing cache key lock for item '{0}'.", _cacheKey);
                Monitor.Exit(_cacheKey);
                _cacheKey = null;
            }
        }

        private byte[] ReplaceRequestVerificationTokenWithBeaconTag(byte[] source, Encoding encoding) {
            // Because of the __RequestVerificationToken hidden field vary by the user, before caching the output, we need to replace the 
            // __RequestVerificationToken hidden field with a "beacon" text that will be replaced before rendering the page
            // with a fresh new __RequestVerificationToken hidden field.
            // What we do is to replace every <input name="__RequestVerificationToken" value="{the-value}" /> with
            //                                <$request-verification-token-beacon-for-hidden-field />
            if (PreventCachingRequestVerificationToken()) {
                var outputString = encoding.GetString(source);
                var resultString = new StringBuilder();
                var startIndex = 0;
                var verificationTokenTagStartIndex = outputString.IndexOf(REQUEST_VERIFICATION_TOKEN_INVARIANT_TAG, startIndex); //searches in the outputString first byte of RequestVerificationToken input tag
                while (verificationTokenTagStartIndex >= 0) {
                    resultString.Append(outputString.Substring(startIndex, verificationTokenTagStartIndex - startIndex)); //appends to resultString the text before RequestVerificationToken input tag
                    resultString.Append(REQUEST_VERIFICATION_TOKEN_BEACON_TAG); //appends the beacon placeholder tag
                    startIndex = outputString.IndexOf("/>", verificationTokenTagStartIndex) + 2; // set the new starting index after the replaced RequestVerificationToken input tag
                    verificationTokenTagStartIndex = outputString.IndexOf(REQUEST_VERIFICATION_TOKEN_INVARIANT_TAG, startIndex);// searches in the outputString next first byte of RequestVerificationToken input tag
                }
                resultString.Append(outputString.Substring(startIndex)); // completes the resultString appending the remaining characters
                return encoding.GetBytes(resultString.ToString());
            }
            return source;
        }

        private byte[] ReplaceBeaconTagWithFreshRequestVerificationToken(byte[] source, Encoding encoding) {
            // Because of the __RequestVerificationToken hidden field vary by the user, we replace the beacon generated by the ReplaceRequestVerificationTokenWithBeacon method
            // with a fresh new __RequestVerificationToken hidden field.
            // What we do is to replace every <$request-verification-token-beacon-for-hidden-field /> with
            //                                <input name="__RequestVerificationToken " value="{the-fresh-new-value}" />
            if (PreventCachingRequestVerificationToken()) {
                var outputString = encoding.GetString(source);
                var antiForgeyToken = new HtmlHelper(new ViewContext(), new ViewDataContainer()).AntiForgeryTokenOrchard();
                var resultString = outputString.Replace(REQUEST_VERIFICATION_TOKEN_BEACON_TAG, antiForgeyToken.ToString());
                _etagNeedsRefresh = outputString != resultString;
                return encoding.GetBytes(resultString);
            }
            return source;
        }

        private bool PreventCachingRequestVerificationToken() {
            return _cacheSettings.CacheAuthenticatedRequests
                && (!_cacheSettings.VaryByAuthenticationState
                    || _workContext.CurrentUser != null);
        }

        protected virtual bool IsIgnoredUrl(string url) {
            if (IgnoredRelativePaths == null || !IgnoredRelativePaths.Any()) {
                return false;
            }

            url = url.TrimStart(new[] { '~' });

            if (string.IsNullOrWhiteSpace(_shellSettings.RequestUrlPrefix)) {
                foreach (var relativePath in IgnoredRelativePaths) {
                    if (String.Equals(relativePath, url, StringComparison.OrdinalIgnoreCase)) {
                        return true;
                    }
                }
            }
            else {
                // if there is a RequestUrlPrefix, we want to check by also removing it from the
                // url we are verifying, because the configuration might have been done without it
                var tmp = url.TrimStart(new[] { '/' });
                if (tmp.StartsWith(_shellSettings.RequestUrlPrefix)) {
                    tmp = tmp.Substring(_shellSettings.RequestUrlPrefix.Length);
                }
                foreach (var relativePath in IgnoredRelativePaths) {
                    if (String.Equals(relativePath, url, StringComparison.OrdinalIgnoreCase)
                        || String.Equals(relativePath, tmp, StringComparison.OrdinalIgnoreCase)) {
                        return true;
                    }
                }
            }

            return false;
        }

        protected virtual bool IsIgnoredUrl(string url, IEnumerable<string> ignoredUrls) {
            if (ignoredUrls == null || !ignoredUrls.Any()) {
                return false;
            }

            url = url.TrimStart(new[] { '~' });

            foreach (var ignoredUrl in ignoredUrls) {
                var relativePath = ignoredUrl.TrimStart(new[] { '~' }).Trim();
                if (String.IsNullOrWhiteSpace(relativePath)) {
                    continue;
                }

                // Ignore comments
                if (relativePath.StartsWith("#")) {
                    continue;
                }

                if (String.Equals(relativePath, url, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            // repeat the check by removing the RequestUrlPrefix if it exists.
            if (!string.IsNullOrWhiteSpace(_shellSettings.RequestUrlPrefix)) {
                var tmp = url.TrimStart(new[] { '/' });
                if (tmp.StartsWith(_shellSettings.RequestUrlPrefix)) {
                    tmp = tmp.Substring(_shellSettings.RequestUrlPrefix.Length);
                    foreach (var ignoredUrl in ignoredUrls) {
                        var relativePath = ignoredUrl.TrimStart(new[] { '~' }).Trim();
                        if (String.IsNullOrWhiteSpace(relativePath)) {
                            continue;
                        }

                        // Ignore comments
                        if (relativePath.StartsWith("#")) {
                            continue;
                        }

                        if (String.Equals(relativePath, tmp, StringComparison.OrdinalIgnoreCase)) {
                            return true;
                        }
                    }
                }
            }


            return false;
        }

        protected virtual string ComputeCacheKey(ControllerContext controllerContext, IEnumerable<KeyValuePair<string, object>> parameters) {
            var url = controllerContext.HttpContext.Request.Url.AbsolutePath;
            return ComputeCacheKey(_shellSettings.Name, url, parameters);
        }

        protected virtual string ComputeCacheKey(string tenant, string absoluteUrl, IEnumerable<KeyValuePair<string, object>> parameters) {
            var keyBuilder = new StringBuilder();

            keyBuilder.AppendFormat("tenant={0};url={1};", tenant, absoluteUrl.ToLowerInvariant());

            if (parameters != null) {
                foreach (var pair in parameters) {
                    keyBuilder.AppendFormat("{0}={1};", pair.Key.ToLowerInvariant(), Convert.ToString(pair.Value).ToLowerInvariant());
                }
            }

            //make CacheKey morphable by external modules
            _cachingEvents.KeyGenerated(keyBuilder);

            return keyBuilder.ToString();
        }

        protected virtual CacheItem GetCacheItem(string key) {
            try {
                var cacheItem = _cacheStorageProvider.GetCacheItem(key);
                return cacheItem;
            }
            catch (Exception e) {
                Logger.Error(e, "An unexpected error occurred while reading a cache entry");
            }

            return null;
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_isDisposed) {
                if (disposing) {
                    // Free other state (managed objects).
                }

                if (_cacheKey != null && Monitor.IsEntered(_cacheKey)) {
                    Monitor.Exit(_cacheKey);
                }

                _isDisposed = true;
            }
        }

        ~OutputCacheFilter() {
            // Ensure locks are released even after an unexpected exception
            Dispose(false);
        }

    }

    public class ViewDataContainer : IViewDataContainer {
        public ViewDataDictionary ViewData { get; set; }
    }
}
