using DeckSurf.Plugin.OBS.Obs;
using DeckSurf.Plugin.OBS.Rendering;
using DeckSurf.SDK.Interfaces;
using DeckSurf.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeckSurf.Plugin.OBS.Commands
{
    /// <summary>
    /// Shared plumbing for OBS commands. The host creates one command instance
    /// and calls ExecuteOnActivation once per mapped button, so this base keeps a
    /// registry of per-button bindings, acquires the pooled OBS connection for
    /// each, and re-renders the affected keys when connection or scene state
    /// changes in OBS.
    /// </summary>
    public abstract class ObsCommandBase : IDeckSurfCommand, IDeckSurfStatusProvider
    {
        // Screenshots are requested at 16:9 to avoid OBS stretching the frame;
        // the renderer center-crops them to the square key.
        private const int PreviewWidth = 288;
        private const int PreviewHeight = 162;

        private readonly object _sync = new();
        private readonly Dictionary<string, ButtonBinding> _bindings = new();
        private readonly List<ObsWebSocketClient> _acquiredClients = new();
        private readonly HashSet<ObsWebSocketClient> _hookedClients = new();

        private System.Timers.Timer _previewTimer;

        public abstract string Name { get; }

        public abstract string Description { get; }

        public virtual void ExecuteOnActivation(CommandMapping mappedCommand, IConnectedDevice mappedDevice)
        {
            if (mappedCommand == null || mappedDevice == null)
            {
                return;
            }

            var binding = new ButtonBinding
            {
                Mapping = mappedCommand,
                Device = mappedDevice,
                Client = ObsConnectionManager.Acquire(ObsConnectionSettings.FromArguments(mappedCommand.CommandArguments)),
                PreviewEnabled = mappedCommand.CommandArguments.GetBoolean("preview", true),
                PreviewIntervalMs = Math.Clamp(mappedCommand.CommandArguments.GetInt32("preview_interval", 3), 1, 60) * 1000
            };

            lock (_sync)
            {
                _bindings[BindingKey(mappedCommand)] = binding;
                _acquiredClients.Add(binding.Client);

                if (_hookedClients.Add(binding.Client))
                {
                    var client = binding.Client;
                    client.ConnectionEstablished += (s, e) => RenderAllFor(client);
                    client.ConnectionLost += (s, e) => RenderAllFor(client);
                    client.CurrentProgramSceneChanged += (s, e) => RenderAllFor(client);
                    client.SceneListChanged += (s, e) => RenderAllFor(client);
                }

                if (binding.PreviewEnabled && _previewTimer == null)
                {
                    // One ticker per command instance; per-binding due times let
                    // buttons declare different refresh intervals.
                    _previewTimer = new System.Timers.Timer(1000);
                    _previewTimer.Elapsed += (s, e) => RefreshDuePreviews();
                    _previewTimer.Start();
                }
            }

            TryRender(binding);
        }

        public abstract void ExecuteOnAction(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1);

        // The interface's default implementations of these are only mapped where
        // the interface is declared, so they are surfaced here as virtuals to let
        // derived commands participate.
        public virtual Task ExecuteOnActionAsync(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1, CancellationToken cancellationToken = default)
        {
            ExecuteOnAction(mappedCommand, mappedDevice, activatingButton);
            return Task.CompletedTask;
        }

        public virtual void ExecuteOnEvent(CommandMapping mappedCommand, IConnectedDevice mappedDevice, ButtonPressEventArgs eventArgs)
        {
            ArgumentNullException.ThrowIfNull(eventArgs);

            if (eventArgs.EventKind == ButtonEventKind.Down)
            {
                ExecuteOnAction(mappedCommand, mappedDevice, eventArgs.Id);
            }
        }

        /// <summary>
        /// Reports whether the OBS instance configured in the current values is
        /// reachable, with the failure reason when it is not.
        /// </summary>
        public async Task<CommandStatus> GetStatusAsync(CommandArguments currentValues, CancellationToken cancellationToken = default)
        {
            var settings = ObsConnectionSettings.FromArguments(currentValues ?? CommandArguments.Empty);
            var client = ObsConnectionManager.Acquire(settings);

            try
            {
                await client.WaitForScenesAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);

                if (client.IsConnected)
                {
                    return CommandStatus.Ready($"Connected to OBS at {settings.Host}:{settings.Port} ({client.Scenes.Count} scenes).");
                }

                var reason = string.IsNullOrEmpty(client.LastError) ? "connection timed out." : client.LastError;
                return CommandStatus.Unavailable($"Not connected to OBS at {settings.Host}:{settings.Port}: {reason}");
            }
            catch (OperationCanceledException)
            {
                return CommandStatus.Unavailable($"Not connected to OBS at {settings.Host}:{settings.Port}: status check was canceled.");
            }
            finally
            {
                ObsConnectionManager.Release(client);
            }
        }

        public void Dispose()
        {
            List<ObsWebSocketClient> acquired;

            lock (_sync)
            {
                _previewTimer?.Stop();
                _previewTimer?.Dispose();
                _previewTimer = null;

                acquired = new List<ObsWebSocketClient>(_acquiredClients);
                _acquiredClients.Clear();
                _bindings.Clear();
                _hookedClients.Clear();
            }

            foreach (var client in acquired)
            {
                ObsConnectionManager.Release(client);
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Renders the key for a single binding. Implementations decide what the
        /// button should look like based on the client's current state.
        /// </summary>
        protected abstract void RenderBinding(ButtonBinding binding);

        /// <summary>
        /// Returns the binding registered for a mapping, activating it on the fly
        /// if the host invoked the action without a prior activation call.
        /// </summary>
        protected ButtonBinding GetOrCreateBinding(CommandMapping mappedCommand, IConnectedDevice mappedDevice)
        {
            lock (_sync)
            {
                if (_bindings.TryGetValue(BindingKey(mappedCommand), out var binding))
                {
                    return binding;
                }
            }

            ExecuteOnActivation(mappedCommand, mappedDevice);

            lock (_sync)
            {
                return _bindings.TryGetValue(BindingKey(mappedCommand), out var binding) ? binding : null;
            }
        }

        protected void TryRender(ButtonBinding binding)
        {
            // Scene keys are always command-drawn (the commands declare
            // CommandDynamicDisplay), so a button_image_path left over in an old
            // profile is ignored. Knob mappings have no key surface to draw on.
            if (binding == null || binding.Mapping.Target != MappingTarget.Key)
            {
                return;
            }

            try
            {
                RenderBinding(binding);
            }
            catch (Exception ex)
            {
                // Rendering happens on websocket callback threads; a disposed or
                // disconnected device must not take down the connection loop.
                Debug.WriteLine($"Could not render OBS key {binding.Mapping.ButtonIndex}: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts an asynchronous screenshot render of a scene onto the binding's
        /// key. Returns false when previews don't apply (disabled, no scene, or
        /// not connected) so the caller can fall back to text rendering. A render
        /// already in flight for the binding is not stacked; the current key image
        /// simply stays up until the next tick.
        /// </summary>
        protected bool TryBeginPreviewRender(ButtonBinding binding, string sceneName, bool isLive, Action renderFallback)
        {
            if (binding == null || !binding.PreviewEnabled || string.IsNullOrEmpty(sceneName) || !binding.Client.IsConnected)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref binding.PreviewGate, 1, 0) != 0)
            {
                return true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var screenshot = await binding.Client.GetSourceScreenshotAsync(sceneName, PreviewWidth, PreviewHeight).ConfigureAwait(false);
                    var image = KeyImageRenderer.RenderPreview(binding.Device.ButtonResolution, screenshot, sceneName, isLive);
                    binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not render preview of '{sceneName}': {ex.Message}");

                    try
                    {
                        renderFallback?.Invoke();
                    }
                    catch (Exception fallbackEx)
                    {
                        Debug.WriteLine($"Fallback render failed for '{sceneName}': {fallbackEx.Message}");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref binding.PreviewGate, 0);
                }
            });

            return true;
        }

        private static string BindingKey(CommandMapping mapping)
        {
            return $"{mapping.Target}:{mapping.ButtonIndex}";
        }

        private void RefreshDuePreviews()
        {
            List<ButtonBinding> due;
            var now = Environment.TickCount64;

            lock (_sync)
            {
                due = _bindings.Values
                    .Where(b => b.PreviewEnabled && b.Client.IsConnected && now >= b.NextPreviewDue)
                    .ToList();
            }

            foreach (var binding in due)
            {
                binding.NextPreviewDue = now + binding.PreviewIntervalMs;
                TryRender(binding);
            }
        }

        private void RenderAllFor(ObsWebSocketClient client)
        {
            List<ButtonBinding> affected;

            lock (_sync)
            {
                affected = _bindings.Values.Where(b => b.Client == client).ToList();
            }

            foreach (var binding in affected)
            {
                TryRender(binding);
            }
        }

        protected sealed class ButtonBinding
        {
            // Interlocked gate for the async preview render; a field because
            // Interlocked cannot target a property.
            internal int PreviewGate;

            public CommandMapping Mapping { get; init; }

            public IConnectedDevice Device { get; init; }

            public ObsWebSocketClient Client { get; init; }

            public bool PreviewEnabled { get; init; }

            public int PreviewIntervalMs { get; init; }

            public long NextPreviewDue { get; set; }
        }
    }
}
