using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using System.Globalization;

namespace Metamorphosis
{
    internal class SnapshotMaker
    {
        private Document _doc;
        private Dictionary<long, Parameter> _paramDict = new Dictionary<long, Parameter>();
        private Dictionary<string, int> _valueDict = new Dictionary<string, int>();
        private Dictionary<string, string> _headerDict = new Dictionary<string, string>();
        private string _filename;
        private string _dbFilename;
        private int _valueId = 0;
        private List<Level> _allLevels;
        private Utilities.Settings.LogLevel _logLevel = Utilities.Settings.LogLevel.Basic;


        #region Constructor
        internal SnapshotMaker(Document doc, string filename)
        {
            _doc = doc;
            _filename = filename;

            _dbFilename = _filename;
            // see: http://system.data.sqlite.org/index.html/info/bbdda6eae2
            if (_filename.StartsWith(@"\\")) _dbFilename = @"\\" + _dbFilename;

            _logLevel = Utilities.Settings.GetLogLevel();
        }
        #endregion

        #region Accessor
        internal TimeSpan Duration { get; private set; }
        #endregion

        #region PublicMethods
        internal void Export()
        {
            // make the sqlite database
            createDatabase();
            // populate
            exportParameterData();

            _doc.Application.WriteJournalComment("Export Completed. Releasing hold on database file.", false);
            // release the hold on the database 
            //https://stackoverflow.com/questions/8511901/system-data-sqlite-close-not-releasing-database-file
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        #endregion

        #region PrivateMethods
        private void createDatabase()
        {
            _doc.Application.WriteJournalComment("Creating database: " + _filename, false);
            if (File.Exists(_filename)) File.Delete(_filename); // we have to replace the contents anyway

            // ran into a case where the path didn't exist. make it happen.
            string folder = Path.GetDirectoryName(_filename);
            if (Directory.Exists(folder) == false) Directory.CreateDirectory(folder);

            //create the SQLite database file.
            SQLiteConnection.CreateFile(_filename);

            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                conn.Open();
                // create the table structure from the sql instructions.
                string[] lines = Utilities.DataUtility.ReadSQLScript("databaseFormat.txt");
                if (lines == null)
                {
                    throw new InvalidOperationException(
                        "Embedded resource 'databaseFormat.txt' is missing from assembly '" +
                        System.Reflection.Assembly.GetExecutingAssembly().GetName().Name +
                        "'. Without it no tables can be created.");
                }

                foreach (string sql in lines)
                {

                    SQLiteCommand command = new SQLiteCommand(sql, conn);
                    command.ExecuteNonQuery();
                }
            }
        
        }

        
        private void exportParameterData()
        {
            _doc.Application.WriteJournalComment("Retrieving Data...", false);
            DateTime start = DateTime.Now;
            
            // retrieve all of the instance elements, and process them.
            FilteredElementCollector coll = new FilteredElementCollector(_doc);
            coll.WhereElementIsNotElementType();

            Dictionary<ElementId, Element> typeElementsUsed = new Dictionary<ElementId, Element>();
            IList<Element> instances = coll.ToElements().Where(e => e.Category != null).ToList();
            foreach ( var elem in instances)
            {
                if (elem.Category == null) continue; // don't do it!

                // see if the current element has a type element, and make sure we're getting that.
                ElementId typeId = elem.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    if (typeElementsUsed.ContainsKey(typeId) == false)
                    {
                        Element typeElem = _doc.GetElement(typeId);
                        if ((typeElem.Category != null))
                        {
                            typeElementsUsed.Add(typeId, typeElem); // only add if it's a typeElement with Category
                        }
                    }
                }
            }

            string msg = (DateTime.Now - start) + ": " + instances.Count + " instances and " + typeElementsUsed.Count + " types.";
            _doc.Application.WriteJournalComment(msg, false);
            System.Diagnostics.Debug.WriteLine(msg);

            // go through all of the type elements and instances and capture the parameter ids
            _headerDict["SchemaVersion"] = "1.2";
            _headerDict["Model"] = Utilities.RevitUtils.GetModelPath(_doc);
            _headerDict["ExportVersion"] = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            _headerDict["ExportDate"] = DateTime.Now.ToString();
            _headerDict["ExportDateTicks"] = DateTime.Now.Ticks.ToString();
            _headerDict["ExportingUser"] = Environment.UserDomainName + "\\" + Environment.UserName;
            _headerDict["MachineName"] = Environment.MachineName;
            _headerDict["RevitVersion"] = _doc.Application.VersionNumber;
            _headerDict["RevitBuild"] = _doc.Application.VersionBuild;

#if REVIT2015 || REVIT2016 || REVIT2017 || REVIT2018
                // do not support Document Version
#else
            DocumentVersion ver = Document.GetDocumentVersion(_doc);
            _headerDict["DocumentGuid"] = ver.VersionGUID.ToString();
            _headerDict["NumSaves"] = ver.NumberOfSaves.ToString();
#endif

