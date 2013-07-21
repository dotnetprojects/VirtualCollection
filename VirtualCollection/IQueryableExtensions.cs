using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VirtualCollectionWPF
{
    public static class IQueryableExtensions
    {
        public static List<object> ToList(this IQueryable query, int size = 50)
        {
            var lst = new List<object>(size);
            foreach (var v in query)
            {
                lst.Add(v);
            }

            return lst;
        }
    }
}
