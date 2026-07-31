using DeckSurf.SDK.Models;

namespace DeckSurf.Plugin.OBS.Obs
{
    /// <summary>
    /// Connection parameters for the obs-websocket server, read from a command's
    /// arguments. Every OBS command accepts the same host/port/password keys so
    /// buttons pointing at the same OBS instance share one connection.
    /// </summary>
    public sealed record ObsConnectionSettings(string Host, int Port, string Password)
    {
        public const string DefaultHost = "127.0.0.1";

        public const int DefaultPort = 4455;

        /// <summary>
        /// Key used to pool connections. The password is part of the key: the
        /// profile editor queries scenes before the password has been typed, and a
        /// client created with the wrong password retries auth forever. Keying on
        /// host:port alone would hand that broken client to every later acquire.
        /// The key stays in memory only and is never serialized.
        /// </summary>
        public string PoolKey => $"{Host}:{Port}:{Password}";

        public static ObsConnectionSettings FromArguments(CommandArguments arguments)
        {
            var host = arguments.GetString("host", DefaultHost);
            var port = arguments.GetInt32("port", DefaultPort);
            if (port is <= 0 or > 65535)
            {
                port = DefaultPort;
            }

            return new ObsConnectionSettings(host, port, arguments.GetString("password"));
        }
    }
}
