using System;

namespace PenguinExtention.Models
{
    /// <summary>
    /// Tracks the indexing state of a single source file.
    /// Used by the incremental indexer to skip files whose content hash has not changed.
    /// </summary>
    public class IndexedFile
    {
        /// <summary>Absolute path to the file.</summary>
        public string FilePath { get; set; }

        /// <summary>SHA-256 hex digest of the file's content at the time it was last indexed.</summary>
        public string ContentHash { get; set; }

        /// <summary>UTC timestamp of the last successful indexing pass for this file.</summary>
        public DateTime LastIndexed { get; set; }

        /// <summary>True if the file lives under the Engine source tree.</summary>
        public bool IsEngineFile { get; set; }
    }
}
