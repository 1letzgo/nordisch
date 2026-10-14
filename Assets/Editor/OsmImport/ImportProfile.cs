using System.Diagnostics;
using System.Text;
using Debug = UnityEngine.Debug;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Phase timings of an import ("[Import] phase …" lines in the log): wall time since the previous mark, managed heap
    /// and the process' resident memory. The summary at the end lists the slowest phases.
    /// </summary>
    public static class ImportProfile
    {
        static Stopwatch total, phase;
        static readonly StringBuilder summary = new StringBuilder();
        static string area;

        public static void Begin(string areaId)
        {
            area = areaId;
            total = Stopwatch.StartNew();
            phase = Stopwatch.StartNew();
            summary.Clear();
        }

        /// <summary>Ends the current phase under <paramref name="name"/> and starts the next one.</summary>
        public static void Mark(string name)
        {
            if (phase == null) return;
            long ms = phase.ElapsedMilliseconds;
            long managed = System.GC.GetTotalMemory(false) >> 20;
            long rss = Process.GetCurrentProcess().WorkingSet64 >> 20;
            Debug.Log($"[Import] {area} phase {name}: {ms} ms (managed {managed} MB, rss {rss} MB)");
            summary.Append($"{name} {ms / 1000f:F1}s, ");
            phase.Restart();
        }

        public static void End()
        {
            if (total == null) return;
            Debug.Log($"[Import] {area} total {total.ElapsedMilliseconds / 1000f:F1} s: {summary}");
            total = phase = null;
        }
    }
}
