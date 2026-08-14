using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Metamorphosis.Objects;
using System.Data.SQLite;
using System.Globalization;

namespace Metamorphosis
{
    public class ComparisonMaker
    {
        #region Declarations
        private enum VersionGuidCompareEnum { Unknown, Matching, NotMatching};
        private Document _doc;
        private string _filename;
        private string _dbFilename;
        private Dictionary<int, string> _parameterDict = new Dictionary<int, string>();
        private Dictionary<string, string> _headerDict = new Dictionary<string, string>();
        private Dictionary<int, string> _valueDict = new Dictionary<int, string>();
        private Dictionary<string, int> _categoryCount = new Dictionary<string, int>();
        private HashSet<string> _requestedCategoryNames = new HashSet<string>();
        private IList<Level> _allLevels;
        private bool _isMetric = false;
        private bool _useGuidCompare = false;
        private IList<LinkState> _previousLinks = new List<LinkState>();
        private HashSet<string> _suspectCategories = new HashSet<string>();



        // TODO: separate categories by dictionary of category, elementid, parameter
        private Dictionary<long, RevitElement> _idValues = new Dictionary<long, RevitElement>();
        private Dictionary<long, RevitElement> _currentElems = new Dictionary<long, RevitElement>();


        #endregion

        #region Accessors
        public Boolean AllCategories { get; set; } = true;
        public IList<Category> RequestedCategories { get; set; }
        public double MoveTolerance { get; set; }
        public float RotateTolerance { get; set; }
        public bool UseEpisodeGuid { get; set; }

        /// <summary>
        /// Differences in the linked models between the two snapshots. Populated by
        /// <see cref="Compare"/>. These are not model changes - they are reasons some of
        /// the reported changes may not be real, so read them before trusting a room's
        /// area or volume delta.
        /// </summary>
        public IList<LinkWarning> LinkWarnings { get; private set; } = new List<LinkWarning>();
        #endregion

        #region Constructor
        public ComparisonMaker(Document doc, string previousFile)
        {
            MoveTolerance = 0.0006;  // default;
            RotateTolerance = 0.0349f; // default

            _doc = doc;
            if (doc.DisplayUnitSystem == DisplayUnit.METRIC) _isMetric = true;

            _filename = previousFile;

            RequestedCategories = new List<Category>();

            _dbFilename = _filename;
            // see: http://system.data.sqlite.org/index.html/info/bbdda6eae2
            if (_filename.StartsWith(@"\\")) _dbFilename = @"\\" + _dbFilename;
            doc.Application.WriteJournalComment("Previous File DB: " + _dbFilename, false);

            _useGuidCompare = Metamorphosis.Utilities.Settings.GetVersionGuidOption();
            doc.Application.WriteJournalComment("GuidCompare Option: " + _useGuidCompare, false);
            UseEpisodeGuid = false;
        }
        #endregion

        #region PublicMethods
        public IList<Change> Compare()
        {
            // we want to load up a previous model, 
            readPrevious();

            if (UseEpisodeGuid && canUseEpisodeGuid() == false)
            {
                _doc.Application.WriteJournalComment("Falling back to traditional/EpisodeGuid not available!", false);
                UseEpisodeGuid = false;
            }
            // Work out how the linked models differ before looking at elements, so the
            // element changes can be labelled with what we learn here.
            buildLinkWarnings();

            IList<Change> changes;
            if (UseEpisodeGuid)
            {
                changes = compareWithEpisodeGuid();
            }
            else
            {
                // read the existing data into memory
                readModel();

                // make our comparisons
                changes = compareData();
            }

            flagSuspectChanges(changes);

            return changes;
        }

        public void Serialize(string filename, IList<Change> changes)
        {
            System.IO.File.WriteAllText(filename, Serialize(changes));
        }

        public static ChangeSummary DeSerialize(string filename)
        {
            string content = System.IO.File.ReadAllText(filename);
            ChangeSummary cs = Newtonsoft.Json.JsonConvert.DeserializeObject<ChangeSummary>(content);

            return cs;
        }

        public string Serialize(IList<Change> changes)
        {
            // build the change summary:

            ChangeSummary summary = new ChangeSummary();
            summary.ModelName = _doc.Title;
            summary.ModelPath = _doc.PathName;
            summary.NumberOfChanges = changes.Count;
            summary.PreviousFile = _filename;
            summary.ComparisonDate = DateTime.Now;
            summary.Changes = changes;
            summary.ModelSummary = _categoryCount;
            summary.LevelNames = _allLevels.OrderBy(a => a.Elevation).Select(a => a.Name).ToList();
            summary.LinkWarnings = LinkWarnings;


            string result = Newtonsoft.Json.JsonConvert.SerializeObject(summary);
            //var serialize = new System.Web.Script.Serialization.JavaScriptSerializer();
            //string result = serialize.Serialize(summary);

            return result;

        }
        #endregion

