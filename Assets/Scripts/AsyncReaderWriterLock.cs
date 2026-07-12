using System;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class AsyncReaderWriterLock
    {
        private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
        private readonly SemaphoreSlim _readSemaphore = new(1, 1);
        private int _readers;

        public async Task EnterReadAsync(CancellationToken token = default)
        {
            await _readSemaphore.WaitAsync(token);
            if (++_readers == 1)
                await _writeSemaphore.WaitAsync(token);
            _readSemaphore.Release();
        }

        public void ExitRead()
        {
            _readSemaphore.Wait();
            if (--_readers == 0)
                _writeSemaphore.Release();
            _readSemaphore.Release();
        }

        public async Task EnterWriteAsync(CancellationToken token = default)
        {
            await _writeSemaphore.WaitAsync(token);
        }

        public void ExitWrite()
        {
            _writeSemaphore.Release();
        }
    }
}