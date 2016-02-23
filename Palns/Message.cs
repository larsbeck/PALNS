using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

[module: SuppressMessage("StyleCop.CSharp.DocumentationRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.NamingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.OrderingRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.ReadabilityRules", "*")]
[module: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "*")]

namespace Palns
{
    public class Message<TSolution>
    {
        public int OperatorIndex { get; set; }
        public Func<TSolution, Task<TSolution>> DestroyOperator { get; set; }
        public Func<TSolution, Task<TSolution>> RepairOperator { get; set; }
        public WeightSelection WeightSelection { get; set; }
        public double Temperature { get; set; }
        public TSolution Solution { get; set; }
    }
}