        #region PrivateMethods
        private IList<Change> compareWithEpisodeGuid()
        {
#if REVIT2015 || REVIT2016 || REVIT2017 || REVIT2018 || REVIT2019 || REVIT2020 || REVIT2021 || REVIT2022
            throw new ApplicationException("Unable to use EpisodeGuid comparison before 2023!");
#else
            List<Change> changes = new List<Change>();
            Guid episodeGuid = Guid.Parse(_headerDict["DocumentGuid"]);

            if (!AllCategories)
            {
                foreach (var c in RequestedCategories) _requestedCategoryNames.Add(c.Name);

            }
            // get the levels:
            if (_allLevels == null)
            {
                FilteredElementCollector coll = new FilteredElementCollector(_doc);
                coll.OfClass(typeof(Level));

                _allLevels = coll.Cast<Level>().ToList();
            }


            DocumentDifference diff = _doc.GetChangedElements(episodeGuid);
            var createdIds = diff.GetCreatedElementIds();
            var modifiedIds = diff.GetModifiedElementIds();

            // build the created:
            foreach (var id in createdIds)
            {
                Element e = _doc.GetElement(id);
                string catName = (e.Category != null) ? e.Category.Name : "(none)";

                if (AllCategories || _requestedCategoryNames.Contains(catName))
                {

                    changes.Add(buildNew(makeRevitElemFromElement(e, false)));
                }
            }

            // build the modified:
            foreach( var id in modifiedIds )
            {
                Element e = _doc.GetElement(id);
                string catName = (e.Category != null) ? e.Category.Name : "(none)";

                if (AllCategories || _requestedCategoryNames.Contains(catName))
                {
                    RevitElement current = makeRevitElemFromElement(e, true);
                    if (_idValues.ContainsKey(current.ElementId))
                    {
                        Change c = compareElements(current, _idValues[current.ElementId]);
                        if (c != null)
                        {
                            changes.Add(c);
                        }
                        else
                        {
                            _doc.Application.WriteJournalComment($"Weird: EpisodeGUID Says that element {id}:{catName}:{e.Name} has changed, but we couldn't find a change? ignoring.", false);
                        }
                    }
                    else
                    {
                        _doc.Application.WriteJournalComment($"WEIRD: Element id {current.ElementId}: {current.Category}: {e.Name} does not exist in the db data?", false);
;                    }
                  
                }
            }

            if (diff.AreDeletedElementIdsAvailable)
            {
                var deletedIds = diff.GetDeletedElementIds();
                foreach( var id in deletedIds )
                {
                    if (_idValues.ContainsKey(id.AsLong()))
                    {
                        RevitElement previous = _idValues[id.AsLong()];
                        if (!AllCategories && (_requestedCategoryNames.Contains(previous.Category) == false)) continue; // do not include
                        changes.Add(buildDeleted(previous));
                    }
                    else
                    {
                        _doc.Application.WriteJournalComment($"Weird! Element Id {id} is deleted, but we don't have a previous record of it???", false);
                    }
                }

            }
            else
            {
                _doc.Application.WriteJournalComment("NOTE: Because this is not a workshared file, Deleted Elements are not recorded. Please do not use the Document Episode GUID approach if you need details on the Deleted elements!", false);
            }

            return changes;
#endif
        }
        private IList<Change> compareData()
        {
            List<Change> changes = new List<Change>();

            foreach (var currentPair in _currentElems)
            {
                var current = currentPair.Value;
                // find it from the previous
                if (_idValues.ContainsKey(currentPair.Key))
                {
                    // it exists, so let's compare
                    var previous = _idValues[currentPair.Key];

                    VersionGuidCompareEnum compare = VersionGuidCompareEnum.Unknown;
#if REVIT2015 || REVIT2016 || REVIT2017 || REVIT2018 || REVIT2019 || REVIT2020
                    // can't do anything with VersionGUID
#else
                    if ((current != null) && (previous != null) && (current.VersionGuid != null) && (previous.VersionGuid != null))
                    {
                        if (current.VersionGuid != previous.VersionGuid)
                        {
                            compare = VersionGuidCompareEnum.NotMatching;
                        }
                        else
                        {
                            compare = VersionGuidCompareEnum.Matching;
                        }
                    }
#endif
                    if (_useGuidCompare && (compare == VersionGuidCompareEnum.Matching)) continue; // skip stuff where it is matching!

                    var change = compareElements(current, previous);
                    if (change != null)
                    {
                        //temporary: does this agree with the compare?
                        if (compare == VersionGuidCompareEnum.Matching)
                        {
#if DEBUG
                            System.Diagnostics.Debug.Assert(false, "This should have been matching. why is there a change?");
#endif
                            _doc.Application.WriteJournalComment("Note: Odd element that should match but doesn't: " + current.Category + ": " + change.ChangeDescription, false);
                        }
                        changes.Add(change);
                    }
                }
                else
                {
                    // it has been removed.
                    changes.Add(buildNew(current));
                }
            }

            // now look for deleted items
            foreach (var previousPair in _idValues)
            {
                if (_currentElems.ContainsKey(previousPair.Key) == false)
                {
                    if (!AllCategories && (_requestedCategoryNames.Contains(previousPair.Value.Category) == false)) continue; // do not include
                    changes.Add(buildDeleted(previousPair.Value));
                }
            }

            return changes;
        }

        private Change buildDeleted(RevitElement element)
        {
            Change c = new Change() { ElementId = element.ElementId, Category = element.Category, ChangeType = Change.ChangeTypeEnum.DeletedElement,
                Level = (element.Level != null) ? element.Level : "", IsType = element.IsType };
            c.BoundingBoxDescription = Utilities.RevitUtils.SerializeBoundingBox(element.BoundingBox);
            return c;
        }
        private Change compareElements(RevitElement current, RevitElement previous)
        {
            // Both comparisons always run. An element can be moved and re-parametered in the
            // same revision, and a quantity take-off needs to see both - reporting only the
            // parameter change because it was tested first hides the geometric one.
            // at present, we can only compare string values
            Change parameterChange = compareParameters(current, previous);
            Change geometryChange = compareGeometry(current, previous);

            if (parameterChange == null) return geometryChange;
            if (geometryChange == null) return parameterChange;

            parameterChange.MergeFrom(geometryChange);

            return parameterChange;
        }

