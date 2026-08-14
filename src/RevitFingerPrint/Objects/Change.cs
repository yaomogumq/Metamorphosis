using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace Metamorphosis.Objects
{
    public class Change
    {
        public enum ChangeTypeEnum { ParameterChange, Move, Rotate, GeometryChange, NewElement, DeletedElement }

        private readonly List<ChangeTypeEnum> _changeTypes = new List<ChangeTypeEnum>();

        #region Properties
        public long ElementId { get; set; }

        public string UniqueId { get; set; }

        public string Category { get; set; }

        /// <summary>
        /// The primary change type, kept single-valued so that existing consumers keep working:
        /// the colour choices in Settings.xml are keyed by this name, and so are Dynamo graphs
        /// and any previously written JSON. When an element changed in more than one way this
        /// is the first entry of <see cref="ChangeTypes"/> - read that for the whole picture.
        /// </summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public ChangeTypeEnum ChangeType
        {
            get { return (_changeTypes.Count > 0) ? _changeTypes[0] : ChangeTypeEnum.ParameterChange; }
            set
            {
                _changeTypes.Remove(value);
                _changeTypes.Insert(0, value);
            }
        }

        /// <summary>
        /// Every way in which this element changed. An element that both moved and had a
        /// parameter edited in the same revision reports both, because a quantity take-off
        /// needs the geometric change and the data change - not whichever happened to be
        /// tested first.
        /// </summary>
        /// <remarks>
        /// Replace rather than the default Auto: <see cref="ChangeType"/> is deserialized first
        /// and has already seeded the list, so letting Json.NET append into it would duplicate
        /// the primary type. Older result files that only carry ChangeType still read correctly.
        /// </remarks>
        [JsonProperty(ItemConverterType = typeof(StringEnumConverter), ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<ChangeTypeEnum> ChangeTypes
        {
            get { return _changeTypes; }
            set
            {
                _changeTypes.Clear();
                if (value == null) return;
                foreach (var type in value) AddChangeType(type);
            }
        }

        /// <summary>
        /// All change types as one readable string, e.g. "ParameterChange + Move".
        /// </summary>
        [JsonIgnore]
        public string ChangeTypeDescription
        {
            get { return String.Join(" + ", _changeTypes); }
        }

        public String Level { get; set; } = String.Empty;
        public string BoundingBoxDescription { get; set; }

        public string ChangeDescription { get; set; } = String.Empty;

        public Boolean IsType { get; set; } = false;

        public string MoveDescription { get; set; }

        public string RotationDescription { get; set; }
        #endregion

        #region PublicMethods
        public void AddChangeType(ChangeTypeEnum type)
        {
            if (_changeTypes.Contains(type) == false) _changeTypes.Add(type);
        }

        public bool HasChangeType(ChangeTypeEnum type)
        {
            return _changeTypes.Contains(type);
        }

        /// <summary>
        /// Fold another change on the same element into this one, so a compound edit stays a
        /// single record carrying every change type. Descriptions are concatenated; the
        /// per-axis descriptions (move/rotation/bounding box) are only taken from the other
        /// change where this one has nothing to say, since each axis is written by exactly
        /// one of the two comparisons.
        /// </summary>
        public void MergeFrom(Change other)
        {
            if (other == null) return;

            foreach (var type in other.ChangeTypes) AddChangeType(type);

            ChangeDescription = joinDescriptions(ChangeDescription, other.ChangeDescription);

            if (String.IsNullOrEmpty(MoveDescription)) MoveDescription = other.MoveDescription;
            if (String.IsNullOrEmpty(RotationDescription)) RotationDescription = other.RotationDescription;
            if (String.IsNullOrEmpty(BoundingBoxDescription)) BoundingBoxDescription = other.BoundingBoxDescription;
            if (String.IsNullOrEmpty(Level)) Level = other.Level;
            if (String.IsNullOrEmpty(UniqueId)) UniqueId = other.UniqueId;
        }

        public override string ToString()
        {
            return Category + ": " + ChangeTypeDescription;
        }
        #endregion

        #region PrivateMethods
        private static string joinDescriptions(string first, string second)
        {
            if (String.IsNullOrEmpty(first)) return second;
            if (String.IsNullOrEmpty(second)) return first;

            return first + "; " + second;
        }
        #endregion
    }
}
