﻿using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Adapters.DroneDB.Models;

namespace Registry.Adapters.DroneDB
{
    public class MetaManager
    {
        private readonly DDB _ddb;
        
        public MetaManager(DDB ddb)
        {
            _ddb = ddb;
        }

        public Meta Add(string key, string data, string path = null)
        {

            var m = DDBWrapper.MetaAdd(_ddb.DatasetFolderPath, key, data, path);

            return new Meta
            {
                Data = JToken.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public Meta Set(string key, string data, string path = null)
        {
            var m = DDBWrapper.MetaSet(_ddb.DatasetFolderPath, key, data, path);

            return new Meta
            {
                Data = JToken.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public int Remove(string id)
        {
            return DDBWrapper.MetaRemove(_ddb.DatasetFolderPath, id);
        }

        public string Get(string key, string path = null)
        {

            var m = DDBWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);

            return m;
        }

        public int Unset(string key, string path = null)
        {
            return DDBWrapper.MetaUnset(_ddb.DatasetFolderPath, key, path);
        }

        public MetaListItem[] List(string path = null)
        {
            return DDBWrapper.MetaList(_ddb.DatasetFolderPath, path).ToArray();
        }
    }
}