        private Change buildNew(RevitElement current)
        {
#if LONGELEMENTIDS
            Element e = _doc.GetElement(new ElementId(current.ElementId));
#else
            Element e = _doc.GetElement(new ElementId((int)current.ElementId));
#endif

            Change c = new Change() { ElementId = current.ElementId, UniqueId = e.UniqueId, Category = current.Category, ChangeType = Change.ChangeTypeEnum.NewElement, Level = (current.Level != null) ? current.Level : "", IsType = current.IsType };
            c.BoundingBoxDescription = Utilities.RevitUtils.SerializeBoundingBox(current.BoundingBox);

            return c;
        }

        private Change compareParameters(RevitElement current, RevitElement previous)
        {
            StringBuilder description = null;
            int numParmsChanged = 0;
            foreach (var pair in current.Parameters)
            {
                // we don't want to look at "EditedBy"... we think, because it will be the only thing
                //reported.


                if (previous.Parameters.ContainsKey(pair.Key))
                {
                    // test if they match
                    if (current.Parameters[pair.Key] != previous.Parameters[pair.Key])
                    {
                        if (description == null) description = new StringBuilder();
                        numParmsChanged++;
                        if (numParmsChanged > 1) description.Append(", ");
                        description.Append(pair.Key + " From: " + previous.Parameters[pair.Key] + " to " + current.Parameters[pair.Key]);

                    }

                }
            }
            if (numParmsChanged > 0)
            {
#if LONGELEMENTIDS
                Element e = _doc.GetElement(new ElementId(current.ElementId));
#else
                Element e = _doc.GetElement(new ElementId((int)current.ElementId));
#endif
                Change c = new Change()
                {
                    ElementId = current.ElementId,
                    UniqueId = e.UniqueId,
                    Category = (e.Category != null) ? e.Category.Name : "(none)",
                    ChangeType = Change.ChangeTypeEnum.ParameterChange,
                    ChangeDescription = description.ToString(),
                    Level = (current.Level != null) ? current.Level : "",
                    IsType = current.IsType
                };
                c.BoundingBoxDescription = Utilities.RevitUtils.SerializeBoundingBox(current.BoundingBox);

                return c;
            }

            return null;
        }

        private string getHumanComparison(double distInFeet)
        {
            if (_isMetric)
            {
                // less than 1 foot
                if (distInFeet < 1.0)
                {
                    return (distInFeet * 304.8).ToString("F1") + "mm";
                }
                if (distInFeet < 3.28)  // less than a m, do cm...
                {
                    return (distInFeet * 30.48).ToString("F2") + "cm";
                }
                return (distInFeet * 0.3048) + "m";
            }
            else
            {
                if (distInFeet < 3.0)
                {
                    return (distInFeet * 12.0).ToString("F3") + "in.";
                }
                else
                {
                    return distInFeet + "ft.";
                }
            }
        }

        private Change compareGeometry(RevitElement current, RevitElement previous)
        {
            // try to do a comparison based on bounding boxes and locations...
            // this is CERTAINLY imperfect

           //old, fixed: double tolerance = 0.0006;  // decimal feet - 1/128"?

            // Every axis below is tested, and they accumulate into one change record. An
            // element that was both moved and rotated has changed in two ways and a take-off
            // needs both of them, not just the first one we happened to look for.
            Change c = null;
            double dist = -1;
            double dist2 = -1;

            bool movedPrimary = didMove(current.LocationPoint, previous.LocationPoint, MoveTolerance, out dist);
            bool movedSecondary = (current.LocationPoint2 != null) && (previous.LocationPoint2 != null) &&
                                   didMove(previous.LocationPoint2, current.LocationPoint2, MoveTolerance, out dist2);

            if (movedPrimary || movedSecondary)
            {
                c = addGeometryChange(c, current, Change.ChangeTypeEnum.Move,
                                      "Location Offset " + getHumanComparison(movedPrimary ? dist : dist2));

                if (movedPrimary && movedSecondary)
                {
                    // both moved. record both.
                    c.MoveDescription = Utilities.RevitUtils.SerializeDoubleMove(previous.LocationPoint, current.LocationPoint,
                                                                                  previous.LocationPoint2, current.LocationPoint2);
                }
                else if (movedPrimary)
                {
                    // single move.
                    c.MoveDescription = Utilities.RevitUtils.SerializeMove(previous.LocationPoint, current.LocationPoint);
                }
                else
                {
                    // only one side moved though...
                    c.MoveDescription = Utilities.RevitUtils.SerializeMove(previous.LocationPoint2, current.LocationPoint2);
                }
            }

            // check rotation
            // old, fixed: float rotationTolerance = 0.0349f; // two degrees?
            float rotationDiff = current.Rotation - previous.Rotation;

            if (Math.Abs(rotationDiff) > RotateTolerance)
            {
                c = addGeometryChange(c, current, Change.ChangeTypeEnum.Rotate,
                                      "Rotation: " + ((rotationDiff) * 180.0 / Math.PI).ToString("F2") + " degrees");

                c.RotationDescription = Utilities.RevitUtils.SerializeRotation(current.LocationPoint, XYZ.BasisZ, rotationDiff);
            }

            // now the bounding box...
            if ((current.BoundingBox != null) && (previous.BoundingBox != null))
            {
                double maxDist = Math.Max(current.BoundingBox.Min.DistanceTo(previous.BoundingBox.Min),
                                           current.BoundingBox.Max.DistanceTo(previous.BoundingBox.Max));

                double sizeDelta = -1;
                bool resized = didResize(current.BoundingBox, previous.BoundingBox, MoveTolerance, out sizeDelta);

                // Rotating an element changes the extents of its axis-aligned bounding box
                // without the element itself changing size, and the box alone cannot tell the
                // two apart - so once a rotation is reported, a size difference proves nothing.
                bool rotated = (c != null) && c.HasChangeType(Change.ChangeTypeEnum.Rotate);

                // A box that only shifted is just a restatement of the move or rotation already
                // reported above, so it stays suppressed as noise - that part of the original
                // behaviour is deliberate. A box whose SIZE changed is a different fact: the
                // element itself got bigger or smaller, which changes quantities, so it is
                // always reported whether or not the element also moved.
                if (resized && (rotated == false))
                {
                    c = addGeometryChange(c, current, Change.ChangeTypeEnum.GeometryChange,
                                          "BoundingBox Size Change " + getHumanComparison(sizeDelta));
                }
                else if ((c == null) && (maxDist > MoveTolerance))
                {
                    c = addGeometryChange(c, current, Change.ChangeTypeEnum.GeometryChange,
                                          "BoundingBox Offset " + getHumanComparison(maxDist));
                }
            }

            return c;

        }

