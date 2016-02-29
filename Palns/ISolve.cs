namespace Palns
{
    /// <summary>
    /// A basic solver interface
    /// </summary>
    /// <typeparam name="TInput">The type of the problem input</typeparam>
    /// <typeparam name="TOutput">The type of the solution output</typeparam>
    internal interface ISolve<in TInput, out TOutput> where TOutput : ISolution<TOutput>
    {
        TOutput Solve(TInput input);
    }
}