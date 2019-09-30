using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceIndexer
{
    public struct SemaphoreLock : IDisposable
    {
        private readonly SemaphoreSlim sem;

        private SemaphoreLock(SemaphoreSlim sem)
        {
            this.sem = sem;
        }

        public void Dispose()
        {
            sem.Release();
        }

        public static ValueTask<SemaphoreLock> LockAsync(SemaphoreSlim sem)
        {
            var waitTask = sem.WaitAsync();
            if (waitTask.IsCompletedSuccessfully)
            {
                return new ValueTask<SemaphoreLock>(new SemaphoreLock(sem));
            }

            static async Task<SemaphoreLock> WaitForLock(Task waitTask, SemaphoreSlim sem)
            {
                await waitTask.ConfigureAwait(false);
                return new SemaphoreLock(sem);
            }

            return new ValueTask<SemaphoreLock>(WaitForLock(waitTask, sem));
        }
    }
}