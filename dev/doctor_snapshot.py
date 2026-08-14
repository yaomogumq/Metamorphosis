# encoding: utf-8
"""
Plant a known COMPOUND change in a snapshot, to test the comparison engine without
touching the Revit model at all.

The obvious way to test "does an element that both moved and changed a parameter get
reported correctly" is to edit the model. That means modifying a document someone has
open, and unwinding it afterwards. Unnecessary: a snapshot is plain SQLite, so it is
easier and completely safe to make the RECORDED PAST differ from the live model in two
ways at once. The comparison then has to report both.

Usage
  1. Take a baseline with the installed add-in:
         Metamorphosis.Snapshot.Export(doc, r"C:\\Temp\\metamorphosis-dev\\baseline.sdb")
  2. Run this. It shifts one element's location by a foot AND repoints one of its
     parameters to a new value, then prints the element id.
  3. Run roslyn_reload.py. That element must come back as ParameterChange + Move in a
     single record. Before the item-1 fix it came back as ParameterChange alone.

The snapshot is left DOCTORED. Delete and re-export before using it as a real baseline.

TWO TRAPS WORTH KNOWING
  - The value pool is deduplicated - about 16k distinct values behind 400k EAV rows on
    a real model - so editing a row in _objects_val in place would change every element
    sharing that value. Always INSERT a new value and repoint the one EAV row.
  - Text columns come back as byte[], not TEXT. Decode with UTF8 before use.
"""

import System
import clr
clr.AddReference("System.Data.SQLite")
from System.Data.SQLite import SQLiteConnection
from System.Text import Encoding

SNAPSHOT = r"C:\Temp\metamorphosis-dev\baseline.sdb"
CATEGORY = "Structural Framing"     # any category whose instances have real locations
SHIFT_FEET = 1.0                    # well above the default 0.0006 ft move tolerance
SENTINEL = "DOCTORED-BASELINE"


def asc(x):
    """pyRevit serialises MCP responses as ASCII JSON; one stray degree sign or CJK
    character throws after the work is already done. Sanitise everything printed."""
    if x is None:
        return "None"
    return "".join([c if 32 <= ord(c) < 127 else "?" for c in str(x)])


def cell(v):
    if v is None or isinstance(v, System.DBNull):
        return None
    if isinstance(v, System.Array[System.Byte]):
        return Encoding.UTF8.GetString(v)
    return str(v)


conn = SQLiteConnection("Data Source=%s;Version=3;" % SNAPSHOT)
conn.Open()


def q(sql, n=40):
    cmd = conn.CreateCommand()
    cmd.CommandText = sql
    r = cmd.ExecuteReader()
    rows = []
    while r.Read() and len(rows) < n:
        rows.append([cell(r.GetValue(i)) for i in range(r.FieldCount)])
    r.Close()
    return rows


def ex(sql):
    cmd = conn.CreateCommand()
    cmd.CommandText = sql
    return cmd.ExecuteNonQuery()


rows = q("""SELECT g.id, g.Location FROM _objects_geom g
            JOIN _objects_id i ON g.id = i.id
            WHERE i.category = '%s' AND i.isType = 0
              AND g.Location IS NOT NULL AND g.Location <> '' AND g.Location <> '0,0,0'
            ORDER BY g.id LIMIT 1""" % CATEGORY)

if not rows:
    print("No suitable element in category '%s'." % asc(CATEGORY))
else:
    eid, loc = rows[0][0], rows[0][1]
    print("target element : %s  (%s)" % (asc(eid), asc(CATEGORY)))
    print("location before: %s" % asc(loc))

    # --- change 1: move it ---------------------------------------------------------
    parts = loc.split(",")
    newloc = "%s,%s,%s" % (repr(float(parts[0]) + SHIFT_FEET), parts[1], parts[2])
    ex("UPDATE _objects_geom SET Location='%s' WHERE id=%s" % (newloc, eid))
    print("location after : %s" % asc(newloc))

    # Bounding box deliberately untouched: a box that merely shifts is suppressed as a
    # restatement of the move, so this isolates Move without dragging in GeometryChange.

    # --- change 2: alter one parameter ---------------------------------------------
    params = q("""SELECT e.attribute_id, a.name, v.value
                  FROM _objects_eav e
                  JOIN _objects_attr a ON a.id = e.attribute_id
                  JOIN _objects_val v ON v.id = e.value_id
                  WHERE e.entity_id = %s AND a.name NOT IN ('Edited by')
                  ORDER BY a.name""" % eid)
    target = None
    for p in params:
        if p[1] in ("Comments", "Mark"):
            target = p
            break
    if target is None and params:
        target = params[0]

    if target is None:
        print("element has no usable parameter - pick another CATEGORY")
    else:
        newvid = int(q("SELECT MAX(id) FROM _objects_val")[0][0]) + 1
        ex("INSERT INTO _objects_val (id,value) VALUES (%d,'%s')" % (newvid, SENTINEL))
        ex("UPDATE _objects_eav SET value_id=%d WHERE entity_id=%s AND attribute_id=%s"
           % (newvid, eid, target[0]))
        print("parameter      : '%s'  '%s' -> '%s'" % (asc(target[1]), asc(target[2]), SENTINEL))

        print("")
        print("Expected from the fixed engine : ParameterChange + Move, in ONE record")
        print("Expected from the old engine   : ParameterChange only, move silently lost")
        print("TARGET_ID=%s" % asc(eid))

conn.Close()
