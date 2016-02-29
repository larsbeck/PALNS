namespace Palns
{
    /// <summary>
    /// Classes implementing this interface must provide a clone method
    /// </summary>
    /// <typeparam name="T">The type of the implementing class</typeparam>
    public interface IPalnsClonable<out T>
    {
        T Clone();
    }
}