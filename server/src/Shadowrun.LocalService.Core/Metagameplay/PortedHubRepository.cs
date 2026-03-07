using System.Collections.Generic;
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

        public PortedHubLoader()
            : this(new SceneObjectDataFileSerializer<Hub>(JsonFxSerializerProvider.Current, new FileSystem()))
        {
        }

        public PortedHubLoader(ISceneObjectDataFileSerializer<Hub> hubDataFileSerializer)
        {
            _hubDataFileSerializer = hubDataFileSerializer;
        }

        public IHubData LoadHub(string name)
        {
            if (string.IsNullOrEmpty(name) || _hubDataFileSerializer == null)
            {
                return null;
            }

            var result = _hubDataFileSerializer.LoadFromFile(string.Format("hubs\\{0}\\mapdata.json", name));
            if (result == null)
            {
                return null;
            }

            return HubData.CreateFromSerializableHub(name, result);
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
