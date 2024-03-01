using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Adminbot.Domain
{
    public static class RouletteWheel
    {
        private static Random random = new Random();

        public static T Spin<T>(List<WeightedItem<T>> weightedItems)
        {
            long totalWeight = weightedItems.Sum(item => item.Weight);
            double randomValue = random.NextDouble() * totalWeight;

            long currentWeight = 0;
            foreach (var weightedItem in weightedItems)
            {
                currentWeight += weightedItem.Weight;
                if (randomValue <= currentWeight)
                {
                    return weightedItem.Item;
                }
            }

            // This should not happen, but if it does, return the last item.
            return weightedItems.Last().Item;
        }
    }
}