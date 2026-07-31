using DeckSurf.Plugin.OBS.Commands;
using DeckSurf.SDK.Interfaces;
using DeckSurf.SDK.Models;
using System;
using System.Collections.Generic;

namespace DeckSurf.Plugin.OBS
{
    public class Plugin : IDeckSurfPlugin
    {
        private readonly PluginMetadata _metadata = new()
        {
            Author = "Den Delimarsky",
            Id = "DeckSurf.Plugin.OBS",
            Name = "DeckSurf OBS Connector",
            // Reported from the assembly so the version shown in DeckSurf always
            // matches what the build (and the release tag) stamped on the DLL.
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Website = "https://github.com/dend/decksurf-obs"
        };

        public PluginMetadata Metadata => _metadata;

        public List<Type> GetSupportedCommands()
        {
            return new List<Type>()
            {
                typeof(SwitchScene),
                typeof(CycleScenes)
            };
        }
    }
}
