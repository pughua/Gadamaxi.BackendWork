using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Batchs
{
    public enum BatchStatus
    {
        None = 0,
        Awaiting =1,
        Init = 2,
        Processing = 3,
        Failure = 4,
        Success = 5
    }
}
