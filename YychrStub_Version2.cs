using System.IO;
using System.Collections;

namespace Yychr
{
    // Minimal interface stub to compile the plugin. Replace with actual SDK types when available.
    public interface IYychrPlugin
    {
        string Name { get; }
        void Read(PluginArgs args);
        void Write(PluginArgs args);
    }

    public class PluginArgs
    {
        public Stream SourceStream { get; set; }
        public Stream DestinationStream { get; set; }
        public IDictionary Parameters { get; set; }
    }
}