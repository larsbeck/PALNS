namespace Palns
{
    /// <summary>
    /// Every solution type must implement this interface
    /// </summary>
    /// <typeparam name="T">The type of the solution</typeparam>
    public interface ISolution<out T> : IPalnsClonable<T>
    {
        double Objective { get; }
    }
}