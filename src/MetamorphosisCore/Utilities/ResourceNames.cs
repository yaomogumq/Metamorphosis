using System;
using System.Reflection;

namespace Metamorphosis.Utilities
{
    /// <summary>
    /// One source of truth for embedded resource ids.
    ///
    /// These used to be derived two different ways: SnapshotMaker built them from the
    /// running assembly's own name, while DataUtility hardcoded the "Metamorphosis."
    /// prefix. Those agree only while the assembly keeps that name, and nothing checked -
    /// renaming the output produced a null script list and a bare NullReferenceException
    /// from createDatabase, with nothing to suggest resources were the cause.
    /// </summary>
    internal static class ResourceNames
    {
        private static readonly string _prefix =
            typeof(ResourceNames).Assembly.GetName().Name + ".";

        /// <summary>
        /// Qualify a resource path relative to the project root, e.g.
        /// <c>"DBScript.UpgradeToV1.txt"</c> or <c>"databaseFormat.txt"</c>.
        /// </summary>
        internal static string Qualify(string relativeName)
        {
            if (String.IsNullOrEmpty(relativeName)) return relativeName;

            return _prefix + relativeName;
        }
    }
}
