using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DeckSurf.Plugin.OBS.Obs
{
    /// <summary>
    /// Minimal obs-websocket v5 client (the protocol built into OBS Studio 28+),
    /// implemented directly on <see cref="ClientWebSocket"/> so the plugin carries
    /// no third-party dependencies. Maintains a persistent connection with
    /// automatic reconnect, tracks the scene list and current program scene, and
    /// exposes the requests the commands need.
    /// </summary>
    public sealed class ObsWebSocketClient : IDisposable
    {
        private const int RpcVersion = 1;

        // EventSubscription::Scenes: scene list and program scene change events.
        private const int EventSubscriptionScenes = 1 << 2;

        // EventSubscription::Outputs: record/stream output state change events.
        private const int EventSubscriptionOutputs = 1 << 6;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

        private readonly ObsConnectionSettings _settings;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingRequests = new();
        private readonly object _stateLock = new();

        private ClientWebSocket _socket;
        private Task _connectionLoop;
        private volatile bool _identified;
        private bool _disposed;

        public ObsWebSocketClient(ObsConnectionSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public event EventHandler ConnectionEstablished;

        public event EventHandler ConnectionLost;

        public event EventHandler<string> CurrentProgramSceneChanged;

        public event EventHandler<IReadOnlyList<string>> SceneListChanged;

        public event EventHandler<bool> RecordStateChanged;

        public ObsConnectionSettings Settings => _settings;

        public bool IsConnected => _identified;

        public string CurrentProgramScene { get; private set; }

        public bool IsRecording { get; private set; }

        /// <summary>
        /// Gets the failure message of the most recent connection attempt, or null
        /// once a connection is established. Lets status reporting distinguish
        /// "OBS not running" from "wrong password".
        /// </summary>
        public string LastError { get; private set; }

        public IReadOnlyList<string> Scenes { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Starts the background connection loop. Safe to call once; subsequent
        /// calls are no-ops.
        /// </summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_disposed || _connectionLoop != null)
                {
                    return;
                }

                _connectionLoop = Task.Run(() => RunAsync(_lifetime.Token));
            }
        }

        /// <summary>
        /// Waits until the client is connected and has a scene list, or the timeout
        /// elapses. Returns the scene list, empty when OBS could not be reached in
        /// time. Used by configuration tooling for one-shot scene queries.
        /// </summary>
        public async Task<IReadOnlyList<string>> WaitForScenesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

            while (Environment.TickCount64 < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (IsConnected && Scenes.Count > 0)
                {
                    return Scenes;
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return Scenes;
        }

        public Task SetCurrentProgramSceneAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            return SendRequestAsync(
                "SetCurrentProgramScene",
                new JsonObject { ["sceneName"] = sceneName },
                cancellationToken);
        }

        /// <summary>
        /// Starts recording when stopped and stops it when running. Returns the
        /// new state. ToggleRecord is used instead of StartRecord/StopRecord so a
        /// button press can never race the tracked state into an error response.
        /// </summary>
        public async Task<bool> ToggleRecordAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync("ToggleRecord", null, cancellationToken).ConfigureAwait(false);
            var isRecording = response?["outputActive"]?.GetValue<bool>() ?? !IsRecording;
            UpdateRecordingState(isRecording);
            return isRecording;
        }

        /// <summary>
        /// Renders a snapshot of a scene (or any source) and returns it as encoded
        /// image bytes. OBS composites the scene on demand, so this works for
        /// scenes that are not currently on program.
        /// </summary>
        public async Task<byte[]> GetSourceScreenshotAsync(string sourceName, int width, int height, CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync(
                "GetSourceScreenshot",
                new JsonObject
                {
                    ["sourceName"] = sourceName,
                    ["imageFormat"] = "png",
                    ["imageWidth"] = Math.Clamp(width, 8, 4096),
                    ["imageHeight"] = Math.Clamp(height, 8, 4096),
                },
                cancellationToken).ConfigureAwait(false);

            // imageData is a data URI: "data:image/png;base64,<payload>".
            var dataUri = (string)response?["imageData"]
                ?? throw new IOException($"OBS returned no image data for '{sourceName}'.");

            var separator = dataUri.IndexOf(',');
            return Convert.FromBase64String(separator >= 0 ? dataUri[(separator + 1)..] : dataUri);
        }

        public async Task<JsonObject> SendRequestAsync(string requestType, JsonObject requestData = null, CancellationToken cancellationToken = default)
        {
            var socket = _socket;
            if (socket == null || !_identified)
            {
                throw new InvalidOperationException($"Not connected to OBS at {_settings.Host}:{_settings.Port}.");
            }

            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = completion;

            try
            {
                var payload = new JsonObject
                {
                    ["op"] = 6,
                    ["d"] = new JsonObject
                    {
                        ["requestType"] = requestType,
                        ["requestId"] = requestId,
                        ["requestData"] = requestData
                    }
                };

                await SendJsonAsync(socket, payload, cancellationToken).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                timeout.CancelAfter(RequestTimeout);

                var response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

                var status = response?["requestStatus"] as JsonObject;
                if (status?["result"]?.GetValue<bool>() != true)
                {
                    throw new ObsRequestException(
                        requestType,
                        status?["code"]?.GetValue<int>() ?? -1,
                        (string)status?["comment"]);
                }

                return response["responseData"] as JsonObject;
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _lifetime.Cancel();

            try
            {
                // The connection loop owns the socket and may have already
                // disposed it by the time cancellation is observed.
                _socket?.Abort();
            }
            catch (ObjectDisposedException)
            {
            }

            _lifetime.Dispose();
        }

        private static string ComputeAuthentication(string password, string salt, string challenge)
        {
            // obs-websocket v5 auth: Base64(SHA256(Base64(SHA256(password + salt)) + challenge)).
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }

        private static async Task<JsonObject> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            using var messageBuffer = new MemoryStream();
            var chunk = new byte[16 * 1024];

            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                messageBuffer.Write(chunk, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return JsonNode.Parse(Encoding.UTF8.GetString(messageBuffer.ToArray())) as JsonObject;
        }

        private static IReadOnlyList<string> ParseSceneNames(JsonArray scenes)
        {
            if (scenes == null)
            {
                return Array.Empty<string>();
            }

            // obs-websocket reports sceneIndex 0 as the bottom of the OBS scene
            // list, so sort descending to match the top-to-bottom order in the UI.
            return scenes
                .OfType<JsonObject>()
                .OrderByDescending(s => s["sceneIndex"]?.GetValue<int>() ?? 0)
                .Select(s => (string)s["sceneName"])
                .Where(name => !string.IsNullOrEmpty(name))
                .ToArray();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var reconnectDelay = InitialReconnectDelay;

            while (!cancellationToken.IsCancellationRequested)
            {
                var sessionStart = Stopwatch.StartNew();

                try
                {
                    await ConnectAndListenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Debug.WriteLine($"OBS connection to {_settings.Host}:{_settings.Port} failed: {ex.Message}");
                }

                SetDisconnected();

                // A session that survived for a while means OBS was genuinely up,
                // so start the backoff over instead of compounding it.
                if (sessionStart.Elapsed > TimeSpan.FromSeconds(30))
                {
                    reconnectDelay = InitialReconnectDelay;
                }

                try
                {
                    await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                reconnectDelay = TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, MaxReconnectDelay.TotalSeconds));
            }
        }

        private async Task ConnectAndListenAsync(CancellationToken cancellationToken)
        {
            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("obswebsocket.json");

            await socket.ConnectAsync(new Uri($"ws://{_settings.Host}:{_settings.Port}"), cancellationToken).ConfigureAwait(false);

            var hello = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Connection closed before the OBS Hello message.");

            if (hello["op"]?.GetValue<int>() != 0)
            {
                throw new IOException("Expected an OBS Hello (op 0) message.");
            }

            var identifyData = new JsonObject
            {
                ["rpcVersion"] = RpcVersion,
                ["eventSubscriptions"] = EventSubscriptionScenes | EventSubscriptionOutputs
            };

            if ((hello["d"] as JsonObject)?["authentication"] is JsonObject authChallenge)
            {
                identifyData["authentication"] = ComputeAuthentication(
                    _settings.Password ?? string.Empty,
                    (string)authChallenge["salt"],
                    (string)authChallenge["challenge"]);
            }

            await SendJsonAsync(socket, new JsonObject { ["op"] = 1, ["d"] = identifyData }, cancellationToken).ConfigureAwait(false);

            var identified = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("OBS closed the connection during identification. Check the configured password.");

            if (identified["op"]?.GetValue<int>() != 2)
            {
                throw new IOException("OBS rejected the identification request.");
            }

            _socket = socket;
            _identified = true;
            LastError = null;
            ConnectionEstablished?.Invoke(this, EventArgs.Empty);

            // The state snapshots need the receive loop below to pump the
            // responses, so they run as a concurrent task rather than inline.
            _ = Task.Run(
                async () =>
                {
                    await TryRefreshSceneStateAsync(cancellationToken).ConfigureAwait(false);
                    await TryRefreshRecordStateAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken);

            await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
                if (message == null)
                {
                    return;
                }

                var data = message["d"] as JsonObject;

                switch (message["op"]?.GetValue<int>())
                {
                    case 5:
                        HandleEvent(data);
                        break;

                    case 7:
                        var requestId = (string)data?["requestId"];
                        if (requestId != null && _pendingRequests.TryGetValue(requestId, out var completion))
                        {
                            completion.TrySetResult(data);
                        }

                        break;
                }
            }
        }

        private void HandleEvent(JsonObject data)
        {
            var eventData = data?["eventData"] as JsonObject;

            switch ((string)data?["eventType"])
            {
                case "CurrentProgramSceneChanged":
                    UpdateCurrentScene((string)eventData?["sceneName"]);
                    break;

                case "SceneListChanged":
                    UpdateSceneList(ParseSceneNames(eventData?["scenes"] as JsonArray));
                    break;

                case "SceneNameChanged":
                    // A rename of the live scene does not emit CurrentProgramSceneChanged.
                    if ((string)eventData?["oldSceneName"] == CurrentProgramScene)
                    {
                        UpdateCurrentScene((string)eventData?["sceneName"]);
                    }

                    break;

                case "RecordStateChanged":
                    // outputActive is true only once the output is fully started,
                    // so the STARTING/STOPPING transitions render as not recording.
                    UpdateRecordingState(eventData?["outputActive"]?.GetValue<bool>() ?? false);
                    break;
            }
        }

        private async Task TryRefreshSceneStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await SendRequestAsync("GetSceneList", null, cancellationToken).ConfigureAwait(false);
                UpdateSceneList(ParseSceneNames(response?["scenes"] as JsonArray));
                UpdateCurrentScene((string)response?["currentProgramSceneName"]);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not fetch the OBS scene list: {ex.Message}");
            }
        }

        private async Task TryRefreshRecordStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await SendRequestAsync("GetRecordStatus", null, cancellationToken).ConfigureAwait(false);
                UpdateRecordingState(response?["outputActive"]?.GetValue<bool>() ?? false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not fetch the OBS record status: {ex.Message}");
            }
        }

        private void UpdateRecordingState(bool isRecording)
        {
            if (isRecording == IsRecording)
            {
                return;
            }

            IsRecording = isRecording;
            RecordStateChanged?.Invoke(this, isRecording);
        }

        private void UpdateCurrentScene(string sceneName)
        {
            if (sceneName == null || sceneName == CurrentProgramScene)
            {
                return;
            }

            CurrentProgramScene = sceneName;
            CurrentProgramSceneChanged?.Invoke(this, sceneName);
        }

        private void UpdateSceneList(IReadOnlyList<string> scenes)
        {
            if (scenes == null || scenes.SequenceEqual(Scenes))
            {
                return;
            }

            Scenes = scenes;
            SceneListChanged?.Invoke(this, scenes);
        }

        private void SetDisconnected()
        {
            var wasConnected = _identified;
            _identified = false;
            _socket = null;

            // The recording state is unknown while disconnected; reset silently
            // since keys re-render into their disconnected look via ConnectionLost.
            IsRecording = false;

            foreach (var requestId in _pendingRequests.Keys.ToArray())
            {
                if (_pendingRequests.TryRemove(requestId, out var completion))
                {
                    completion.TrySetException(new IOException("The connection to OBS was lost."));
                }
            }

            if (wasConnected)
            {
                ConnectionLost?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task SendJsonAsync(ClientWebSocket socket, JsonObject payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
