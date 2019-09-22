using Penguin.Persistence.Repositories.EntityFramework.Abstractions.Interfaces;
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
        public DbChangeTracker ChangeTracker => DbContext.ChangeTracker;

        /// <summary>
        /// Checks to see if the instance is still valid
        /// </summary>
        public abstract bool IsDisposed { get; }

        /// <summary>
        /// Handles the disposal logic for the current instance
        /// </summary>
        public abstract void Dispose();

        /// <summary>
        /// Retrieves an entity entry from the underlying context
        /// </summary>
        /// <param name="entity">The entity to get the entry for</param>
        /// <returns>The entity entry for the object</returns>
        public DbEntityEntry Entry(object entity) => DbContext.Entry(entity);

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public virtual void SaveChanges() => DbContext.SaveChanges();

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public virtual Task SaveChangesAsync() => DbContext.SaveChangesAsync();

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbSet<T> Set<T>() where T : class => DbContext.Set<T>();

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbSet Set(Type toCheck) => DbContext.Set(toCheck);


        /// <summary>
        /// Preps the DbContext for a new write context
        /// </summary>
        public abstract void BeginWrite();

        /// <summary>
        /// The current DbContext instance being referenced
        /// </summary>
        protected abstract DbContext DbContext { get; }
    }
}