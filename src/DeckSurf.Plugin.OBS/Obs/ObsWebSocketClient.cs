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

        // EventSubscription::Inputs: input mute and rename events.
        private const int EventSubscriptionInputs = 1 << 3;

        // EventSubscription::Outputs: record/stream/virtual camera output state
        // change events.
        private const int EventSubscriptionOutputs = 1 << 6;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        // A failed connect to a down OBS is a cheap, immediate TCP refusal, so
        // the retry cap stays short; a long cap only delays recovery after OBS
        // comes back up.
        private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

        private readonly ObsConnectionSettings _settings;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingRequests = new();
        private readonly object _stateLock = new();

        // Inputs whose mute state the commands care about. Tracked names survive
        // reconnects; the states themselves are re-fetched on every connect.
        private readonly ConcurrentDictionary<string, byte> _trackedInputs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _inputMuteStates = new(StringComparer.Ordinal);

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

        public event EventHandler<bool> VirtualCamStateChanged;

        /// <summary>
        /// Raised with the input name when a tracked input's mute state changes
        /// or becomes unknown (for example after the input is renamed in OBS).
        /// </summary>
        public event EventHandler<string> InputMuteStateChanged;

        public ObsConnectionSettings Settings => _settings;

        public bool IsConnected => _identified;

        public string CurrentProgramScene { get; private set; }

        public bool IsRecording { get; private set; }

        public bool IsRecordingPaused { get; private set; }

        public bool IsVirtualCamActive { get; private set; }

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

            // Stopping clears any pause; starting begins unpaused.
            UpdateRecordingState(isRecording, isPaused: false);
            return isRecording;
        }

        /// <summary>
        /// Pauses the recording when running and resumes it when paused. Returns
        /// the new paused state. Depending on the obs-websocket version, a call
        /// with no recording active either raises an
        /// <see cref="ObsRequestException"/> or succeeds as a no-op.
        /// </summary>
        public async Task<bool> ToggleRecordPauseAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync("ToggleRecordPause", null, cancellationToken).ConfigureAwait(false);
            var isPaused = response?["outputPaused"]?.GetValue<bool>() ?? !IsRecordingPaused;
            UpdateRecordingState(IsRecording, isPaused);
            return isPaused;
        }

        /// <summary>
        /// Starts the virtual camera when stopped and stops it when running.
        /// Returns the new state.
        /// </summary>
        public async Task<bool> ToggleVirtualCamAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync("ToggleVirtualCam", null, cancellationToken).ConfigureAwait(false);
            var isActive = response?["outputActive"]?.GetValue<bool>() ?? !IsVirtualCamActive;
            UpdateVirtualCamState(isActive);
            return isActive;
        }

        /// <summary>
        /// Registers an input whose mute state should be tracked across the
        /// connection's lifetime. The state is fetched immediately when connected
        /// and re-fetched after every reconnect.
        /// </summary>
        public void TrackInputMute(string inputName)
        {
            if (string.IsNullOrEmpty(inputName))
            {
                return;
            }

            _trackedInputs[inputName] = 0;

            if (IsConnected)
            {
                _ = TryRefreshInputMuteAsync(inputName, _lifetime.Token);
            }
        }

        /// <summary>
        /// Returns the tracked mute state of an input, or null when it is not
        /// known (disconnected, not yet fetched, or the input does not exist).
        /// </summary>
        public bool? GetInputMuteState(string inputName)
        {
            return inputName != null && _inputMuteStates.TryGetValue(inputName, out var muted) ? muted : null;
        }

        public async Task<bool> ToggleInputMuteAsync(string inputName, CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync(
                "ToggleInputMute",
                new JsonObject { ["inputName"] = inputName },
                cancellationToken).ConfigureAwait(false);

            var muted = response?["inputMuted"]?.GetValue<bool>() ?? false;
            UpdateInputMuteState(inputName, muted);
            return muted;
        }

        /// <summary>
        /// Waits until the client is connected or the timeout elapses. Used by
        /// configuration tooling before one-shot queries.
        /// </summary>
        public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

            while (!IsConnected && Environment.TickCount64 < deadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return IsConnected;
        }

        /// <summary>
        /// Returns the names of inputs that can be muted: the special outputs
        /// (microphones, desktop audio) first, then every other input that
        /// accepts a mute query. Used by configuration tooling to populate the
        /// input picker.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetMutableInputsAsync(CancellationToken cancellationToken = default)
        {
            var candidates = new List<string>();

            var special = await SendRequestAsync("GetSpecialInputs", null, cancellationToken).ConfigureAwait(false);
            foreach (var key in new[] { "mic1", "mic2", "mic3", "mic4", "desktop1", "desktop2" })
            {
                var name = (string)special?[key];
                if (!string.IsNullOrEmpty(name) && !candidates.Contains(name))
                {
                    candidates.Add(name);
                }
            }

            var list = await SendRequestAsync("GetInputList", null, cancellationToken).ConfigureAwait(false);
            foreach (var input in (list?["inputs"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var name = (string)input["inputName"];
                if (!string.IsNullOrEmpty(name) && !candidates.Contains(name))
                {
                    candidates.Add(name);
                }
            }

            // Only audio-capable inputs answer GetInputMute; video-only sources
            // error out and are filtered from the picker.
            var mutable = new List<string>();
            foreach (var name in candidates)
            {
                try
                {
                    await SendRequestAsync("GetInputMute", new JsonObject { ["inputName"] = name }, cancellationToken).ConfigureAwait(false);
                    mutable.Add(name);
                }
                catch (ObsRequestException)
                {
                }
            }

            return mutable;
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
                ["eventSubscriptions"] = EventSubscriptionScenes | EventSubscriptionInputs | EventSubscriptionOutputs
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
                    await TryRefreshVirtualCamStateAsync(cancellationToken).ConfigureAwait(false);

                    foreach (var inputName in _trackedInputs.Keys.ToArray())
                    {
                        await TryRefreshInputMuteAsync(inputName, cancellationToken).ConfigureAwait(false);
                    }
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
                    HandleRecordStateEvent(eventData);
                    break;

                case "VirtualcamStateChanged":
                    // outputActive is true only once the output is fully started,
                    // so the STARTING/STOPPING transitions render as stopped.
                    UpdateVirtualCamState(eventData?["outputActive"]?.GetValue<bool>() ?? false);
                    break;

                case "InputMuteStateChanged":
                    UpdateInputMuteState((string)eventData?["inputName"], eventData?["inputMuted"]?.GetValue<bool>() ?? false);
                    break;

                case "InputNameChanged":
                    // The binding keeps pointing at the old name, so its state is
                    // simply no longer known; the key re-renders into the unknown
                    // look until the mapping is updated.
                    var oldName = (string)eventData?["oldInputName"];
                    if (oldName != null && _inputMuteStates.TryRemove(oldName, out _))
                    {
                        InputMuteStateChanged?.Invoke(this, oldName);
                    }

                    break;
            }
        }

        private void HandleRecordStateEvent(JsonObject eventData)
        {
            // Pause state is derived from outputState rather than outputActive:
            // obs-websocket reports outputActive as false in the PAUSED event even
            // though the recording still exists. STARTING/STOPPING transitions
            // fall through to outputActive and render as not recording.
            switch ((string)eventData?["outputState"])
            {
                case "OBS_WEBSOCKET_OUTPUT_STARTED":
                case "OBS_WEBSOCKET_OUTPUT_RESUMED":
                    UpdateRecordingState(isRecording: true, isPaused: false);
                    break;

                case "OBS_WEBSOCKET_OUTPUT_PAUSED":
                    UpdateRecordingState(isRecording: true, isPaused: true);
                    break;

                case "OBS_WEBSOCKET_OUTPUT_STOPPED":
                    UpdateRecordingState(isRecording: false, isPaused: false);
                    break;

                default:
                    UpdateRecordingState(eventData?["outputActive"]?.GetValue<bool>() ?? false, isPaused: false);
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

                // Early obs-websocket 5.0 builds shipped the paused flag with a
                // typo ("ouputPaused"); read both so those versions still work.
                var isPaused = (response?["outputPaused"] ?? response?["ouputPaused"])?.GetValue<bool>() ?? false;
                UpdateRecordingState(response?["outputActive"]?.GetValue<bool>() ?? false, isPaused);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not fetch the OBS record status: {ex.Message}");
            }
        }

        private async Task TryRefreshVirtualCamStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await SendRequestAsync("GetVirtualCamStatus", null, cancellationToken).ConfigureAwait(false);
                UpdateVirtualCamState(response?["outputActive"]?.GetValue<bool>() ?? false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not fetch the OBS virtual camera status: {ex.Message}");
            }
        }

        private async Task TryRefreshInputMuteAsync(string inputName, CancellationToken cancellationToken)
        {
            try
            {
                var response = await SendRequestAsync(
                    "GetInputMute",
                    new JsonObject { ["inputName"] = inputName },
                    cancellationToken).ConfigureAwait(false);

                UpdateInputMuteState(inputName, response?["inputMuted"]?.GetValue<bool>() ?? false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not fetch the OBS mute state of '{inputName}': {ex.Message}");
            }
        }

        private void UpdateRecordingState(bool isRecording, bool isPaused)
        {
            // A pause can only exist within a recording. Guards the optimistic
            // update in ToggleRecordPauseAsync: newer obs-websocket versions
            // accept ToggleRecordPause with no recording running instead of
            // erroring, which must not wedge the state into paused-while-idle.
            isPaused &= isRecording;

            if (isRecording == IsRecording && isPaused == IsRecordingPaused)
            {
                return;
            }

            IsRecording = isRecording;
            IsRecordingPaused = isPaused;
            RecordStateChanged?.Invoke(this, isRecording);
        }

        private void UpdateVirtualCamState(bool isActive)
        {
            if (isActive == IsVirtualCamActive)
            {
                return;
            }

            IsVirtualCamActive = isActive;
            VirtualCamStateChanged?.Invoke(this, isActive);
        }

        private void UpdateInputMuteState(string inputName, bool muted)
        {
            if (string.IsNullOrEmpty(inputName))
            {
                return;
            }

            if (_inputMuteStates.TryGetValue(inputName, out var existing) && existing == muted)
            {
                return;
            }

            _inputMuteStates[inputName] = muted;
            InputMuteStateChanged?.Invoke(this, inputName);
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

            // Output and mute states are unknown while disconnected; reset
            // silently since keys re-render into their disconnected look via
            // ConnectionLost. Tracked input names are kept for the reconnect.
            IsRecording = false;
            IsRecordingPaused = false;
            IsVirtualCamActive = false;
            _inputMuteStates.Clear();

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
