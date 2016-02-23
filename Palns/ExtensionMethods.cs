using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

[module: SuppressMessage("StyleCop.CSharp.DocumentationRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.NamingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.OrderingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "*")]

namespace Palns
{
    public static class ExtensionMethods
    {
        public static IEnumerable<double> ToCumulativEnumerable(this IEnumerable<double> input)
        {
            if (input == null || !input.Any())
                yield break;
            var temp = 0.0;
            var sum = input.Sum();
            foreach (var value in input)
            {
                temp += value;
                yield return temp/sum;
            }
        }
    }
}