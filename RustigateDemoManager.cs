using Oxide.Core;
using System.IO;

namespace Oxide.Ext.Rustigate
{
    public class RustigateDemoManager
    {
        public RustigateDemoManager()
        {
        }

        public bool IsDemoOnDisk(string demofileLocation)
        {
            //demofileLocation looks like: demos/playerSteamID64/demofilename.dem
            string RootDir = Interface.Oxide.RootDirectory + "/";
            return File.Exists(RootDir + demofileLocation);
        }
    }
}
