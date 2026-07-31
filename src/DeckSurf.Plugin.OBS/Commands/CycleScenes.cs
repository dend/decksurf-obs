using DeckSurf.Plugin.OBS.Rendering;
using DeckSurf.SDK.Interfaces;
using DeckSurf.SDK.Models;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DeckSurf.Plugin.OBS.Commands
{
    [CommandDynamicDisplay]
    [CommandParameter("host", CommandParameterType.String, DisplayName = "OBS host", Description = "Host name or IP address of the obs-websocket server.", DefaultValue = "127.0.0.1", Order = 0)]
    [CommandParameter("port", CommandParameterType.Integer, DisplayName = "OBS port", Description = "Port of the obs-websocket server.", DefaultValue = "4455", MinValue = 1, MaxValue = 65535, Order = 1)]
    [CommandParameter("password", CommandParameterType.Secret, DisplayName = "OBS password", Description = "obs-websocket password. Leave empty when authentication is disabled.", Order = 2)]
    [CommandParameter("preview", CommandParameterType.Boolean, DisplayName = "Scene preview on key", Description = "Render a periodically refreshed snapshot of the live scene as the button image.", DefaultValue = "true", Order = 3)]
    [CommandParameter("preview_interval", CommandParameterType.Integer, DisplayName = "Preview refresh (seconds)", Description = "How often the scene snapshot is refreshed.", DefaultValue = "3", MinValue = 1, MaxValue = 60, Order = 4)]
    public class CycleScenes : ObsCommandBase
    {
        public override string Name => "Cycle scenes";

        public override string Description => "Cycles through OBS scenes. Press to advance, or rotate a Stream Deck+ knob in either direction. The key shows the scene that is currently live.";

        public override void ExecuteOnAction(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int activatingButton = -1)
        {
            Advance(mappedCommand, mappedDevice, 1);
        }

        public override void ExecuteOnEvent(CommandMapping mappedCommand, IConnectedDevice mappedDevice, ButtonPressEventArgs eventArgs)
        {
            ArgumentNullException.ThrowIfNull(eventArgs);

            if (eventArgs.IsKnobRotating == true)
            {
                Advance(mappedCommand, mappedDevice, eventArgs.KnobRotationDirection == KnobRotationDirection.Right ? 1 : -1);
                return;
            }

            if (eventArgs.EventKind == ButtonEventKind.Down)
            {
                ExecuteOnAction(mappedCommand, mappedDevice, eventArgs.Id);
            }
        }

        protected override void RenderBinding(ButtonBinding binding)
        {
            var state = binding.Client.IsConnected ? KeyVisualState.Active : KeyVisualState.Disconnected;
            var label = binding.Client.CurrentProgramScene ?? "OBS";

            // This key always shows what's on program, so the preview is the live
            // scene and carries the LIVE treatment.
            if (TryBeginPreviewRender(binding, binding.Client.CurrentProgramScene, isLive: true, () => RenderTextKey(binding, label, state)))
            {
                return;
            }

            RenderTextKey(binding, label, state);
        }

        private static void RenderTextKey(ButtonBinding binding, string label, KeyVisualState state)
        {
            var image = KeyImageRenderer.Render(binding.Device.ButtonResolution, label, state);
            binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
        }

        private void Advance(CommandMapping mappedCommand, IConnectedDevice mappedDevice, int delta)
        {
            var binding = GetOrCreateBinding(mappedCommand, mappedDevice);
            if (binding == null)
            {
                return;
            }

            var client = binding.Client;
            var scenes = client.Scenes;
            if (!client.IsConnected || scenes.Count == 0)
            {
                return;
            }

            // An unknown current scene resolves to index -1, so a press lands on
            // the first scene in the list.
            var currentIndex = scenes.ToList().IndexOf(client.CurrentProgramScene);
            var nextScene = scenes[((currentIndex + delta) % scenes.Count + scenes.Count) % scenes.Count];

            _ = Task.Run(async () =>
            {
                try
                {
                    await client.SetCurrentProgramSceneAsync(nextScene).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"Could not switch OBS to scene '{nextScene}': {ex.Message}");
                }
            });
        }
    }
}
