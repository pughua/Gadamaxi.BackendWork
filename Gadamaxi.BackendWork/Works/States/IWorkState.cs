using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Works.States
{
    public interface IWorkState
    {
        string StateName { get; }

        string Info { get; }

        bool Finish { get; }

        DateTime CreateUtcTime { get;}

        IReadOnlyDictionary<string, string> GetData();

        void Add(string key, string value);

        string? GetData(string key);
    }
}
