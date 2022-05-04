using Penguin.Persistence.Repositories.EntityFramework.Interfaces;
using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Threading.Tasks;

namespace Penguin.Persistence.Repositories.EntityFramework.Objects
{
    /// <summary>
    /// Abstract context wrapper class that forwards needed properties to the internal DB context
    /// </summary>
    public abstract class BaseContextWrapper : IDbContext
    {
        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbChangeTracker ChangeTracker => this.DbContext.ChangeTracker;

        /// <summary>
        /// Checks to see if the instance is still valid
        /// </summary>
        public abstract bool IsDisposed { get; }

        /// <summary>
        /// The current DbContext instance being referenced
        /// </summary>
        protected abstract DbContext DbContext { get; }

        /// <summary>
        /// Preps the DbContext for a new write context
        /// </summary>
        public abstract void BeginWrite(bool NewWrite);

        /// <summary>
        /// Handles the disposal logic for the current instance
        /// </summary>
        public abstract void Dispose();

        /// <summary>
        /// Retrieves an entity entry from the underlying context
        /// </summary>
        /// <param name="entity">The entity to get the entry for</param>
        /// <returns>The entity entry for the object</returns>
        public DbEntityEntry Entry(object entity)
        {
            return this.DbContext.Entry(entity);
        }

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public virtual void SaveChanges()
        {
            _ = this.DbContext.SaveChanges();
        }

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public virtual Task SaveChangesAsync()
        {
            return this.DbContext.SaveChangesAsync();
        }

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbSet<T> Set<T>() where T : class
        {
            return this.DbContext.Set<T>();
        }

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbSet Set(Type toCheck)
        {
            return this.DbContext.Set(toCheck);
        }
    }
}