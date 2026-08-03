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
    public class ToggleVirtualCamera : ObsCommandBase
    {
        public override string Name => "Toggle virtual camera";

        public override string Description => "Starts or stops the OBS virtual camera. The key shows a CAM circle that lights up blue while the virtual camera is running, tracking starts and stops made in OBS itself as well.";

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
                await binding.Client.ToggleVirtualCamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Could not toggle the OBS virtual camera: {ex.Message}");
            }
        }

        protected override void RenderBinding(ButtonBinding binding)
        {
            var client = binding.Client;

            var state = !client.IsConnected
                ? KeyVisualState.Disconnected
                : client.IsVirtualCamActive
                    ? KeyVisualState.Active
                    : KeyVisualState.Inactive;

            var image = KeyImageRenderer.RenderVirtualCamKey(binding.Device.ButtonResolution, state);
            binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
        }
    }
}
