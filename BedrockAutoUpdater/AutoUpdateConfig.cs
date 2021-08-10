using System;
using System.Collections.Generic;
using System.Text;

namespace BedrockUpdater
{
    class AutoUpdateConfig
    {
        public double UpdateCheckInterval { get; set; }

        public string InstallationMode { get; set; }

        public string InstallationTime { get; set; }

        public string[] IgnoreFiles { get; set; }
    }
}
