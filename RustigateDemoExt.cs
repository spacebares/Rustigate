using Oxide.Core;
using System;
using System.IO;

namespace Oxide.Ext.Rustigate
{
    public class RustigateDemoExt
    {
        //demofileLocation looks like: demos/playerSteamID64/demofilename.dem
        private string RootDir = Interface.Oxide.RootDirectory + "/";
        public RustigateDemoExt()
        {
            
        }

        public bool IsDemoOnDisk(string demofileLocation)
        {
            return File.Exists(RootDir + demofileLocation);
        }

        public void DeleteDemoFromDisk(string demofileLocation)
        {
            if (IsDemoOnDisk(demofileLocation))
            {
                File.Delete(RootDir + demofileLocation);
            }
        }

        public long GetDemoSize(string demofileLocation)
        {
            FileInfo DemoFileInfo = new FileInfo(RootDir + demofileLocation);
            return DemoFileInfo.Length;
        }
    }
}
