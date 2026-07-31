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
    public class ToggleRecording : ObsCommandBase
    {
        public override string Name => "Toggle recording";

        public override string Description => "Starts or stops recording in OBS Studio. The key turns red with a REC badge while a recording is in progress.";

        protected override bool SupportsPreview => false;

        public override void ExecuteOnAction(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1)
        {
            _ = ExecuteOnActionAsync(mappedCommand, mappedDevice, activatingButton);
        }

        public override async Task ExecuteOnActionAsync(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1, CancellationToken cancellationToken = default)
        {
            var binding = GetOrCreateBinding(mappedCommand, mappedDevice);
            if (binding == null)
            {
                return;
            }

            try
            {
                await binding.Client.ToggleRecordAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not toggle OBS recording: {ex.Message}");
            }
        }

        protected override void RenderBinding(ButtonBinding binding)
        {
            var state = !binding.Client.IsConnected
                ? KeyVisualState.Disconnected
                : binding.Client.IsRecording
                    ? KeyVisualState.Active
                    : KeyVisualState.Inactive;

            var image = KeyImageRenderer.Render(
                binding.Device.ButtonResolution,
                state == KeyVisualState.Active ? "Recording" : "Record",
                state,
                badgeText: "REC");

            binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
        }
    }
}
