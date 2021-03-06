﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using W8lessLabs.GraphAPI.Logging;

namespace W8lessLabs.GraphAPI
{
    /// <summary>
    /// Sets and restores HTTP Headers
    /// </summary>
    public class HttpServiceHeadersScope : IDisposable
    {
        private HttpClient _http;
        List<(string name, IEnumerable<string> value)> _previousValues;
        List<(string name, string value)> _addedValues;

        public HttpServiceHeadersScope(HttpClient http, (string name, string value)[] headers) :
            this(http, headers, NullLogger.Instance)
        {

        }

        internal HttpServiceHeadersScope(HttpClient http, (string name, string value)[] headers, ILogger logger)
        {
            _http = http;
            _previousValues = new List<(string, IEnumerable<string>)>();
            _addedValues = new List<(string name, string value)>();

            var defaultHeaders = _http.DefaultRequestHeaders;
            foreach ((string name, string value) in headers)
            {
                if("Authorization".Equals(name, StringComparison.OrdinalIgnoreCase))
                    logger.Trace("Adding Scoped Header: {0} = {1}", name, "<value omitted>");
                else
                    logger.Trace("Adding Scoped Header: {0} = {1}", name, value);

                if (defaultHeaders.TryGetValues(name, out IEnumerable<string> existingValue))
                {
                    _previousValues.Add((name, existingValue));
                    defaultHeaders.Remove(name);
                    defaultHeaders.TryAddWithoutValidation(name, value);
                }
                else
                    defaultHeaders.TryAddWithoutValidation(name, value);

                _addedValues.Add((name, value));
            }
        }

        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    var defaultHeaders = _http.DefaultRequestHeaders;
                    foreach ((string addedName, _) in _addedValues)
                        defaultHeaders.Remove(addedName);

                    foreach ((string name, IEnumerable<string> values) in _previousValues)
                        defaultHeaders.Add(name, values);
                }

                disposedValue = true;
            }
        }

        public void Dispose() =>
            Dispose(true);
    }
}
