using System;
using System.Threading;
using System.Threading.Tasks;

namespace Palns
{
    /// <summary>
    /// Taken from here: https://www.hanselman.com/blog/ComparingTwoTechniquesInNETAsynchronousCoordinationPrimitives.aspx
    /// </summary>
    public sealed class AsyncLock
    {
        private readonly SemaphoreSlim _mSemaphore = new SemaphoreSlim(1, 1);
        private readonly Task<IDisposable> _mReleaser;

        public AsyncLock()
        {
            _mReleaser = Task.FromResult((IDisposable)new Releaser(this));
        }

        public Task<IDisposable> LockAsync()
        {
            var wait = _mSemaphore.WaitAsync();
            return wait.IsCompleted ?
                _mReleaser :
                wait.ContinueWith((_, state) => (IDisposable)state,
                    _mReleaser.Result, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly AsyncLock _mToRelease;
            internal Releaser(AsyncLock toRelease) { _mToRelease = toRelease; }
            public void Dispose() { _mToRelease._mSemaphore.Release(); }
        }
    }
}