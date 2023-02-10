using Penguin.Persistence.Abstractions.Interfaces;
using System;

namespace Penguin.Persistence.Repositories.EntityFramework
{
    /// <summary>
    /// A WriteContext implementation intended for use with the EFPersistenceContext
    /// </summary>
    public class WriteContext : IWriteContext
    {
        private bool disposedValue;

        /// <summary>
        /// A bool representing whether or not this WriteContext should attempt to commit changes asynchronously
        /// </summary>
        public bool Async { get; set; }

        /// <summary>
        /// The PersistenceContext that spawned this write context
        /// </summary>
        public IPersistenceContext Context { get; protected set; }

        /// <summary>
        /// Creates a new instance of this write context using the provided persistencecontext as a source
        /// </summary>
        /// <param name="context">The persistence context to use for these changes</param>
        public WriteContext(IPersistenceContext context)
        {
            Context = context;

            EnableWrite();
        }

        /// <summary>
        /// Cancels any open writes and detaches all entities
        /// </summary>
        public void CancelWrite()
        {
            Context.CancelWrite();
        }

        /// <summary>
        /// Disposes of this WriteContext and attempts to persist any changes
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes of this WriteContext and attempts to persist any changes
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (Async)
                {
                    Context.Commit(this);
                }
                else
                {
                    Context.Commit(this);
                }

                Context.EndWrite(this);

                disposedValue = true;
            }
        }

        private void EnableWrite()
        {
            Context.BeginWrite(this);
        }

        // To detect redundant calls
        /// <summary>
        /// Disposes of this WriteContext and attempts to persist any changes
        /// </summary>
        ~WriteContext()
        {
            Dispose(false);
        }
    }
}