using Penguin.Persistence.Abstractions.Interfaces;
using System.Threading.Tasks;

namespace Penguin.Persistence.Repositories.EntityFramework
{
    /// <summary>
    /// A WriteContext implementation intended for use with the EFPersistenceContext
    /// </summary>
    public class WriteContext : IWriteContext
    {
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
            this.Context = context;

            this.EnableWrite();
        }

        /// <summary>
        /// Cancels any open writes and detaches all entities
        /// </summary>
        public void CancelWrite()
        {
            this.Context.CancelWrite();
        }

        /// <summary>
        /// Disposes of this WriteContext and attempts to persist any changes
        /// </summary>
        public void Dispose()
        {
            disposed = true;

            if (this.Async)
            {
                this.Context.Commit(this);
            }
            else
            {
                this.Context.Commit(this);
            }

            this.DisableWrite();
        }

        private bool disposed = false;

        private Task<int> LastAsync { get; set; }

        private void DisableWrite() => this.Context.EndWrite(this);

        private void EnableWrite() => this.Context.BeginWrite(this);

        /// <summary>
        /// Disposes of this WriteContext and attempts to persist any changes
        /// </summary>
        ~WriteContext()
        {
            if (!disposed)
            {
                this.Context.EndWrite(this);
            }

            disposed = true;
        }
    }
}