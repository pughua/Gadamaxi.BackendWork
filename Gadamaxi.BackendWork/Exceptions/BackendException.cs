using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Exceptions
{
    public class BackendException:Exception
    {
        public BackendException(string message) : base(message)
        {
        }

        public BackendException(string message,Exception exception) : base(message,exception)
        {
        }
    }
}
