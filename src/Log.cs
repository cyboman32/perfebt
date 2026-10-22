using System;
using System.IO;
using System.Text;

namespace PerfeBT
{
    internal static class Log
    {
        private static StreamWriter writer;

        internal static void Open(string path)
        {
            writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        }

        // Also called from the game's save threads.
        internal static void Write(string line)
        {
            var w = writer;
            if (w == null)
                return;
            var text = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
            lock (w)
                w.WriteLine(text);
        }
    }
}
