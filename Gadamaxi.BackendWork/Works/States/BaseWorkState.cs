using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Gadamaxi.BackendWork.Works.States
{
    public abstract class BaseWorkState : IWorkState
    {
        public BaseWorkState(string name,string info,bool finish)
        {
            StateName = name;
            Info = info;
            Finish = finish;
        }

        public BaseWorkState(IWorkState state, bool finish) :this(state.StateName,state.Info, finish) 
        {
            foreach(var kv in state.GetData())
            {
                Add(kv.Key, kv.Value);
            }

            SetOutData2Property();
        }

        public string StateName { get; private set; }

        public string Info { get; private set; }

        public DateTime CreateUtcTime { get; protected set; } = Helper.CurrentUtcTime();

        public bool Finish { get; protected set; }

        protected Dictionary<string,string> _data = new Dictionary<string,string>();

        public void Add(string key, string value)
        {
            Helper.CheckArgumentNull(key, nameof(key));
            _data.TryAdd(key, value);
        }

        public abstract IReadOnlyDictionary<string, string> GetData();

        private void SetOutData2Property()
        {
            foreach (var kv in _data)
            {
                Helper.SetPropertyValue(this, kv.Key, kv.Value);
            }
        }

        public string? GetData(string key)
        {
            string? result = null;

            _data.TryGetValue(key, out result);

            return result;
        }
    }
}
