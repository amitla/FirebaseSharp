﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FirebaseSharp.Portable.Interfaces;
using FirebaseSharp.Portable.Subscriptions;

namespace FirebaseSharp.Portable.Filters
{
    class LimitToLastFilter : ISubscriptionFilter
    {
        private readonly int _limit;

        public LimitToLastFilter(int limit)
        {
            _limit = limit;
        }

        public IEnumerable<IDataSnapshot> Filter(IEnumerable<IDataSnapshot> snapshots)
        {
            throw new NotImplementedException();
        }
    }
}