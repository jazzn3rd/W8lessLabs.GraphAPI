﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using W8lessLabs.GraphAPI.Logging;

namespace W8lessLabs.GraphAPI
{
    public class GraphService : IGraphService
    {
        internal const string GraphEndpoint = "https://graph.microsoft.com/v1.0";
        internal const string GraphEndpoint_Me = GraphEndpoint+ "/me";
        internal const string GraphEndpoint_Drives = GraphEndpoint + "/drives";
        internal const string GraphEndpoint_DriveRoot = GraphEndpoint+ "/me/drive/root";
        internal const string GraphEndpoint_DriveSpecial = GraphEndpoint + "/me/drive/special";
        internal const string GraphEndpoint_DriveItems = GraphEndpoint + "/me/drive/items";
        internal const string GraphEndpoint_DriveRootChildren = GraphEndpoint + "/me/drive/root/children";
        internal const string GraphEndpoint_Delta = GraphEndpoint + "/me/drive/root/delta";

        internal const string SkipTokenParam = "$skipToken=";
        internal const string DeltaTokenParam = "token=";

        internal const int MaxSingleFileUploadSize = 1048576 * 4; // 4 MiB

        private readonly IAuthTokenProvider _authProvider;
        private readonly IHttpService _http;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        // Tokens cache
        private Dictionary<string, AccountToken> _accountTokens;

        // keep a small LRU queue of drive items
        private LinkedList<LRUCacheEntry<GetDriveItemsResponse>> _itemsCache;
        private const int CacheLimit = 30;
        private const int MaxCacheAgeSeconds = 120;

        public GraphService(IAuthTokenProvider authProvider, IHttpService http, IJsonSerializer json, ILoggerProvider loggerProvider = null)
        {
            _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _json = json ?? throw new ArgumentNullException(nameof(json));
            _itemsCache = new LinkedList<LRUCacheEntry<GetDriveItemsResponse>>();
            _accountTokens = new Dictionary<string, AccountToken>();

            if (loggerProvider is null)
                _logger = NullLogger.Instance;
            else
                _logger = loggerProvider.GetLogger();
        }

        private async Task<(bool tokenSuccess, string token)> _TryGetTokenAsync(string accountId)
        {
            bool tokenSuccess = false;
            string token = null;
            DateTimeOffset tokenExpires = default;

            if(_accountTokens.TryGetValue(accountId, out AccountToken cachedToken))
            {
                tokenSuccess = true;
                token = cachedToken.Token;
                tokenExpires = cachedToken.TokenExpires;
            }

            if(token == null || tokenExpires == default || DateTimeOffset.Now > (tokenExpires - TimeSpan.FromSeconds(30)))
            {
                GraphTokenResult result = await _authProvider.GetTokenAsync(accountId).ConfigureAwait(false);
                tokenSuccess = result.Success;

                if(result.Success)
                {
                    token = result.AccessToken;
                    tokenExpires = result.Expires;
                    
                    // cache the token
                    _accountTokens[accountId] = new AccountToken(token, tokenExpires);
                }
            }

            return (tokenSuccess, token);
        }

        private string _GetRequestUrlForPath(string path, SpecialFolder? specialFolder = null)
        {
            string driveRoot = GraphEndpoint_DriveRoot;

            if (specialFolder.HasValue)
                driveRoot = GraphEndpoint_DriveSpecial + "/" + specialFolder.Value.ToFolderName();

            if (!(string.IsNullOrEmpty(path) || path == "/"))
                return driveRoot + ":" + Uri.EscapeUriString(path);
            else
                return driveRoot;
        }

