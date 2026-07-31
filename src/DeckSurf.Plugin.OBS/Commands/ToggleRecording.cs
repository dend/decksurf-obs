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
        // A full dim-bright-dim cycle takes PulsePeriodMs; frames tick an order
        // of magnitude faster so the sine reads as a smooth breathing motion.
        private const int PulsePeriodMs = 2400;
        private const int PulseFrameMs = 120;

        private readonly object _pulseSync = new();
        private System.Timers.Timer _pulseTimer;

        public override string Name => "Toggle recording";

        public override string Description => "Starts or stops recording in OBS Studio. The key shows a REC circle that is greyed out when idle and pulses red while a recording is in progress.";

        protected override bool SupportsPreview => false;

        public override void ExecuteOnActivation(CommandMapping mappedCommand, IConnectedDevice mappedDevice)
        {
            base.ExecuteOnActivation(mappedCommand, mappedDevice);

            if (mappedCommand == null || mappedDevice == null)
            {
                return;
            }

            lock (_pulseSync)
            {
                if (_pulseTimer == null)
                {
                    _pulseTimer = new System.Timers.Timer(PulseFrameMs);
                    _pulseTimer.Elapsed += (s, e) => RenderPulseFrames();
                    _pulseTimer.Start();
                }
            }
        }

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
            var client = binding.Client;

            var state = !client.IsConnected
                ? KeyVisualState.Disconnected
                : client.IsRecording
                    ? KeyVisualState.Active
                    : KeyVisualState.Inactive;

            // The phase is derived from the shared clock so every recording key
            // on the deck breathes in sync.
            var phase = Environment.TickCount64 % PulsePeriodMs / (double)PulsePeriodMs;
            var pulse = (float)((1 - Math.Cos(2 * Math.PI * phase)) / 2);

            var image = KeyImageRenderer.RenderRecordKey(binding.Device.ButtonResolution, state, pulse);
            binding.Device.SetKey(binding.Mapping.ButtonIndex, image);
        }

        protected override void OnDisposing()
        {
            lock (_pulseSync)
            {
                _pulseTimer?.Stop();
                _pulseTimer?.Dispose();
                _pulseTimer = null;
            }
        }

        private void RenderPulseFrames()
        {
            // Idle and disconnected keys are static and re-render through state
            // events; only actively recording keys need animation frames.
            foreach (var binding in SnapshotBindings())
            {
                if (binding.Client.IsConnected && binding.Client.IsRecording)
                {
                    TryRender(binding);
                }
            }
        }
    }
}
