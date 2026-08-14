using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Metamorphosis.Objects
{
    /// <summary>
    /// What a linked model looked like at the moment a snapshot was taken.
    ///
    /// A room's area, volume and boundary are computed from whatever bounds it, and that
    /// geometry usually lives in a linked model rather than the host. So if a link is
    /// merely unloaded, reloaded, or swapped for another revision between two snapshots,
    /// every room depending on it appears to change - without anyone having edited
    /// anything. Nothing in the snapshot used to record link state, which made that
    /// indistinguishable after the fact from a real design change.
    /// </summary>
    public class LinkState
    {
        #region Properties
        public long InstanceId { get; set; }

        public long TypeId { get; set; }

        /// <summary>Link type name, normally the linked file's name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Full path, still readable when the link is unloaded.</summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>
        /// A <c>LinkedFileStatus</c> name - Loaded, Unloaded, NotFound, LocallyUnloaded,
        /// InClosedWorkset, Imported, CanBeUpgraded or Invalid. Stored as text so a
        /// snapshot stays readable if Autodesk adds a value later.
        /// </summary>
        public string Status { get; set; } = String.Empty;

        public bool IsNested { get; set; }

        /// <summary>
        /// The linked document's own version GUID, or null when it could not be read.
        ///
        /// Only a LOADED link has a Document to interrogate, so this is null for every
        /// unloaded link - which on a real model is most of them. Absence therefore means
        /// "could not tell", never "unchanged", and the comparison has to treat the two
        /// differently. See <see cref="HasFingerprint"/>.
        /// </summary>
        public string DocumentGuid { get; set; }

        /// <summary>Save count of the linked document, or -1 when unknown.</summary>
        public int NumberOfSaves { get; set; } = -1;
        #endregion

        #region Accessors
        /// <summary>
        /// Whether this record can say anything about the linked file's CONTENT, as
        /// opposed to merely its presence.
        /// </summary>
        [JsonIgnore]
        public bool HasFingerprint
        {
            get { return String.IsNullOrEmpty(DocumentGuid) == false; }
        }

        /// <summary>
        /// Identity for matching a link across two snapshots. The path is used rather than
        /// the element id, because ids are not stable across models and a link is really
        /// identified by the file it points at.
        /// </summary>
        [JsonIgnore]
        public string Key
        {
            get { return (String.IsNullOrEmpty(Path) ? Name : Path).ToUpperInvariant(); }
        }

        /// <summary>
        /// Was this link actually contributing geometry to the host at the time?
        ///
        /// This is what decides whether a difference matters. A link that was unloaded in
        /// both snapshots bounded nothing on either side, so it cannot have moved a room's
        /// area no matter what happened to the file on disk - warning about it would be
        /// crying wolf on every comparison. Only links that were present somewhere can
        /// explain a change.
        /// </summary>
        [JsonIgnore]
        public bool ContributesGeometry
        {
            get
            {
                if (HasFingerprint) return true;   // only a loaded document yields one

                return String.Equals(Status, "Loaded", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(Status, "Imported", StringComparison.OrdinalIgnoreCase);
            }
        }
        #endregion

        #region PublicMethods
        /// <summary>
        /// Did the linked file's content change between the two? Null means unknowable -
        /// at least one side was unloaded and so has no fingerprint.
        /// </summary>
        public bool? ContentChangedFrom(LinkState previous)
        {
            if (previous == null) return null;
            if (!HasFingerprint || !previous.HasFingerprint) return null;

            if (String.Equals(DocumentGuid, previous.DocumentGuid, StringComparison.OrdinalIgnoreCase) == false)
            {
                return true;
            }

            // Same document, but saved again since - still a content change.
            if ((NumberOfSaves >= 0) && (previous.NumberOfSaves >= 0))
            {
                return NumberOfSaves != previous.NumberOfSaves;
            }

            return false;
        }

        public override string ToString()
        {
            return Name + " (" + Status + ")";
        }
        #endregion
    }
}
