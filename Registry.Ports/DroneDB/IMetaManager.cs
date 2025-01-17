﻿using Registry.Ports.DroneDB.Models;

namespace Registry.Ports.DroneDB
{
    public interface IMetaManager
    {
        Meta Add(string key, string data, string path = null);
        Meta Set(string key, string data, string path = null);
        int Remove(string id);
        string Get(string key, string path = null);
        int Unset(string key, string path = null);
        MetaListItem[] List(string path = null);
        MetaDump[] Dump(string ids = null);

    }
}