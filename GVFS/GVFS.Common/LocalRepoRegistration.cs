using System.Text.Json;
using System.Text.Json.Serialization;

namespace GVFS.Common
{
    /// <summary>
    /// On-disk representation of one repo-registry entry. Wire-compatible
    /// with GVFS.Service.RepoRegistration so the CLI fallback path and the
    /// legacy service can both read/write the same file. New fields added
    /// here MUST also be added to RepoRegistration (and vice versa).
    /// </summary>
    public class LocalRepoRegistration
    {
        public string EnlistmentRoot { get; set; }

        public string OwnerSID { get; set; }

        public bool IsActive { get; set; }

        public static LocalRepoRegistration FromJson(string json)
        {
            return JsonSerializer.Deserialize(json, LocalRepoRegistrationContext.Default.LocalRepoRegistration);
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, LocalRepoRegistrationContext.Default.LocalRepoRegistration);
        }
    }

    // Local source-generated context: a separate context here (rather than
    // adding to GVFSJsonContext) keeps the GVFS.Common <- GVFS.Service
    // assembly dependency direction clean. The service has its own
    // ServiceJsonContext for the service-side RepoRegistration type.
    [JsonSerializable(typeof(LocalRepoRegistration))]
    internal partial class LocalRepoRegistrationContext : JsonSerializerContext
    {
    }
}
