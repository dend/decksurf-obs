using System;

namespace DeckSurf.Plugin.OBS.Obs
{
    /// <summary>
    /// Thrown when obs-websocket rejects a request (op 7 response with result=false).
    /// </summary>
    public sealed class ObsRequestException : Exception
    {
        public ObsRequestException(string requestType, int code, string comment)
            : base($"OBS request '{requestType}' failed with code {code}{(string.IsNullOrEmpty(comment) ? string.Empty : $": {comment}")}")
        {
            RequestType = requestType;
            Code = code;
            Comment = comment;
        }

        public string RequestType { get; }

        public int Code { get; }

        public string Comment { get; }
    }
}
