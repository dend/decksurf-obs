using DeckSurf.Plugin.OBS.Obs;
using DeckSurf.Plugin.OBS.Rendering;
using DeckSurf.SDK.Interfaces;
using DeckSurf.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DeckSurf.Plugin.OBS.Commands
{
    [CommandDynamicDisplay]
    [CommandParameter("scene", CommandParameterType.String, DisplayName = "Scene name", Description = "Name of the OBS scene to switch to, exactly as it appears in OBS.", Required = true, DynamicChoices = true, Order = 0)]
    [CommandParameter("host", CommandParameterType.String, DisplayName = "OBS host", Description = "Host name or IP address of the obs-websocket server.", DefaultValue = "127.0.0.1", Order = 1)]
    [CommandParameter("port", CommandParameterType.Integer, DisplayName = "OBS port", Description = "Port of the obs-websocket server.", DefaultValue = "4455", MinValue = 1, MaxValue = 65535, Order = 2)]
    [CommandParameter("password", CommandParameterType.Secret, DisplayName = "OBS password", Description = "obs-websocket password. Leave empty when authentication is disabled.", Order = 3)]
    [CommandParameter("preview", CommandParameterType.Boolean, DisplayName = "Scene preview on key", Description = "Render a periodically refreshed snapshot of the scene as the button image.", DefaultValue = "true", Order = 4)]
    [CommandParameter("preview_interval", CommandParameterType.Integer, DisplayName = "Preview refresh (seconds)", Description = "How often the scene snapshot is refreshed.", DefaultValue = "3", MinValue = 1, MaxValue = 60, Order = 5)]
    public class SwitchScene : ObsCommandBase, IDeckSurfChoiceProvider
    {
        public override string Name => "Switch scene";

        public override string Description => "Switches OBS Studio to a specific scene, highlighting the button while that scene is live.";

        public override void ExecuteOnAction(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1)
        {
            _ = ExecuteOnActionAsync(mappedCommand, mappedDevice, activatingButton);
        }

        public override async Task ExecuteOnActionAsync(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1, CancellationToken cancellationToken = default)
        {
            var binding = GetOrCreateBinding(mappedCommand, mappedDevice);
            var sceneName = GetSceneName(mappedCommand);

            if (binding == null || string.IsNullOrEmpty(sceneName))
            {
                return;
            }

            try
            {
                await binding.Client.SetCurrentProgramSceneAsync(sceneName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not switch OBS to scene '{sceneName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Serves the scene list to configuration tooling. Reuses the pooled
        /// connection, so when buttons for this OBS instance are already active the
        /// list returns instantly; otherwise a fresh connection gets a few seconds
        /// to come up before giving up and returning empty.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetChoicesAsync(string parameterKey, CommandArguments currentValues, CancellationToken cancellationToken = default)
        {
            if (parameterKey != "scene")
            {
                return Array.Empty<string>();
            }

            var client = ObsConnectionManager.Acquire(ObsConnectionSettings.FromArguments(currentValues ?? CommandArguments.Empty));

            try
            {
                return await client.WaitForScenesAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<string>();
            }
            finally
            {
                ObsConnectionManager.Release(client);
            }
        }

        protected override void RenderBinding(ButtonBinding binding)
        {
            var sceneName = GetSceneName(binding.Mapping);

            var state = !binding.Client.IsConnected
                ? KeyVisualState.Disconnected
                : binding.Client.CurrentProgramScene == sceneName
                    ? KeyVisualState.Active
                    : KeyVisualState.Inactive;

            if (TryBeginPreviewRender(binding, sceneName, state == KeyVisualState.Active, () => RenderTextKey(binding, sceneName, state)))
            {
                return;
            }

            RenderTextKey(binding, sceneName, state);
        }

        private static void RenderTextKey(ButtonBinding binding, string sceneName, KeyVisualState state)
        {
            var image = KeyImageRenderer.Render(
                binding.Device.ButtonResolution,
                string.IsNullOrEmpty(sceneName) ? "OBS" : sceneName,
                state);

            binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
        }

        private static string GetSceneName(CommandMapping mapping)
        {
            return mapping.CommandArguments.GetString("scene");
        }
    }
}