            updateHeaderTable();

            updateParameterDictionary(typeElementsUsed.Values.ToList());
            log((DateTime.Now - start) + ": Parameter Dictionary Updated for Types");
            updateParameterDictionary(instances);
            log((DateTime.Now - start) + ": Parameter Dictionary Updated for Instances");

            updateIdTable(typeElementsUsed.Values.ToList(), true);
            log((DateTime.Now - start) + ": Id Table Updated for Types");
            updateIdTable(instances, false);
            log((DateTime.Now - start) + ": Id Table Updated for Instances");
            updateAttributeTable();
            log((DateTime.Now - start) + ": Attribute Table Updated for All");


            updateEntityAttributeValues(typeElementsUsed.Values.ToList());
            log((DateTime.Now - start) + ": Att/Values Table Updated for Types");

            updateEntityAttributeValues(instances);
            log((DateTime.Now - start) + ": Att/Values Table Updated for Instances");
            updateValueTable();
            log((DateTime.Now - start) + ": Value Table Updated for All");

            updateGeometryTable(instances);
            log((DateTime.Now - start) + ": Geometry Table Updated for Types");

            updateLinksTable();
            log((DateTime.Now - start) + ": Links Table Updated");

            Duration = DateTime.Now - start;
            log("Total Time: " + Duration.TotalMinutes + " minutes");
        }

        private void log(string msg)
        {
            _doc.Application.WriteJournalComment(msg, false);
            System.Diagnostics.Debug.WriteLine(msg);
        }