        /// <summary>
        /// Start a geometric change record, or fold another change type into the one already
        /// started for this element, so that a compound geometric edit stays a single record.
        /// </summary>
        private Change addGeometryChange(Change existing, RevitElement current, Change.ChangeTypeEnum type, string description)
        {
            if (existing != null)
            {
                existing.AddChangeType(type);
                existing.ChangeDescription = existing.ChangeDescription + "; " + description;
                return existing;
            }

            Change c = new Change()
            {
                ChangeType = type,
                Category = current.Category,
                ElementId = current.ElementId,
                UniqueId = current.UniqueId,
                ChangeDescription = description,
                Level = current.Level
            };
            if (current.BoundingBox != null) c.BoundingBoxDescription = Utilities.RevitUtils.SerializeBoundingBox(current.BoundingBox);

            return c;
        }

        /// <summary>
        /// Did the element's bounding box change size, as opposed to just moving? Size is what
        /// a quantity take-off cares about, and it survives the element also having been moved.
        /// </summary>
        private bool didResize(BoundingBoxXYZ current, BoundingBoxXYZ previous, double tolerance, out double delta)
        {
            XYZ currentSize = current.Max - current.Min;
            XYZ previousSize = previous.Max - previous.Min;

            delta = Math.Max(Math.Abs(currentSize.X - previousSize.X),
                    Math.Max(Math.Abs(currentSize.Y - previousSize.Y),
                             Math.Abs(currentSize.Z - previousSize.Z)));

            return delta > tolerance;
        }

        private bool didMove(XYZ oldPoint, XYZ newPoint, double tolerance, out double distance)
        {
            distance = -1;
            if (newPoint == null) return false;
            if (oldPoint == null) return false;

            distance = newPoint.DistanceTo(oldPoint);
            if (distance > tolerance)
            {
                return true;
            }

            return false;
        }

        private void readModel()
        {

            if (!AllCategories)
            {
                foreach (var c in RequestedCategories) _requestedCategoryNames.Add(c.Name);
                
            }
            
            FilteredElementCollector coll = new FilteredElementCollector(_doc);
            coll.WhereElementIsNotElementType();

            var elems = coll.ToElements().Where(e => e.Category != null).ToList();
            Dictionary<ElementId, Element> typesToCheck = new Dictionary<ElementId, Element>();
            foreach( var elem in elems )
            {
                if (elem.Category == null) continue; // we don't want this.

                ElementId typeId = elem.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    if (typesToCheck.ContainsKey(typeId) == false) typesToCheck[typeId] = _doc.GetElement(typeId);
                }
            }

            //////////////////
            populateExisting(typesToCheck.Values.ToList(), true);
            populateExisting(elems, false);
            
        }

        private void populateExisting(IList<Element> elems, bool isTypes)
        {
            // get the levels:
            if (_allLevels == null)
            {
                FilteredElementCollector coll = new FilteredElementCollector(_doc);
                coll.OfClass(typeof(Level));

                _allLevels = coll.Cast<Level>().ToList();
            }

            foreach ( var e in elems)
            {
                Category c = e.Category;
                if (e is FamilySymbol)
                {
                    FamilySymbol fs = e as FamilySymbol;
                    c = fs.Family.FamilyCategory;
                }

                if (c == null) continue; // we don't want these things?

                if (!AllCategories && (_requestedCategoryNames.Contains(c.Name) == false)) continue; // not appropriate

                // count the category usage, for reference.
                if (_categoryCount.ContainsKey(c.Name) == false) _categoryCount[c.Name] = 0;
                _categoryCount[c.Name]++;

                // consolidate
                var revitElem = makeRevitElemFromElement(e, true);
               
                _currentElems.Add(e.Id.AsLong(), revitElem);

              
                
            }
        }
        private void readPrevious()
        {
            readHeader();

            // DUH: We still need to read the database.
            // EpisodeGUID helps us with knowing what elements have changed - but 
            // NOT with knowing what has changed about them!

            //if (UseEpisodeGuid)
            //{
            //    if (canUseEpisodeGuid())
            //    {
            //        _doc.Application.WriteJournalComment("Using Document EpisodeGUID for retrieval!", true);
            //        return;
            //    }
            //    _doc.Application.WriteJournalComment("Unable to use Document EpisodeGUID for retrieval! Continuing with traditional!", true);
            //}
            //UseEpisodeGuid = false; // reset, in case.

            readParameters();
            readValues();
            readElements();
            readGeometry();
            readLinks();
        }

