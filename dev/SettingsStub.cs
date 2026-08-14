// Dev-harness stub for Metamorphosis.Utilities.Settings.
//
// The real Settings class (Utilities/Settingcs.cs) locates Settings.xml via
// Assembly.GetExecutingAssembly().Location. An assembly loaded from a byte array -
// which is how the runtime-compile loop avoids Revit locking the DLL - has an EMPTY
// Location, so that lookup resolves to a bare relative path, XmlDocument.Load throws
// FileNotFoundException, and the throw propagates straight out of the ComparisonMaker
// constructor. Compiling this stub INSTEAD of Settingcs.cs sidesteps that entirely.
//
// The diff engine touches Settings exactly once - ComparisonMaker.cs:64, reading the
// version-GUID option - so this is the whole surface that needs standing in.
//
// Returning false is also the right default for a test harness: it takes the full
// element-by-element comparison path rather than the DocumentVersion GUID shortcut,
// which is precisely the code being exercised. Tolerances are not read from here at
// all; the harness sets MoveTolerance / RotateTolerance on the instance directly.

namespace Metamorphosis.Utilities
{
    internal static class Settings
    {
        internal enum LogLevel { Basic, Verbose };

        internal static bool GetVersionGuidOption()
        {
            return false;
        }

        /// <summary>Basic keeps the journal readable; Verbose logs every geometry INSERT.</summary>
        internal static LogLevel GetLogLevel()
        {
            return LogLevel.Basic;
        }
    }
}
