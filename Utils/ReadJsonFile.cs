using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Utils
{
    public class ReadJsonFile
    {
        public static string ReadJsonAsString(string path = "./Data/servers.json")
        {
            return System.IO.File.ReadAllText(path);
        }
    }
}