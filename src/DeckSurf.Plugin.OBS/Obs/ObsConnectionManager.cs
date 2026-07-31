using System;
using System.Collections.Generic;
using System.Linq;

namespace DeckSurf.Plugin.OBS.Obs
{
    /// <summary>
    /// Pools <see cref="ObsWebSocketClient"/> instances so every button mapped to
    /// the same OBS instance shares a single websocket connection. Acquire/Release
    /// are ref-counted; the connection is closed when the last user releases it.
    /// </summary>
    public static class ObsConnectionManager
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, PooledConnection> Connections = new(StringComparer.OrdinalIgnoreCase);

        public static ObsWebSocketClient Acquire(ObsConnectionSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            lock (Sync)
            {
                if (Connections.TryGetValue(settings.PoolKey, out var pooled))
                {
                    pooled.RefCount++;
                    return pooled.Client;
                }

                var client = new ObsWebSocketClient(settings);
                Connections[settings.PoolKey] = new PooledConnection { Client = client, RefCount = 1 };
                client.Start();
                return client;
            }
        }

        public static void Release(ObsWebSocketClient client)
        {
            if (client == null)
            {
                return;
            }

            lock (Sync)
            {
                var entry = Connections.FirstOrDefault(c => c.Value.Client == client);
                if (entry.Value == null)
                {
                    return;
                }

                if (--entry.Value.RefCount <= 0)
                {
                    Connections.Remove(entry.Key);
                    client.Dispose();
                }
            }
        }

        private sealed class PooledConnection
        {
            public ObsWebSocketClient Client { get; set; }

            public int RefCount { get; set; }
        }
    }
}