        /// <summary>
        /// Read the link manifest recorded when the previous snapshot was taken. Snapshots
        /// written before schema 1.2 have no manifest; readHeader has already run the
        /// upgrade scripts by this point, so the table exists but is empty, and an empty
        /// manifest simply means nothing can be said about links.
        /// </summary>
        private void readLinks()
        {
            _previousLinks = new List<LinkState>();

            try
            {
                using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
                {
                    conn.Open();

                    var check = conn.CreateCommand();
                    check.CommandText = "select name from sqlite_master WHERE type='table' and name='_objects_links'";
                    if (check.ExecuteScalar() == null)
                    {
                        _doc.Application.WriteJournalComment("Previous snapshot has no link manifest (pre-1.2 schema).", false);
                        return;
                    }

                    var cmd = conn.CreateCommand();
                    cmd.CommandText = "select instance_id,type_id,name,path,status,is_nested,doc_guid,num_saves from _objects_links";
                    using (SQLiteDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            _previousLinks.Add(new LinkState()
                            {
                                InstanceId = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                                TypeId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                                Name = readString(reader, 2),
                                Path = readString(reader, 3),
                                Status = readString(reader, 4),
                                IsNested = !reader.IsDBNull(5) && reader.GetInt32(5) == 1,
                                DocumentGuid = reader.IsDBNull(6) ? null : readString(reader, 6),
                                NumberOfSaves = reader.IsDBNull(7) ? -1 : reader.GetInt32(7)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Never fail a comparison because the manifest could not be read - the
                // element diff is still worth having.
                _doc.Application.WriteJournalComment("Could not read link manifest: " + ex.GetType().Name + ": " + ex.Message, false);
                _previousLinks = new List<LinkState>();
            }

            _doc.Application.WriteJournalComment("Previous snapshot recorded " + _previousLinks.Count + " link(s).", false);
        }

        /// <summary>Text columns come back as byte[] rather than TEXT in these snapshots.</summary>
        private string readString(SQLiteDataReader reader, int column)
        {
            if (reader.IsDBNull(column)) return String.Empty;

            object o = reader.GetValue(column);
            byte[] bytes = o as byte[];
            if (bytes != null) return Encoding.UTF8.GetString(bytes);

            return o.ToString();
        }

        /// <summary>
        /// Compare the recorded links against those loaded right now, and work out which
        /// differences can make unrelated elements look changed.
        ///
        /// The decisive distinction is between a link whose CONTENT changed and one whose
        /// mere PRESENCE changed. The first makes downstream room changes real; the second
        /// makes them an artefact of what happened to be loaded at export time. A content
        /// fingerprint only exists for a loaded link, so when either side was unloaded the
        /// honest answer is that we cannot tell - which is treated as suspect, not as clean.
        /// </summary>
        private void buildLinkWarnings()
        {
            LinkWarnings = new List<LinkWarning>();
            _suspectCategories = new HashSet<string>();

            IList<LinkState> currentLinks;
            try
            {
                currentLinks = Utilities.RevitUtils.CollectLinkStates(_doc);
            }
            catch (Exception ex)
            {
                _doc.Application.WriteJournalComment("Could not collect current link states: " + ex.Message, false);
                return;
            }

            if ((_previousLinks.Count == 0) && (currentLinks.Count == 0)) return;

            if (_previousLinks.Count == 0)
            {
                // Nothing recorded to compare against - say so rather than implying the
                // links were verified and found identical.
                LinkWarnings.Add(new LinkWarning()
                {
                    Issue = LinkWarning.LinkIssueEnum.ContentUnknown,
                    LinkName = "(all links)",
                    CausesSuspectChanges = currentLinks.Count > 0,
                    Description = "The previous snapshot recorded no link manifest, so link state could not be " +
                                  "compared. " + currentLinks.Count + " link(s) are loaded now; any room or space " +
                                  "change below may reflect a link difference rather than a design change."
                });
                if (currentLinks.Count > 0) markSpatialCategoriesSuspect();
                return;
            }

            Dictionary<string, LinkState> previousByKey = new Dictionary<string, LinkState>();
            foreach (var link in _previousLinks)
            {
                previousByKey[link.Key] = link;
            }

            bool anySuspect = false;

            foreach (var current in currentLinks)
            {
                LinkState previous = null;
                if (previousByKey.TryGetValue(current.Key, out previous))
                {
                    previousByKey.Remove(current.Key);

                    // A link absent from both snapshots bounded nothing either time, so no
                    // difference in it can explain a change. Saying nothing is the correct
                    // report - warning here would fire on every comparison and train the
                    // reader to ignore the warnings that do matter.
                    if (!current.ContributesGeometry && !previous.ContributesGeometry) continue;

                    bool statusChanged = !String.Equals(previous.Status, current.Status, StringComparison.OrdinalIgnoreCase);
                    bool? contentChanged = current.ContentChangedFrom(previous);

                    if (contentChanged == true)
                    {
                        LinkWarnings.Add(new LinkWarning()
                        {
                            Issue = LinkWarning.LinkIssueEnum.ContentChanged,
                            LinkName = current.Name,
                            Path = current.Path,
                            PreviousStatus = previous.Status,
                            CurrentStatus = current.Status,
                            CausesSuspectChanges = false,
                            Description = "The linked file was revised between the two snapshots. Changes to " +
                                          "elements bounded by it are real."
                        });
                    }
                    else if (statusChanged)
                    {
                        anySuspect = true;
                        LinkWarnings.Add(new LinkWarning()
                        {
                            Issue = LinkWarning.LinkIssueEnum.LoadStateChanged,
                            LinkName = current.Name,
                            Path = current.Path,
                            PreviousStatus = previous.Status,
                            CurrentStatus = current.Status,
                            CausesSuspectChanges = true,
                            Description = "The link went from " + previous.Status + " to " + current.Status +
                                          (contentChanged == false
                                              ? " with no change to its content."
                                              : ", and its content could not be compared because it was unloaded on at least one side.") +
                                          " Room and space changes driven by this link are probably artefacts of what was loaded, not design changes."
                        });
                    }
                    else if (contentChanged == null)
                    {
                        anySuspect = true;
                        LinkWarnings.Add(new LinkWarning()
                        {
                            Issue = LinkWarning.LinkIssueEnum.ContentUnknown,
                            LinkName = current.Name,
                            Path = current.Path,
                            PreviousStatus = previous.Status,
                            CurrentStatus = current.Status,
                            CausesSuspectChanges = true,
                            Description = "The link was " + current.Status + " on both sides, so its content could " +
                                          "not be fingerprinted and a revision cannot be ruled out."
                        });
                    }
                }
                else
                {
                    // Only a link that actually contributes geometry can explain a change;
                    // one added but left unloaded is worth noting, not worth distrusting.
                    bool suspect = current.ContributesGeometry;
                    anySuspect = anySuspect || suspect;
                    LinkWarnings.Add(new LinkWarning()
                    {
                        Issue = LinkWarning.LinkIssueEnum.LinkAdded,
                        LinkName = current.Name,
                        Path = current.Path,
                        CurrentStatus = current.Status,
                        CausesSuspectChanges = suspect,
                        Description = "This link is present now but was not recorded in the previous snapshot." +
                                      (suspect
                                          ? " It is contributing geometry, so rooms bounded by it may appear to have changed."
                                          : " It is not loaded, so it does not affect any measurement.")
                    });
                }
            }

            // Whatever is left was in the previous snapshot and is gone now.
            foreach (var removed in previousByKey.Values)
            {
                bool suspect = removed.ContributesGeometry;
                anySuspect = anySuspect || suspect;
                LinkWarnings.Add(new LinkWarning()
                {
                    Issue = LinkWarning.LinkIssueEnum.LinkRemoved,
                    LinkName = removed.Name,
                    Path = removed.Path,
                    PreviousStatus = removed.Status,
                    CausesSuspectChanges = suspect,
                    Description = "This link was recorded in the previous snapshot and is no longer present." +
                                  (suspect
                                      ? " It was contributing geometry, so rooms that relied on it may appear to have changed."
                                      : " It was not loaded then either, so it affected no measurement.")
                });
            }

            if (anySuspect) markSpatialCategoriesSuspect();

            if (LinkWarnings.Count > 0)
            {
                _doc.Application.WriteJournalComment("Link manifest differences: " + LinkWarnings.Count, false);
            }
        }

        /// <summary>
        /// Categories whose geometry is computed from whatever bounds them, and so are the
        /// ones a link change can silently distort.
        /// </summary>
        private void markSpatialCategoriesSuspect()
        {
            _suspectCategories.Add("Rooms");
            _suspectCategories.Add("Spaces");
            _suspectCategories.Add("Areas");
        }

        /// <summary>
        /// Flag changes that a link difference could have caused. The change is still
        /// reported in full - it is labelled, not hidden, because a real edit to a room
        /// during the same period would otherwise be discarded along with the noise.
        /// </summary>
        private void flagSuspectChanges(IList<Change> changes)
        {
            if (_suspectCategories.Count == 0) return;

            int flagged = 0;
            foreach (var change in changes)
            {
                if (change.Category == null) continue;
                if (_suspectCategories.Contains(change.Category) == false) continue;

                change.PossibleLinkArtifact = true;
                flagged++;
            }

            if (flagged > 0)
            {
                _doc.Application.WriteJournalComment(
                    "Flagged " + flagged + " room/space change(s) as possible link artefacts.", false);
            }
        }

        private void readHeader()
        {
            try
            {
                using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3"))
                {
                    conn.Open();

                    // first see if there is a headers table.
                    var cmd = conn.CreateCommand();
                    cmd.CommandText = "select name from sqlite_master WHERE type='table' and name='_objects_header'";
                    object table = cmd.ExecuteScalar();
                    Version schemaVersion = new Version(0, 0);
                    if (table != null)
                    {
                        // there are headers, check the schema version
                        var sv = conn.CreateCommand();
                        sv.CommandText = "select value from _objects_header WHERE keyword=\"SchemaVersion\"";
                        object o = sv.ExecuteScalar();
                        if (o != null)
                        {
                            string val = o.ToString();
                            if (o is byte[]) val = UTF8Encoding.ASCII.GetString(o as byte[]); // not sure why we get a byte array, but it happens.

                            if (Version.TryParse(val, out schemaVersion))
                            {
                              
                            }
                        }
                        
                    }
                    if (schemaVersion < Utilities.DataUtility.CurrentVersion)
                    {
                        Utilities.DataUtility.UpgradeFrom(conn, schemaVersion, (msg) => { _doc.Application.WriteJournalComment(msg, false); });
                    }

                    // now we can get around to reading the actual headers.
                    var headCmd = conn.CreateCommand();
                    headCmd.CommandText = "select * from _objects_header";
                    SQLiteDataReader reader = headCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string key = reader.GetString(1);
                        string val = reader.GetString(2);
                        _headerDict[key] = val;
                    }

                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                _doc.Application.WriteJournalComment("Error reading headers: " + ex.GetType().Name + ": " + ex.Message, false);
            }
           
        }

        private void readParameters()
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                conn.Open();

                var cmd = conn.CreateCommand();
                cmd.CommandText = "select id,name FROM _objects_attr";

                SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string name = reader.GetString(1);
                    _parameterDict[id] = name;
                }

            }
        }

        private void readValues()
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                conn.Open();

                var cmd = conn.CreateCommand();
                cmd.CommandText = "select id,value FROM _objects_val";

                SQLiteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string value = reader.GetString(1);
                    _valueDict[id] = value;
                }

            }
        }

        private void readElements()
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                conn.Open();

                var cmd = conn.CreateCommand();
                cmd.CommandText = "select id,entity_id,attribute_id,value_id FROM _objects_eav";

                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int entity_id = reader.GetInt32(1);
                        int attribute_id = reader.GetInt32(2);
                        int value_id = reader.GetInt32(3);

                        if (_idValues.ContainsKey(entity_id) == false) _idValues[entity_id] = new RevitElement() { ElementId = entity_id };

                        _idValues[entity_id].ParameterValueIds[attribute_id] = value_id;

                        // we don't want EDITED_BY to be considered?
                        if (attribute_id == -1002067) continue;
                        
                        _idValues[entity_id].Parameters[_parameterDict[attribute_id]] = _valueDict[value_id];

                    }
                }

            

                /// read the ID information for each element.
                cmd = conn.CreateCommand();
                cmd.CommandText = "select id,external_id,category,isType,versionGuid FROM _objects_id";
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long id = reader.GetInt64(0);
                        if (_idValues.ContainsKey(id) == false) _idValues[id] = new RevitElement() { ElementId = id };
                        string guid = reader.GetString(1);
                        string cat = reader.GetString(2);
                        int isType = reader.GetInt32(3);
                        string verguid = null;
                        if (reader.IsDBNull(4) == false) verguid = reader.GetString(4);
                      
                        

                        var elem = _idValues[id];
                        elem.Category = cat;
                        elem.UniqueId = guid;
                        elem.IsType = (isType == 1);
                        elem.VersionGuid = verguid;
                    }
                }
            }
        }

        private void readGeometry()
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                conn.Open();

                var cmd = conn.CreateCommand();
                cmd.CommandText = "select id,BoundingBoxMin,BoundingBoxMax,Location,Location2,Level,Rotation FROM _objects_geom";

                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long entity_id = reader.GetInt64(0);

                        if (_idValues.ContainsKey(entity_id) == false) continue; // not sure how, but let's protect just in case.
                        string bbMin = reader.GetString(1);
                        string bbMax = reader.GetString(2);
                        string lp = reader.GetString(3);
                        string lp2 = reader.GetString(4);
                        string levName = reader.GetString(5);
                        float rot = reader.GetFloat(6);

                        RevitElement elem = _idValues[entity_id];

                        if (!String.IsNullOrEmpty(bbMin)) elem.BoundingBox = new BoundingBoxXYZ()
                        {
                            Min = parsePoint(bbMin),
                            Max = parsePoint(bbMax)
                        };
                        if (!String.IsNullOrEmpty(lp)) elem.LocationPoint = parsePoint(lp);
                        if (!String.IsNullOrEmpty(lp2)) elem.LocationPoint2 = parsePoint(lp2);

                        elem.Level = levName;
                        elem.Rotation = rot;
                    }
                }
            }
        }

        /// <summary>
        /// Determine if we can use EpisodeGuid, based on the header information.
        /// </summary>
        /// <returns></returns>
        private bool canUseEpisodeGuid()
        {
            if (_headerDict == null) return false;

            // _headerDict["DocumentGuid"] = ver.VersionGUID.ToString();
            // _headerDict["NumSaves"] = ver.NumberOfSaves.ToString();

            // not sure how we would get in here without being a sufficient version of Revit, but let's check.
            if (Int32.TryParse(_doc.Application.VersionNumber, out int verNum))
            { 
                if (verNum < 2023)
                {
                    _doc.Application.WriteJournalComment("DocumentVersion not supported in Revit version " + verNum, false);
                    return false;
                }
            }

            if (_headerDict.ContainsKey("DocumentGuid"))
            {
                if (Guid.TryParse(_headerDict["DocumentGuid"], out Guid test))
                {
                    return true;
                }
                _doc.Application.WriteJournalComment("Header contains a DocumentGUID, but it is not a valid GUID? (" + _headerDict["DocumentGuid"] + ")? Cannot leverage it!", true);
            }
            else
            {
                _doc.Application.WriteJournalComment("Header does not contain a DocumentGUID. Cannot leverage it!", true);
            }

            return false;
        }

        private RevitElement makeRevitElemFromId(ElementId id, bool withParams)
        {
            Element e = _doc.GetElement(id);
            return makeRevitElemFromElement(e, withParams);
        }
        private RevitElement makeRevitElemFromElement(Element e, bool withParams)
        {
           
            Category c = e.Category;

            var revitElem = new RevitElement() { ElementId = e.Id.AsLong(), Category = (c != null) ? c.Name : "(none)" };
#if REVIT2015 || REVIT2016 || REVIT2017 || REVIT2018 || REVIT2019 || REVIT2020
                // do nothing here
#else
            if (e.VersionGuid != null) revitElem.VersionGuid = e.VersionGuid.ToString();
#endif

            if (withParams)
            {
                IList<Autodesk.Revit.DB.Parameter> parms = Utilities.RevitUtils.GetParameters(e);
                foreach (var p in parms)
                {
                    //Quick and Dirty - will need to call different stuff for each thing
                    try
                    {
                        if (p.Definition == null) continue; // we don't want this!
                        string definition = p.Definition.Name;

                        
                        string val = null;
                        switch (p.StorageType)
                        {
                            case StorageType.String:
                                val = p.AsString();
                                break;

                            default:
                                val = p.AsValueString();
                                break;
                        }

                        if (val == null) val = "(n/a)";

                        revitElem.Parameters[p.Definition.Name] = val;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("Weird database: " + ex);
                    }
                }
            }
            revitElem.IsType = e is ElementType;

            if (!revitElem.IsType)
            {
                // seen at least one case where retrieving bounding box threw an internal error
                BoundingBoxXYZ box = null;
                try
                {
                    box = e.get_BoundingBox(null);
                }
                catch (Exception ex)
                {
                    _doc.Application.WriteJournalComment("Encountered error trying to get BoundingBox of Element Id: " + e.Id + " Exception: " + ex.GetType().Name + ": " + ex.Message, false);
                    if (ex is Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        var aex = ex as Autodesk.Revit.Exceptions.ApplicationException;
                        _doc.Application.WriteJournalComment("  => " + aex.FunctionId + " " + aex.HResult + " " + aex.Source, false);
                    }
                }
                if (box != null) revitElem.BoundingBox = box;

                LocationPoint lp = e.Location as LocationPoint;
                if (lp != null)
                {
                    try
                    {
                        revitElem.LocationPoint = lp.Point;
                        if (e is FamilyInstance)
                        {
                            // special cases.
                            if (e.Category.Id.IsCategory(BuiltInCategory.OST_Columns) ||
                                e.Category.Id.IsCategory(BuiltInCategory.OST_StructuralColumns))
                            {
                                // in this case, get the Z value from the 
                                var offset = e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);

                                if ((e.LevelId != ElementId.InvalidElementId) && (offset != null))
                                {
                                    Level levPt1 = _doc.GetElement(e.LevelId) as Level;
                                    double newZ = levPt1.Elevation + offset.AsDouble();
                                    revitElem.LocationPoint = new XYZ(revitElem.LocationPoint.X, revitElem.LocationPoint.Y, newZ);
                                }
                            }

                            if ((e as FamilyInstance).CanRotate)
                            {
                                revitElem.Rotation = (float)lp.Rotation;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("Error on " + e.Name + ": " + e.GetType().Name + ": " + ex.Message);
                    }
                }
                else
                {
                    LocationCurve lc = e.Location as LocationCurve;
                    if (lc != null)
                    {
                        if (lc.Curve.IsBound)
                        {
                            revitElem.LocationPoint = lc.Curve.GetEndPoint(0);
                            revitElem.LocationPoint2 = lc.Curve.GetEndPoint(1);
                        }
                    }
                    else
                    {
                        // special case
                        if (e is Grid)
                        {
                            Grid g = e as Grid;
                            revitElem.LocationPoint = g.Curve.GetEndPoint(0);
                            revitElem.LocationPoint2 = g.Curve.GetEndPoint(1);
                        }
                    }
                }


                if (e.LevelId != ElementId.InvalidElementId)
                {
                    revitElem.Level = _doc.GetElement(e.LevelId).Name;
                }
                else
                {
                    if (revitElem.LocationPoint != null)
                    {


                        // we want the next level down from the z value...
                        Level lev = Utilities.RevitUtils.GetNextLevelDown(revitElem.LocationPoint, _allLevels);
                        if (lev != null) revitElem.Level = lev.Name;
                    }
                }
            }
            return revitElem;
        }

        /// <summary>
        /// From a string point, return an XYZ 
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        private XYZ parsePoint(string input)
        {
            if (String.IsNullOrEmpty(input)) return null;

            string[] pieces = input.Split(',');
            if (pieces.Length != 3) return null;

            return new XYZ(Double.Parse(pieces[0], CultureInfo.InvariantCulture), Double.Parse(pieces[1], CultureInfo.InvariantCulture), Double.Parse(pieces[2], CultureInfo.InvariantCulture));
        }
#endregion
    }
}
