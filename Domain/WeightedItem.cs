using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{

    public class WeightedItem<T>
    {
        public T Item { get; }
        public long Weight { get; }

        public WeightedItem(T item, long weight)
        {
            Item = item;
            Weight = weight;
        }
    }
}