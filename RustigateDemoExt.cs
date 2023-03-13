using System.IO;

namespace Oxide.Ext.Rustigate
{
    public class RustigateDemoExt
    {
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
            return File.Exists(DemofileLocation);
        }

        //returns the amount of bytes deleted
        public long DeleteDemoFromDisk(string DemofileLocation)
        {
            if (IsDemoOnDisk(DemofileLocation))
            {
                long DemoSize = GetDemoSize(DemofileLocation);
                DemoFolderSize -= DemoSize;
                File.Delete(DemofileLocation);
                return DemoSize;
            }

            return 0;
        }

        public long GetDemoSize(string DemofileLocation)
        {
            FileInfo DemoFileInfo = new FileInfo(DemofileLocation);
            return DemoFileInfo.Length;
        }
    }
}
