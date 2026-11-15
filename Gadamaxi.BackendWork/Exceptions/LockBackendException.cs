using System;
using System.Collections.Generic;
using System.Text;

namespace Gadamaxi.BackendWork.Exceptions
{
    public class LockBackendException : BackendException
    {
        public LockBackendException(string message) : base(message)
        {
        }

        public LockBackendException(string message, Exception ex) : base(message,ex)
        {
        }
    }
}
