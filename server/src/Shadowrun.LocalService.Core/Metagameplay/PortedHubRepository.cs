using System;
using System.Collections.Generic;
using System.IO;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation.Serialization;
using SRO.Core.Compatibility.Utilities;

namespace Shadowrun.LocalService.Core.Metagameplay
{
    internal interface IPortedHubLoader
    {
        IHubData LoadHub(string name);
    }

    internal sealed class PortedHubLoader : IPortedHubLoader
    {
        private readonly ISceneObjectDataFileSerializer<Hub> _hubDataFileSerializer;
        private readonly string _streamingAssetsDir;

        public PortedHubLoader()
            : this(null, new SceneObjectDataFileSerializer<Hub>(JsonFxSerializerProvider.Current, new FileSystem()))
        {
        }

        public PortedHubLoader(string streamingAssetsDir)
            : this(streamingAssetsDir, new SceneObjectDataFileSerializer<Hub>(JsonFxSerializerProvider.Current, new FileSystem()))
        {
        }

        public PortedHubLoader(ISceneObjectDataFileSerializer<Hub> hubDataFileSerializer)
            : this(null, hubDataFileSerializer)
        {
        }

        public PortedHubLoader(string streamingAssetsDir, ISceneObjectDataFileSerializer<Hub> hubDataFileSerializer)
        {
            _streamingAssetsDir = streamingAssetsDir;
            _hubDataFileSerializer = hubDataFileSerializer;
        }

        public IHubData LoadHub(string name)
        {
            if (string.IsNullOrEmpty(name) || _hubDataFileSerializer == null)
            {
                return null;
            }

            var result = _hubDataFileSerializer.LoadFromFile(ResolveHubMapDataPath(name));
            if (result == null)
            {
                return null;
            }

            return HubData.CreateFromSerializableHub(name, result);
        }

        private string ResolveHubMapDataPath(string name)
        {
            var relativePath = string.Format("hubs\\{0}\\mapdata.json", name);

            foreach (var candidate in EnumerateCandidateHubMapPaths(name, relativePath))
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return relativePath;
        }

        private IEnumerable<string> EnumerateCandidateHubMapPaths(string name, string relativePath)
        {
            if (!string.IsNullOrEmpty(_streamingAssetsDir))
            {
                yield return Path.Combine(Path.Combine(Path.Combine(_streamingAssetsDir, "hubs"), name), "mapdata.json");
            }

            string baseDir = null;
            try
            {
                baseDir = AppDomain.CurrentDomain.BaseDirectory;
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(baseDir))
            {
                yield return Path.Combine(Path.Combine(Path.Combine(Path.Combine(Path.Combine(baseDir, "Resources"), "StreamingAssets"), "hubs"), name), "mapdata.json");
                yield return Path.Combine(Path.Combine(Path.Combine(Path.Combine(baseDir, "StreamingAssets"), "hubs"), name), "mapdata.json");
            }

            string currentDir = null;
            try
            {
                currentDir = Directory.GetCurrentDirectory();
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(currentDir))
            {
                yield return Path.Combine(Path.Combine(Path.Combine(Path.Combine(currentDir, "StreamingAssets"), "hubs"), name), "mapdata.json");
            }

            yield return relativePath;
        }
    }

    internal sealed class PortedHubRepository
    {
        private readonly IPortedHubLoader _hubLoader;
        private readonly Dictionary<string, IHubData> _loadedHubs;

        public PortedHubRepository(IPortedHubLoader hubLoader)
        {
            _hubLoader = hubLoader;
            _loadedHubs = new Dictionary<string, IHubData>();
        }

        public IHubData GetHub(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return new HubData(string.Empty);
            }

            IHubData loaded;
            if (_loadedHubs.TryGetValue(name, out loaded) && loaded != null)
            {
                return loaded;
            }

            loaded = _hubLoader != null ? _hubLoader.LoadHub(name) : null;
            if (loaded != null)
            {
                _loadedHubs[name] = loaded;
                return loaded;
            }

            return new HubData(name);
        }
    }
}
