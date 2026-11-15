using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Recurrings
{
    public class RecurringWorkContext
    {
        public RecurringWorkContext() 
        {
            TimeZoneInfo = TimeZoneInfo.Utc;
        }

        public TimeZoneInfo TimeZoneInfo { get; set; }
    }
}
