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
        /// === FIX (editor domain reload): reset stanu po przerwanym Play mode.
        ///
        /// Gdy w edytorze z wyłączonym Domain Reload zatrzymasz Play w trakcie
        /// operacji (np. w trakcie refreshu), semafory zostają zajęte. Static
        /// lock żyje dalej → następna sesja Play zaczyna z zablokowanym lockiem
        /// → pierwszy EnterWriteAsync deadlockuje.
        ///
        /// Reset: oddaj wszystkie zajęte sloty (Wait(0) w pętli + Release),
        /// wyzeruj licznik czytelników, wyczyść flagę disposed.
        ///
        /// Wywołać z SvnEditorLifecycle.OnPlayModeStateChanged (razem z
        /// SvnRunner.ResetStaticState).
        /// </summary>
        public void ResetForEditorReload()
        {
            // Drain write semaphore slots
            while (_writeSemaphore.Wait(0))
                _writeSemaphore.Release();

            // Drain read semaphore slots
            while (_readSemaphore.Wait(0))
                _readSemaphore.Release();

            _readers = 0;
            _disposed = false;
        }

        /// <summary>
        /// NIE disposujemy semaforów — statyczne instancje muszą przetrwać
        /// kolejne sesje Play (edytor z wył. Domain Reload). SemaphoreSlim
        /// nie ma zasobów natywnych do zwolnienia w tej konfiguracji.
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