        private void updateIdTable(IList<Element> elements, bool isTypes)
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                string currentQuery = "";
                try
                {
                    conn.Open();
                    using (var transaction = conn.BeginTransaction())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO _objects_id (id,external_id,category,isType,versionguid) VALUES(?,?,?,?,?)";
                        var idParam = cmd.Parameters.Add("id", DbType.Int64);
                        var externalParam = cmd.Parameters.Add("external_id", DbType.String);
                        var categoryParam = cmd.Parameters.Add("category", DbType.String);
                        var isTypeParam = cmd.Parameters.Add("isType", DbType.Int32);
                        var versionParam = cmd.Parameters.Add("versionguid", DbType.String);
                        currentQuery = cmd.CommandText;
                        cmd.Prepare();

                        isTypeParam.Value = isTypes ? 1 : 0;

                        foreach (Element e in elements)
                        {
                            object versionGuid = DBNull.Value;
#if REVIT2015 || REVIT2016 || REVIT2017 || REVIT2018 || REVIT2019 || REVIT2020
                            // we do nothing
#else
                            if (e.VersionGuid != null) versionGuid = e.VersionGuid.ToString();
#endif
                            Category c = e.Category;
                            if (c == null)
                            {
                                FamilySymbol fs = e as FamilySymbol;
                                if (fs != null) c = fs.Family.FamilyCategory;
                            }
                            // No quote doubling needed now the value is bound rather than
                            // interpolated - a category with an apostrophe is just data.
                            string catName = (c != null) ? c.Name : "(none)";

                            idParam.Value = e.Id.AsLong();
                            externalParam.Value = e.UniqueId;
                            categoryParam.Value = catName;
                            versionParam.Value = versionGuid;

                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    log("Exception updating ID Table: " + ex.GetType().Name + ": " + ex.Message);
                    log("Current Query: " + currentQuery);
                    throw; // rethrow;
                }
            }
        }

        private void updateHeaderTable()
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                string currentQuery = "";
                try
                {


                    conn.Open();
                    using (var transaction = conn.BeginTransaction())
                    using (var cmd = conn.CreateCommand())
                    {
                        // Also closes a latent bug: the old code escaped the value into a
                        // local and then interpolated the UNescaped one, so a model path or
                        // user name containing an apostrophe produced invalid SQL.
                        cmd.CommandText = "INSERT INTO _objects_header (keyword,value) VALUES(?,?)";
                        var keywordParam = cmd.Parameters.Add("keyword", DbType.String);
                        var valueParam = cmd.Parameters.Add("value", DbType.String);
                        currentQuery = cmd.CommandText;
                        cmd.Prepare();

                        foreach (var pair in _headerDict)
                        {
                            keywordParam.Value = pair.Key;
                            valueParam.Value = pair.Value;

                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    log("Exception updating header Table: " + ex.GetType().Name + ": " + ex.Message);
                    log("Current Query: " + currentQuery);
                    throw; // rethrow;
                }
            }
        }
        private void updateAttributeTable()
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                string currentQuery = "";
                try
                {
                    conn.Open();
                    using (var transaction = conn.BeginTransaction())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO _objects_attr (id,name,category,data_type) VALUES(?,?,?,?)";
                        var idParam = cmd.Parameters.Add("id", DbType.Int64);
                        var nameParam = cmd.Parameters.Add("name", DbType.String);
                        var categoryParam = cmd.Parameters.Add("category", DbType.String);
                        var dataTypeParam = cmd.Parameters.Add("data_type", DbType.Int32);
                        currentQuery = cmd.CommandText;
                        cmd.Prepare();

                        dataTypeParam.Value = -1;

                        foreach (var pair in _paramDict)
                        {
                            string name = pair.Value.Definition.Name;

#if REVIT2015 || REVIT2016 || REVIT2017 || REVIT2018 || REVIT2019 || REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
                            var group = LabelUtils.GetLabelFor(pair.Value.Definition.ParameterGroup);
                            // maybe we don't need? (int)pair.Value.Definition.ParameterGroup
#else  // newer
                            //var group = LabelUtils.GetLabelFor(pair.Value.Definition.ParameterGroup);
                            var groupForgeId = pair.Value.Definition.GetGroupTypeId();
                            var group = LabelUtils.GetLabelForGroup(groupForgeId);
                            // maybe we don't need the PArameterGroupId?

#endif
                            idParam.Value = pair.Value.Id.AsLong();
                            nameParam.Value = name;
                            categoryParam.Value = group;

                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    log("Exception updating attr Table: " + ex.GetType().Name + ": " + ex.Message);
                    log("Current Query: " + currentQuery);
                    throw; // rethrow;
                }
            }
        }

        private void updateValueTable()
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                string currentQuery = "";
                try
                {
                    conn.Open();
                    using (var transaction = conn.BeginTransaction())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO _objects_val (id,value) VALUES(?,?)";
                        var idParam = cmd.Parameters.Add("id", DbType.Int32);
                        var valueParam = cmd.Parameters.Add("value", DbType.String);
                        currentQuery = cmd.CommandText;
                        cmd.Prepare();

                        foreach (var pair in _valueDict)
                        {
                            idParam.Value = pair.Value;
                            valueParam.Value = pair.Key;

                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    log("Exception updating Value Table: " + ex.GetType().Name + ": " + ex.Message);
                    log("Current Query: " + currentQuery);
                    throw; // rethrow;
                }
            }
        }

        private void updateEntityAttributeValues(IList<Element> elems)
        {

            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                string currentQuery = "";
                try
                {


                    conn.Open();
                    using (var transaction = conn.BeginTransaction())
                    // One prepared statement reused for every row. This loop runs once per
                    // parameter per element - over 400,000 times on a real model - and building
                    // then parsing a fresh SQL string each time dominated the whole export.
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO _objects_eav (entity_id,attribute_id,value_id) VALUES(?,?,?)";
                        var entityParam = cmd.Parameters.Add("entity_id", DbType.Int64);
                        var attributeParam = cmd.Parameters.Add("attribute_id", DbType.Int64);
                        var valueParam = cmd.Parameters.Add("value_id", DbType.Int32);
                        currentQuery = cmd.CommandText;
                        cmd.Prepare();

                        foreach (Element e in elems)
                        {
                            IList<Parameter> parms = Utilities.RevitUtils.GetParameters(e);
                            long elementId = e.Id.AsLong();

                            foreach (var p in parms)
                            {
                                if (p.Definition == null) continue; // don't want that!

                                //Quick and Dirty - will need to call different stuff for each thing
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

                                if (_valueDict.ContainsKey(val) == false)
                                {
                                    _valueId++;
                                    _valueDict.Add(val, _valueId);
                                }

                                entityParam.Value = elementId;
                                attributeParam.Value = p.Id.AsLong();
                                valueParam.Value = _valueDict[val];

                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    log("Exception updating EAV Table: " + ex.GetType().Name + ": " + ex.Message);
                    log("Current Query: " + currentQuery);
                    throw; // rethrow;
                }

            }
        }
        private void updateParameterDictionary(IList<Element> elems)
        {

            foreach( Element e in elems)
            {
                IList<Parameter> parms = Utilities.RevitUtils.GetParameters(e);

                                             
                foreach( Parameter p in parms )
                {
                    if (p.Definition == null) continue; // ignore!
                    if (_paramDict.ContainsKey(p.Id.AsLong()) == false) _paramDict.Add(p.Id.AsLong(), p);
                }                                
            }
        }


     

        private void updateGeometryTable(IList<Element> elements)
        {
            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                using (var cmd = conn.CreateCommand())
                {
                    // Prepared once, reused per element - same reasoning as the EAV table,
                    // and it also removes the hand-rolled quote escaping on the level name.
                    cmd.CommandText = "INSERT INTO _objects_geom (id,BoundingBoxMin,BoundingBoxMax,Location,Location2,Level,Rotation) VALUES(?,?,?,?,?,?,?)";
                    var idParam = cmd.Parameters.Add("id", DbType.Int64);
                    var bbMinParam = cmd.Parameters.Add("BoundingBoxMin", DbType.String);
                    var bbMaxParam = cmd.Parameters.Add("BoundingBoxMax", DbType.String);
                    var locParam = cmd.Parameters.Add("Location", DbType.String);
                    var loc2Param = cmd.Parameters.Add("Location2", DbType.String);
                    var levelParam = cmd.Parameters.Add("Level", DbType.String);
                    var rotationParam = cmd.Parameters.Add("Rotation", DbType.Single);
                    cmd.Prepare();

                    foreach (Element e in elements)
                    {
                        BoundingBoxXYZ box = e.get_BoundingBox(null);
                        Location loc = e.Location;

                        if ((loc == null) && (box == null)) continue; // nothing to see here.

                        String bbMin = String.Empty;
                        String bbMax = String.Empty;
                        string lp = String.Empty;
                        string lp2 = String.Empty;
                        float rotation = -1.0f;

                        if (box != null)
                        {
                            bbMin = Utilities.RevitUtils.SerializePoint(box.Min);
                            bbMax = Utilities.RevitUtils.SerializePoint(box.Max);
                        }

                        XYZ p1 = null;
                        if (loc != null)
                        {
                            LocationPoint pt = loc as LocationPoint;
                            if (pt != null)
                            {
                                try
                                {
                                    // noted a time where with a group it didn't work.

                                    XYZ pt1 = pt.Point;
                                    // special cases.
                                    if (e.Category.Id.IsCategory(BuiltInCategory.OST_Columns) ||
                                        e.Category.Id.IsCategory(BuiltInCategory.OST_StructuralColumns))
                                    {
                                        // in this case, get the Z value from the 
                                        var offset = e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);

                                        if ((e.LevelId != ElementId.InvalidElementId) && (offset != null))
                                        {
                                            Level levPt1 = lookupLevel(e, pt1);
                                            double newZ = levPt1.Elevation + offset.AsDouble();
                                            pt1 = new XYZ(pt1.X, pt1.Y, newZ);
                                        }
                                    }

                                    lp = Utilities.RevitUtils.SerializePoint(pt1);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine("Error: " + e.Name + ": " + e.GetType().Name + ": " + ex.Message);
                                }
                                try
                                {
                                    if (e is FamilyInstance)
                                    {
                                        if ((e as FamilyInstance).CanRotate)
                                        {
                                            rotation = (float)pt.Rotation;
                                        }
                                    }
                                }
                                catch {  // swallow. Some just don't like it...
                                }
                            }
                            else
                            {
                                LocationCurve crv = loc as LocationCurve;
                                if (crv != null)
                                {
                                    if (crv.Curve.IsBound)
                                    {
                                        p1 = crv.Curve.GetEndPoint(0);
                                        XYZ p2 = crv.Curve.GetEndPoint(1);
                                        lp = Utilities.RevitUtils.SerializePoint(p1);
                                        lp2 = Utilities.RevitUtils.SerializePoint(p2);
                                    }
                                }
                                else
                                {
                                    if (box == null)
                                    {
                                        // ok, special case one: Grid
                                        if (e is Grid)
                                        {
                                            Grid g = e as Grid;
                                            p1 = g.Curve.GetEndPoint(0);
                                            XYZ p2 = g.Curve.GetEndPoint(1);
                                            lp = Utilities.RevitUtils.SerializePoint(p1);
                                            lp2 = Utilities.RevitUtils.SerializePoint(p2);
                                        }
                                        else
                                        {
                                            continue; // not sure what this is???
                                        }
                                    }
                                }
                            }
                        }

                        // retrieve the level
                        Level lev = lookupLevel(e, p1);
                        string levName = String.Empty;
                        if (lev != null) levName = lev.Name;

                        idParam.Value = e.Id.AsLong();
                        bbMinParam.Value = bbMin;
                        bbMaxParam.Value = bbMax;
                        locParam.Value = lp;
                        loc2Param.Value = lp2;
                        levelParam.Value = levName;
                        rotationParam.Value = rotation;

                        if (_logLevel == Utilities.Settings.LogLevel.Verbose)
                        {
                            _doc.Application.WriteJournalComment(
                                String.Format("_objects_geom id={0} bbMin='{1}' bbMax='{2}' loc='{3}' loc2='{4}' level='{5}' rot={6}",
                                    e.Id.AsLong(), bbMin, bbMax, lp, lp2, levName,
                                    rotation.ToString(CultureInfo.InvariantCulture)), false);
                        }

                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
            }
        }

        /// <summary>
        /// Record which links were present, and in what state, when this snapshot was taken.
        ///
        /// Without this a link being unloaded, reloaded or swapped for another revision
        /// between two snapshots is indistinguishable from someone having moved the walls -
        /// every room bounded by that link reports an area change either way. Storing the
        /// state is what lets the comparison tell those two apart afterwards.
        /// </summary>
        private void updateLinksTable()
        {
            IList<Objects.LinkState> links;
            try
            {
                links = Utilities.RevitUtils.CollectLinkStates(_doc);
            }
            catch (Exception ex)
            {
                // A snapshot without a link manifest is still a usable snapshot, so this
                // must never take the export down with it.
                log("Could not collect link states: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            using (SQLiteConnection conn = new SQLiteConnection("Data Source=" + _dbFilename + ";Version=3;"))
            {
                string currentQuery = "";
                try
                {
                    conn.Open();
                    using (var transaction = conn.BeginTransaction())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO _objects_links (instance_id,type_id,name,path,status,is_nested,doc_guid,num_saves) VALUES(?,?,?,?,?,?,?,?)";
                        var instanceParam = cmd.Parameters.Add("instance_id", DbType.Int64);
                        var typeParam = cmd.Parameters.Add("type_id", DbType.Int64);
                        var nameParam = cmd.Parameters.Add("name", DbType.String);
                        var pathParam = cmd.Parameters.Add("path", DbType.String);
                        var statusParam = cmd.Parameters.Add("status", DbType.String);
                        var nestedParam = cmd.Parameters.Add("is_nested", DbType.Int32);
                        var guidParam = cmd.Parameters.Add("doc_guid", DbType.String);
                        var savesParam = cmd.Parameters.Add("num_saves", DbType.Int32);
                        currentQuery = cmd.CommandText;
                        cmd.Prepare();

                        foreach (var link in links)
                        {
                            instanceParam.Value = link.InstanceId;
                            typeParam.Value = link.TypeId;
                            nameParam.Value = link.Name;
                            pathParam.Value = link.Path;
                            statusParam.Value = link.Status;
                            nestedParam.Value = link.IsNested ? 1 : 0;
                            // Null stays null: absence of a fingerprint means the link was
                            // unloaded and its content could not be read, not that it matched.
                            guidParam.Value = String.IsNullOrEmpty(link.DocumentGuid)
                                ? (object)DBNull.Value : link.DocumentGuid;
                            savesParam.Value = link.NumberOfSaves;

                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    log("Exception updating Links Table: " + ex.GetType().Name + ": " + ex.Message);
                    log("Current Query: " + currentQuery);
                    throw; // rethrow;
                }
            }

            log("Recorded " + links.Count + " link(s) in the snapshot.");
        }

        private Level lookupLevel(Element e, XYZ pt)
        {
            // given the Element, figure out the level if possible.
            if (e.LevelId != ElementId.InvalidElementId) return e.Document.GetElement(e.LevelId) as Level;
            // otherwise, let's see if we can get it from the location point.

            if (pt == null) return null; // we don't know.

            if (_allLevels == null)
            {
                FilteredElementCollector coll = new FilteredElementCollector(_doc);
                coll.OfClass(typeof(Level));

                _allLevels = coll.Cast<Level>().ToList();
            }

            // we want the next level down from the z value...
            Level lev = Utilities.RevitUtils.GetNextLevelDown(pt, _allLevels);

            return lev;
        }

        // escapeQuote is gone: every value is bound as a parameter now, so quote doubling
        // is no longer anyone's job.

#endregion
    }
}
