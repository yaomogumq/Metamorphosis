using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Metamorphosis.Objects
{
    /// <summary>
    /// Something about the linked models differs between the two snapshots, in a way that
    /// can make unrelated elements look changed.
    ///
    /// These are reported alongside the element changes rather than folded into them,
    /// because they are not changes to the model - they are a reason to distrust some of
    /// the changes that are. Silently dropping the affected elements would hide real
    /// edits; silently reporting them as normal would hand a take-off phantom quantities.
    /// </summary>
    public class LinkWarning
    {
        public enum LinkIssueEnum
        {
            /// <summary>Present in both, but loaded in one and not the other.</summary>
            LoadStateChanged,

            /// <summary>The linked file itself was revised - downstream changes are real.</summary>
            ContentChanged,

            /// <summary>Link exists only in the newer snapshot.</summary>
            LinkAdded,

            /// <summary>Link existed in the older snapshot and is gone.</summary>
            LinkRemoved,

            /// <summary>Present in both and loaded in both, but content could not be compared.</summary>
            ContentUnknown
        }

        #region Properties
        [JsonConverter(typeof(StringEnumConverter))]
        public LinkIssueEnum Issue { get; set; }

        public string LinkName { get; set; } = String.Empty;

        public string Path { get; set; } = String.Empty;

        public string PreviousStatus { get; set; } = String.Empty;

        public string CurrentStatus { get; set; } = String.Empty;

        /// <summary>
        /// Whether changes to elements whose geometry depends on this link should be
        /// treated as suspect. True when the link's presence changed but its content
        /// either did not change or could not be checked - a room's area moving under
        /// those conditions says more about the link than about the design.
        /// </summary>
        public bool CausesSuspectChanges { get; set; }

        public string Description { get; set; } = String.Empty;
        #endregion

        #region PublicMethods
        public override string ToString()
        {
            return LinkName + ": " + Issue;
        }
        #endregion
    }
}
