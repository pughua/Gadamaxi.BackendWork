using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Works.States
{
    public class EmptyWorkState : BaseWorkState
    {
        public const string Name = "empty";
        public EmptyWorkState(string name,
            string info,
            DateTime createTime,
            Dictionary<string, string>? data
            ) : base(name, string.Empty, false)
        {
            CreateUtcTime = createTime;
            if (data != null)
            {
                _data = data;
            }
        }

        public override IReadOnlyDictionary<string, string> GetData()
        {
            return _data;
        }
    }
}
