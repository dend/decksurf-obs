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
    [CommandParameter("input", CommandParameterType.String, DisplayName = "Audio input", Description = "Name of the OBS audio input to mute, exactly as it appears in the OBS audio mixer.", Required = true, DynamicChoices = true, Order = 0)]
    [CommandParameter("host", CommandParameterType.String, DisplayName = "OBS host", Description = "Host name or IP address of the obs-websocket server.", DefaultValue = "127.0.0.1", Order = 1)]
    [CommandParameter("port", CommandParameterType.Integer, DisplayName = "OBS port", Description = "Port of the obs-websocket server.", DefaultValue = "4455", MinValue = 1, MaxValue = 65535, Order = 2)]
    [CommandParameter("password", CommandParameterType.Secret, DisplayName = "OBS password", Description = "obs-websocket password. Leave empty when authentication is disabled.", Order = 3)]
    public class ToggleMute : ObsCommandBase, IDeckSurfChoiceProvider
    {
        public override string Name => "Toggle mute";

        public override string Description => "Mutes or unmutes an OBS audio input, such as a microphone. The key shows a red slashed microphone while the input is muted, tracking mute changes made in OBS itself as well.";

        protected override bool SupportsPreview => false;

        public override void ExecuteOnActivation(CommandMapping mappedCommand, IConnectedDevice mappedDevice)
        {
            base.ExecuteOnActivation(mappedCommand, mappedDevice);

            if (mappedCommand == null || mappedDevice == null)
            {
                return;
            }

            // Registers the input with the shared client so its mute state is
            // fetched now and again after every reconnect.
            GetOrCreateBinding(mappedCommand, mappedDevice)?.Client.TrackInputMute(GetInputName(mappedCommand));
        }

        public override void ExecuteOnAction(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1)
        {
            _ = ExecuteOnActionAsync(mappedCommand, mappedDevice, activatingButton);
        }

        public override async Task ExecuteOnActionAsync(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1, CancellationToken cancellationToken = default)
        {
            var binding = GetOrCreateBinding(mappedCommand, mappedDevice);
            var inputName = GetInputName(mappedCommand);

            if (binding == null || string.IsNullOrEmpty(inputName))
            {
                return;
            }

            try
            {
                await binding.Client.ToggleInputMuteAsync(inputName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not toggle mute of OBS input '{inputName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Serves the audio input list to configuration tooling. Reuses the pooled
        /// connection, so when buttons for this OBS instance are already active
        /// the list returns quickly; otherwise a fresh connection gets a few
        /// seconds to come up before giving up and returning empty.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetChoicesAsync(string parameterKey, CommandArguments currentValues, CancellationToken cancellationToken = default)
        {
            if (parameterKey != "input")
            {
                return Array.Empty<string>();
            }

            var client = ObsConnectionManager.Acquire(ObsConnectionSettings.FromArguments(currentValues ?? CommandArguments.Empty));

            try
            {
                if (!await client.WaitForConnectionAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false))
                {
                    return Array.Empty<string>();
                }

                return await client.GetMutableInputsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not list OBS audio inputs: {ex.Message}");
                return Array.Empty<string>();
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
            var inputName = GetInputName(binding.Mapping);
            var client = binding.Client;

            var state = !client.IsConnected
                ? KeyVisualState.Disconnected
                : client.GetInputMuteState(inputName) == true
                    ? KeyVisualState.Active
                    : KeyVisualState.Inactive;

            var image = KeyImageRenderer.RenderMuteKey(binding.Device.ButtonResolution, state, inputName);
            binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
        }

        private static string GetInputName(CommandMapping mapping)
        {
            return mapping.CommandArguments.GetString("input");
        }
    }
}
