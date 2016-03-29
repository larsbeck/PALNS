using System.CodeDom.Compiler;
using System.Diagnostics.CodeAnalysis;

[module: SuppressMessage("StyleCop.CSharp.DocumentationRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.NamingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.OrderingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "*")]
[module: GeneratedCode("ReSharper API", "0")]

namespace Palns
{
    internal enum WeightSelection
    {
        Rejected = 0,
        Accepted = 1,
        BetterThanCurrent = 2,
        NewGlobalBest = 3
    }
}