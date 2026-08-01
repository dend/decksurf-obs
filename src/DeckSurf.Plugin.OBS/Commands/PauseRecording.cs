using DeckSurf.Plugin.OBS.Rendering;
using DeckSurf.SDK.Interfaces;
using DeckSurf.SDK.Models;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DeckSurf.Plugin.OBS.Commands
{
    [CommandDynamicDisplay]
    [CommandParameter("host", CommandParameterType.String, DisplayName = "OBS host", Description = "Host name or IP address of the obs-websocket server.", DefaultValue = "127.0.0.1", Order = 0)]
    [CommandParameter("port", CommandParameterType.Integer, DisplayName = "OBS port", Description = "Port of the obs-websocket server.", DefaultValue = "4455", MinValue = 1, MaxValue = 65535, Order = 1)]
    [CommandParameter("password", CommandParameterType.Secret, DisplayName = "OBS password", Description = "obs-websocket password. Leave empty when authentication is disabled.", Order = 2)]
    public class PauseRecording : ObsCommandBase
    {
        public override string Name => "Pause recording";

        public override string Description => "Pauses or resumes the current OBS recording. The key shows pause bars that turn amber while the recording is paused, and stays dimmed when no recording is running.";

        protected override bool SupportsPreview => false;

        public override void ExecuteOnAction(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1)
        {
            _ = ExecuteOnActionAsync(mappedCommand, mappedDevice, activatingButton);
        }

        public override async Task ExecuteOnActionAsync(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1, CancellationToken cancellationToken = default)
        {
            var binding = GetOrCreateBinding(mappedCommand, mappedDevice);

            // Pausing only applies to a running recording; a press while idle is
            // ignored rather than surfacing an OBS error.
            if (binding == null || !binding.Client.IsRecording)
            {
                return;
            }

            try
            {
                await binding.Client.ToggleRecordPauseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not toggle the OBS recording pause: {ex.Message}");
            }
        }

        protected override void RenderBinding(ButtonBinding binding)
        {
            var client = binding.Client;

            var state = !client.IsConnected
                ? KeyVisualState.Disconnected
                : client.IsRecording
                    ? KeyVisualState.Active
                    : KeyVisualState.Inactive;

            var image = KeyImageRenderer.RenderPauseKey(
                binding.Device.ButtonResolution,
                state,
                client.IsConnected && client.IsRecordingPaused);
            binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
        }
    }
}
