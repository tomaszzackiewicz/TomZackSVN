using System;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class AsyncReaderWriterLock : IDisposable
    {
        private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
        private readonly SemaphoreSlim _readSemaphore = new(1, 1);
        private int _readers;
        private bool _disposed;

        public async Task EnterReadAsync(CancellationToken token = default)
        {
            ThrowIfDisposed();
            await _readSemaphore.WaitAsync(token).ConfigureAwait(false);

            try
            {
                if (++_readers == 1)
                    await _writeSemaphore.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                --_readers;
                _readSemaphore.Release();
                throw;
            }

            _readSemaphore.Release();
        }

        public void ExitRead()
        {
            ThrowIfDisposed();
            _readSemaphore.Wait();

            try
            {
                if (--_readers == 0)
                    _writeSemaphore.Release();
            }
            finally
            {
                _readSemaphore.Release();
            }
        }

        public async Task ExitReadAsync(CancellationToken token = default)
        {
            ThrowIfDisposed();
            await _readSemaphore.WaitAsync(token).ConfigureAwait(false);

            try
            {
                if (--_readers == 0)
                    _writeSemaphore.Release();
            }
            finally
            {
                _readSemaphore.Release();
            }
        }

        public async Task EnterWriteAsync(CancellationToken token = default)
        {
            ThrowIfDisposed();
            await _writeSemaphore.WaitAsync(token).ConfigureAwait(false);
        }

        public void ExitWrite()
        {
            ThrowIfDisposed();
            _writeSemaphore.Release();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _readSemaphore?.Dispose();
            _writeSemaphore?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncReaderWriterLock));
        }
    }
}