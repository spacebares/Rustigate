using Oxide.Core;
using System;
using System.IO;

namespace Oxide.Ext.Rustigate
{
    public class RustigateDemoExt
    {
        //demofileLocation looks like: demos/playerSteamID64/demofilename.dem
        private string RootDir = Interface.Oxide.RootDirectory + "/";

        //instead of getting the size of the folder each time a demo is added or deleted
        //we just incrementally subtract or add based on DeleteDemoFromDisk and NotifyNewDemoCreated calls
        public long DemoFolderSize;

        public RustigateDemoExt()
        {
            
        }

        public void NotifyNewDemoCreated(string DemofileLocation)
        {
            long DemoSize = GetDemoSize(DemofileLocation);
            DemoFolderSize += DemoSize;
        }

        public bool IsDemoOnDisk(string DemofileLocation)
        {
            return File.Exists(RootDir + DemofileLocation);
        }

        //returns the amount of bytes deleted
        public long DeleteDemoFromDisk(string DemofileLocation)
        {
            if (IsDemoOnDisk(DemofileLocation))
            {
                long DemoSize = GetDemoSize(DemofileLocation);
                DemoFolderSize -= DemoSize;
                File.Delete(RootDir + DemofileLocation);
                return DemoSize;
            }

            return 0;
        }

        public long GetDemoSize(string DemofileLocation)
        {
            FileInfo DemoFileInfo = new FileInfo(RootDir + DemofileLocation);
            return DemoFileInfo.Length;
        }
    }
}
