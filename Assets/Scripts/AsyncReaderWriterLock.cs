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

            // === FIX K1: czekaj na readSemaphore PRZED try — wyjątek z WaitAsync
            // (np. cancel przed wejściem) NIE może trafiać do catch, który robi
            // rollback --_readers/Release: wcześniej cancel w tym punkcie
            // ZMNIEJSZAŁ cudzy licznik czytelników i wypuszczał cudzy slot
            // (trwała korupcja stanu przy pierwszym EnterReadAsync z canceled tokenem).
            await _readSemaphore.WaitAsync(token).ConfigureAwait(false);

            try
            {
                // Jesteśmy w sekcji — inkrementacja i ewentualne wzięcie write-locka.
                // UWAGA: pierwszy czytelnik TRZYMA readSemaphore przez czas oczekiwania
                // na writeSemaphore — kolejni czytelnicy czekają na readSemaphore.
                // To zamierzone: gwarantuje, że _readers nie zmieni się między
                // checkem a wzięciem write-locka.
                _readers++;

                try
                {
                    if (_readers == 1)
                        await _writeSemaphore.WaitAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    // Wyjątek w oczekiwaniu na write-lock — cofamy inkrementację.
                    _readers--;
                    throw;
                }
            }
            finally
            {
                _readSemaphore.Release();
            }
        }

        public void ExitRead()
        {
            ThrowIfDisposed();
            _readSemaphore.Wait();

            try
            {
                _readers--;
                if (_readers == 0)
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
                _readers--;
                if (_readers == 0)
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