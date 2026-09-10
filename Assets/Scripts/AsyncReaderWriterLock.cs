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
                _readers++;

                try
                {
                    if (_readers == 1)
                        await _writeSemaphore.WaitAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    _readers--;
                    throw;
                }
            }
            finally
            {
                _readSemaphore.Release();
            }
        }

        /// <summary>
        /// Wersja synchroniczna — WYŁĄCZNIE dla kodu, który na 100% NIE jest na
        /// main thread Unity (np. wewnątrz Task.Run). Na main thread może
        /// zablokować się w nieskończoność, gdy writer czeka na main thread.
        /// </summary>
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

        /// <summary>
        /// === FIX (shutdown race): ŚWIADOMIE nie disposujemy semaforów.
        /// SemaphoreSlim.Dispose() zwalnia natywne zasoby tylko wtedy, gdy
        /// użyto AvailableWaitHandle — my nie używamy, więc nie ma czego
        /// zwalniać poza managed pamięcią (GC i tak to zrobi).
        /// Disposal w trakcie in-flight operacji (ExitWrite / ExitReadAsync
        /// po Dispose) rzucałby ObjectDisposedException w losowym miejscu
        /// aplikacji. Bezpieczniej zostawić semafory żywe do końca procesu.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncReaderWriterLock));
        }
    }
}