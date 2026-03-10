using Shadowrun.LocalService.Core.Metagameplay;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private MetagameplayStaticDataIndex GetMetagameplayStaticDataIndex()
        {
            return MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);
        }

        private bool IsRepeatableMission(string missionName)
        {
            return !string.IsNullOrEmpty(missionName) && GetMetagameplayStaticDataIndex().IsMissionRepeatable(missionName);
        }

        private bool TryResolveBodytypeId(ulong metatypeId, ulong genderId, out ulong bodytypeId)
        {
            return GetMetagameplayStaticDataIndex().TryGetBodytypeId(metatypeId, genderId, out bodytypeId);
        }
    }
}