        private string _GetRequestUrlForChildren(string path, SpecialFolder? specialFolder = null)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                if (specialFolder.HasValue)
                    return GraphEndpoint_DriveSpecial + "/" + specialFolder.Value.ToFolderName() + "/children";
                else
                    return GraphEndpoint_DriveRootChildren;
            }
            else
            {
                if(specialFolder.HasValue)
                    return GraphEndpoint_DriveSpecial + "/" + specialFolder.Value.ToFolderName() + ":" + Uri.EscapeUriString(path) + ":/children";
                else
                    return GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(path) + ":/children";
            }
        }

        private string _GetRequestUrlForContent(string path, SpecialFolder? specialFolder = null)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                if (specialFolder.HasValue)
                    return GraphEndpoint_DriveSpecial + "/" + specialFolder.Value.ToFolderName() + "/content";
                else
                    return GraphEndpoint_DriveRootChildren;
            }
            else
            {
                if (specialFolder.HasValue)
                    return GraphEndpoint_DriveSpecial + "/" + specialFolder.Value.ToFolderName() + ":" + Uri.EscapeUriString(path) + ":/content";
                else
                    return GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(path) + ":/content";
            }

            //if (string.IsNullOrEmpty(path) || path == "/")
            //    return GraphEndpoint_DriveRootChildren;
            //else
            //    return GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(path) + ":/content";
        }

        private string _GetRequestUrlForPermissions(string path, SpecialFolder? specialFolder = null)
        {
            string driveRoot = GraphEndpoint_DriveRoot;

            if (specialFolder.HasValue)
                driveRoot = GraphEndpoint_DriveSpecial + "/" + specialFolder.Value.ToFolderName();

            if (string.IsNullOrEmpty(path) || path == "/")
                return driveRoot + "/permissions";
            else
                return driveRoot + ":" + Uri.EscapeUriString(path) + ":/permissions";
        }

        private bool _TryGetRequestUrlForItemId(string driveItemId, out string requestUrl)
        {
            if (string.IsNullOrEmpty(driveItemId))
            {
                requestUrl = null;
                return false;
            }
            else
                requestUrl = GraphEndpoint_DriveItems + "/" + Uri.EscapeUriString(driveItemId);
                
            return true;
        }

        private bool _TryGetRequestUrlForPermissionsById(string driveItemId, out string requestUrl)
        {
            if (string.IsNullOrEmpty(driveItemId))
            {
                requestUrl = string.Empty;
                return false;
            }
            else
                requestUrl = GraphEndpoint_DriveItems + "/" + Uri.EscapeUriString(driveItemId) + "/permissions";
            return true;
        }

        private string _GetRequestUrlWithPaging(GetDriveItemsRequest request)
        {
            string requestUrl = _GetRequestUrlForChildren(request?.Path, request?.SpecialFolder);
            int pageSize = Math.Max(request.PageSize, GetDriveItemsRequest.MinimumPageSize);
            requestUrl += "?$top=" + pageSize;

            if (!string.IsNullOrEmpty(request.SkipToken))
                requestUrl += "&$skipToken=" + WebUtility.UrlEncode(request.SkipToken);

            return requestUrl;
        }

        private string _GetSkipToken(string nextLink)
        {
            string skipToken = null;
            if(!string.IsNullOrEmpty(nextLink))
            {
                int index = nextLink.IndexOf(SkipTokenParam, StringComparison.OrdinalIgnoreCase);
                if(index != -1)
                {
                    skipToken = nextLink.Substring(index + SkipTokenParam.Length);
                    index = skipToken.IndexOf('&');
                    if(index != -1) // there's another parameter to trim off...
                        skipToken = skipToken.Substring(0, index);
                }
            }
            return skipToken;
        }

        private HttpServiceHeadersScope _WithAuthHeader(string token) =>
            _http.WithHeaders(("Authorization", "Bearer " + token));

        private T _UnwrapResponse<T>(HttpResponseValue<T> response)
        {
            if (response.Success)
                return response.Value;
            else
            {
                _logger.Error("Request Failed - Request URI: {0} - Error Code: {1} - Response Message: {2}", 
                    response.RequestUri,
                    response.ErrorMessage?.Code,
                    response.ErrorMessage?.Message);
                return default;
            }
        }

        public async Task<GraphUser> GetMeAsync(string accountId)
        {
            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
                using (_WithAuthHeader(token))
                    return _UnwrapResponse(await _http.GetJsonAsync<GraphUser>(GraphEndpoint_Me).ConfigureAwait(false));
            else
                return null;
        }

        public async Task<DriveItem> GetDriveItemAsync(string accountId, string itemPath, SpecialFolder? specialFolder = null)
        {
            if(!string.IsNullOrEmpty(itemPath))
            {
                string requestUrl = _GetRequestUrlForPath(itemPath, specialFolder);
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                    using (_WithAuthHeader(token))
                        return _UnwrapResponse(await _http.GetJsonAsync<DriveItem>(requestUrl).ConfigureAwait(false));
            }
            return null;
        }

        public async Task<DriveItem> GetDriveItemByIdAsync(string accountId, string driveItemId)
        {
            if (_TryGetRequestUrlForItemId(driveItemId, out string requestUrl))
            {
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                    using (_WithAuthHeader(token))
                        return _UnwrapResponse(await _http.GetJsonAsync<DriveItem>(requestUrl).ConfigureAwait(false));
            }
            return null;
        }

        public async Task<GetDriveItemsResponse> GetDriveItemsAsync(string accountId, GetDriveItemsRequest request)
        {
            if (request != null)
            {
                if (request.AllowCache && _TryGetFromCache(request.GetCacheKey(), out GetDriveItemsResponse cachedResponse))
                    return cachedResponse;
                else
                {
                    string requestUrl = _GetRequestUrlWithPaging(request);
                    (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                    if (tokenSuccess)
                    {
                        using (_WithAuthHeader(token))
                        {
                            var response = await _http.GetJsonAsync<GraphResponse<DriveItem[]>>(requestUrl).ConfigureAwait(false);
                            if (response.Success)
                            {
                                var graphResponse = response.Value;
                                var driveItems = new GetDriveItemsResponse(graphResponse.Value, _GetSkipToken(graphResponse.NextLink));
                                _AddToCache(request, driveItems);
                                return driveItems;
                            }
                            else
                                return default;
                        }
                    }
                }
            }
            return null;
        }

        public async Task<GetDriveItemsDeltaResponse> GetDriveItemsDeltaAsync(string accountId, string deltaOrNextLink = null)
        {
            string requestUrl = GraphEndpoint_DriveRoot + "/delta";
            if (!string.IsNullOrEmpty(deltaOrNextLink))
                requestUrl = deltaOrNextLink;

            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    var response = await _http.GetJsonAsync<GraphResponse<DriveItem[]>>(requestUrl).ConfigureAwait(false);
                    if (response.Success)
                    {
                        var graphResponse = response.Value;
                        var driveItems = new GetDriveItemsDeltaResponse(graphResponse.Value, graphResponse.NextLink, graphResponse.DeltaLink);
                        return driveItems;
                    }
                    else
                        return default;
                }
            }
            return null;
        }

        public string GetDeltaLinkFromToken(string deltaToken)
        {
            if (deltaToken is null) throw new ArgumentNullException(nameof(deltaToken));

            return GraphEndpoint_Delta + "?" + DeltaTokenParam + Uri.EscapeDataString(deltaToken);
        }

        public async Task<int> GetChildItemsCountAsync(string accountId, string path, SpecialFolder? specialFolder = null)
        {
            int count = 0;
            string requestUrl = _GetRequestUrlForPath(path, specialFolder);
            requestUrl += "?expand=children(select=id)";

            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    var response = await _http.GetJsonAsync<DriveItem>(requestUrl).ConfigureAwait(false);
                    if(response.Success)
                        count = response.Value?.IsFolder() == true ? response.Value.Folder.ChildCount : 0;
                }
            }
            return count;
        }

        public async Task<IEnumerable<Permission>> GetPermissionsAsync(string accountId, string path, SpecialFolder? specialFolder = null)
        {
            string requestUrl = _GetRequestUrlForPermissions(path, specialFolder);
            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    return _UnwrapResponse(
                        await _http.GetJsonAsync<GraphResponse<Permission[]>>(requestUrl).ConfigureAwait(false))?.Value;
                }
            }
            return Array.Empty<Permission>();
        }

        public async Task<IEnumerable<Permission>> GetPermissionsByIdAsync(string accountId, string driveItemId)
        {
            if (_TryGetRequestUrlForPermissionsById(driveItemId, out string requestUrl))
            {
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        return _UnwrapResponse(
                            await _http.GetJsonAsync<GraphResponse<Permission[]>>(requestUrl).ConfigureAwait(false))?.Value;
                    }
                }
            }
            return Array.Empty<Permission>();
        }

        public async Task<Permission> CreateSharingLinkAsync(string accountId, string driveItemId, SharingLinkTypeOptions type, SharingLinkScopeOptions scope)
        {
            if (_TryGetRequestUrlForItemId(driveItemId, out string requestUrl))
            {
                requestUrl += "/createLink";
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                        return _UnwrapResponse(
                            // POST /me/drive/items/{itemId}/createLink
                            await _http.PostJsonAsync<Permission>(requestUrl,
                                _json.Serialize(new CreateSharingLinkRequest(type, scope))).ConfigureAwait(false));
                }
            }
            return null;
        }

        public async Task<PermissionUpdateResponse> AddPermissionAsync(string accountId, string driveItemId, SharingInvitationRequest request)
        {
            if (_TryGetRequestUrlForItemId(driveItemId, out string requestUrl))
            {
                requestUrl += "/invite";
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        // POST /me/drive/items/{item-id}/invite
                        var response = await _http.PostJsonAsync<SharingInvitationResponse>(requestUrl,
                                _json.Serialize(request)).ConfigureAwait(false);

                        // send through Unwrap as well for logging...
                        var value = _UnwrapResponse(response);

                        return new PermissionUpdateResponse(response.Success, value?.Value.FirstOrDefault(), response.ErrorMessage);
                    }
                }
            }
            return null;
        }

        public async Task<PermissionUpdateResponse> UpdatePermissionAsync(string accountId, string driveItemId, string permissionId, params PermissionRoleOptions[] roles)
        {
            if (_TryGetRequestUrlForItemId(driveItemId, out string requestUrl))
            {
                requestUrl += "/permissions/" + Uri.EscapeUriString(permissionId);
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        // PATCH /me/drive/items/{item-id}/permissions/{perm-id}
                        var response = await _http.PatchJsonAsync<Permission>(requestUrl,
                                _json.Serialize(new
                                {
                                    roles = roles.Select(r => r.AsString()).ToArray()
                                })).ConfigureAwait(false);

                        // send through Unwrap as well for logging...
                        var value = _UnwrapResponse(response);

                        return new PermissionUpdateResponse(response.Success, response.Value, response.ErrorMessage);
                    }
                }
            }
            return null;
        }

        public async Task<DriveItem> CreateFolderAsync(string accountId, string path, string newFolderName, SpecialFolder? specialFolder = null)
        {
            string requestUrl = _GetRequestUrlForChildren(path, specialFolder);
            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    var createFolder = new
                    {
                        Name = newFolderName,
                        Folder = new Folder()
                    };
                    return _UnwrapResponse(await _http.PostJsonAsync<DriveItem>(requestUrl, _json.Serialize(createFolder)).ConfigureAwait(false));
                }
            }
            return null;
        }

        private async Task<DriveItem> _UploadSmallFileAsync(string accountId, FileUploadRequest request, Stream fileContent)
        {
            string requestUrl = _GetRequestUrlForContent(request.GetFullFilePath(), request.SpecialFolder);

            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                    return _UnwrapResponse(await _http.PutBinaryAsync<DriveItem>(requestUrl, fileContent).ConfigureAwait(false));
            }
            return default;
        }

        public async Task<DriveItem> _UploadLargeFileAsync(string accountId, FileUploadRequest request, Stream fileContent)
        {
            string createSessionUrl = GraphEndpoint_DriveRoot;
            if (request.SpecialFolder.HasValue)
                createSessionUrl = GraphEndpoint_DriveSpecial + "/" + request.SpecialFolder.Value.ToFolderName();

            createSessionUrl += ":" + Uri.EscapeUriString(request.GetFullFilePath()) + ":/createUploadSession";

            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    string replace = request.Overwrite ? "replace" : "fail";
                    string fileName = _json.Serialize(request.FileName);
                    //string description = _json.Serialize(request.Description); // Only supported on ODrive Personal
                    //string fileSystemInfo = @"{ ""odata.type"": ""microsoft.graph.fileSystemInfo"" }"; // TODO - support adding FSInfo

                    string createSessionRequest = $@"{{
    ""item"": {{
        ""@microsoft.graph.conflictBehavior"": ""{replace}"",
        ""name"": {fileName}
    }}
}}";
                    var createSessionResponse = _UnwrapResponse(await _http.PostJsonAsync<CreateUploadSessionResponse>(createSessionUrl, createSessionRequest).ConfigureAwait(false));
                    if (createSessionResponse != null)
                    {
                        using (var session = new FileUploadSession(request,
                            createSessionResponse.UploadUrl,
                            createSessionResponse.ExpirationDateTime,
                            _http,
                            _json))
                        {
                            await session.UploadAsync(fileContent, request.FileSize).ConfigureAwait(false);
                        }
                    }
                }
            }
            return default;
        }

        public async Task<DriveItem> UploadFileAsync(string accountId, FileUploadRequest request, Stream fileContent)
        {
            if (string.IsNullOrEmpty(request.FilePath))
                throw new ArgumentException("FilePath", "A File Path is required.");

            if (string.IsNullOrEmpty(request.FileName))
                throw new ArgumentException("FileName", "A File Name is required.");

            return await _UploadLargeFileAsync(accountId, request, fileContent).ConfigureAwait(false);
        }

        public async Task<Stream> DownloadFileAsync(string accountId, string path, (int start, int end) range = default, SpecialFolder? specialFolder = null)
        {
            string requestUrl = _GetRequestUrlForContent(path, specialFolder);

            (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    (string header, string value)[] contentHeaders = default;
                    if (range.end > 0 && range.end > range.start)
                        contentHeaders = new[] { ("Range", string.Format("bytes={0}-{1}", range.start, range.end)) };

                    return _UnwrapResponse(await _http.GetStreamAsync(requestUrl, contentHeaders).ConfigureAwait(false));
                }
            }
            return Stream.Null;
        }

        public async Task<DriveItem> UpdateItemByIdAsync(string accountId, string driveItemId, Dictionary<string, object> updateValues)
        {
            if (_TryGetRequestUrlForItemId(driveItemId, out string requestUrl))
            {
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                    using (_WithAuthHeader(token))
                        // PATCH /me/drive/items/{item-id}
                        return _UnwrapResponse(await _http.PatchJsonAsync<DriveItem>(requestUrl, _json.Serialize(updateValues)).ConfigureAwait(false));
            }
            return null;
        }

        public async Task<bool> DeleteItemByIdAsync(string accountId, string driveItemId)
        {
            if (_TryGetRequestUrlForItemId(driveItemId, out string requestUrl))
            {
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        // DELETE /me/drive/items/{item-id}
                        var response = await _http.DeleteAsync(requestUrl).ConfigureAwait(false);
                        return response.Success;
                    }
                }
            }
            return false;
        }

        public async Task<bool> DeletePermissionByIdAsync(string accountId, string driveItemId, string permissionId)
        {
            if (_TryGetRequestUrlForItemId(driveItemId, out string requestUrl))
            {
                if (!string.IsNullOrEmpty(permissionId))
                {
                    requestUrl += "/permissions/" + Uri.EscapeUriString(permissionId);

                    (bool tokenSuccess, string token) = await _TryGetTokenAsync(accountId).ConfigureAwait(false);
                    if (tokenSuccess)
                    {
                        using (_WithAuthHeader(token))
                        {
                            // DELETE /me/drive/items/{item-id}/permissions/{perm-id}
                            var response = await _http.DeleteAsync(requestUrl).ConfigureAwait(false);
                            return response.Success;
                        }
                    }
                }
            }
            return false;
        }

        private bool _TryGetFromCache(string cacheKey, out GetDriveItemsResponse cachedResponse)
        {
            var entry = _itemsCache.FirstOrDefault(i => i.Key == cacheKey);
            bool found = false;

            if (entry != null)
            {
                if ((DateTime.Now - entry.Created).TotalSeconds > MaxCacheAgeSeconds)
                {
                    _itemsCache.Remove(entry);
                    cachedResponse = default;
                }
                else
                {
                    entry.LastUsed = DateTime.Now;
                    cachedResponse = entry.Value;
                    found = true;
                }
            }
            else
                cachedResponse = default;

            return found;
        }

        private void _AddToCache(GetDriveItemsRequest request, GetDriveItemsResponse response)
        {
            if (!_TryGetFromCache(request.GetCacheKey(), out GetDriveItemsResponse cachedResponse))
            {
                if (_itemsCache.Count > CacheLimit)
                {
                    var prune = _itemsCache.OrderBy(c => c.LastUsed).First();
                    _itemsCache.Remove(prune);
                }
                _itemsCache.AddFirst(new LRUCacheEntry<GetDriveItemsResponse>()
                {
                    Key = request.GetCacheKey(),
                    Value = response,
                    LastUsed = DateTime.Now,
                    Created = DateTime.Now
                });
            }
        }

        private class AccountToken
        {
            public AccountToken(string token, DateTimeOffset tokenExpires)
            {
                Token = token;
                TokenExpires = tokenExpires;
            }

            public string Token;
            public DateTimeOffset TokenExpires;
        }

        private class LRUCacheEntry<T>
        {
            public string Key { get; set; }
            public DateTime LastUsed { get; set; }
            public DateTime Created { get; set; }
            public T Value { get; set; }
        }

        private class CreateUploadSessionResponse
        {
            public string UploadUrl { get; set; }
            public DateTimeOffset ExpirationDateTime { get; set; }
        }
    }
}
