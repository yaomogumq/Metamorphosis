using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Metamorphosis.Objects
{
    public class ChangeSummary
    {
        #region Properties
        public String ModelName { get; set; }
        public String ModelPath { get; set; }
        public String PreviousFile { get; set; }
        public DateTime ComparisonDate { get; set; }

        public int NumberOfChanges { get; set; }

        public Dictionary<string, int> ModelSummary { get; set; } = new Dictionary<string, int>();

        public IList<string> LevelNames { get; set; } = new List<string>();
        public IList<Change> Changes { get; set; } = new List<Change>();

        /// <summary>
        /// Ways the linked models differed between the two snapshots. Not model changes -
        /// reasons to distrust some of the changes above, since a room's area is computed
        /// from whatever bounds it and much of that lives in a link. Read alongside any
        /// change flagged <see cref="Change.PossibleLinkArtifact"/>.
        /// </summary>
        public IList<LinkWarning> LinkWarnings { get; set; } = new List<LinkWarning>();
        #endregion
    }
}
