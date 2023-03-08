using System;
using System.IO;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Extensions;

namespace Oxide.Ext.Rustigate
{
    /// <summary>
    /// The extension class that represents this extension
    /// </summary>
    public class RustigateExtension : Extension
    {
        /// <summary>
        /// Gets the name of this extension
        /// </summary>
        public override string Name => "Rustigate";

        /// <summary>
        /// Gets the author of this extension
        /// </summary>
        public override string Author => "https://github.com/spacebares/Rustigate";

        /// <summary>
        /// Gets the version of this extension
        /// </summary>
        public override VersionNumber Version => new VersionNumber(0, 0, 1);

        public static RustigateDemoExt RustigateDemoManager;

        /// <summary>
        /// Initializes a new instance of the MySqlExtension class
        /// </summary>
        public RustigateExtension(ExtensionManager manager) : base(manager)
        {

        }

        /// <summary>
        /// Loads this extension
        /// </summary>
        public override void Load()
        {

        }

        /// <summary>
        /// Loads plugin watchers used by this extension
        /// </summary>
        /// <param name="pluginDirectory"></param>
        public override void LoadPluginWatchers(string pluginDirectory)
        {

        }

        /// <summary>
        /// Called when all other extensions have been loaded
        /// </summary>
        public override void OnModLoad()
        {
            RustigateDemoManager = new RustigateDemoExt();
        }

        /// <summary>
        /// Called when server is shutdown
        /// </summary>
        public override void OnShutdown()
        {
            
        }
    }
}
