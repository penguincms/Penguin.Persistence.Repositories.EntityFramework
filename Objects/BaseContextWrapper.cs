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
        /// The current DbContext instance being referenced
        /// </summary>
        protected abstract DbContext DbContext { get; }

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbChangeTracker ChangeTracker => DbContext.ChangeTracker;

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbSet<T> Set<T>() where T : class => DbContext.Set<T>();

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public DbSet Set(Type toCheck) => DbContext.Set(toCheck);

        /// <summary>
        /// Handles the disposal logic for the current instance
        /// </summary>
        public abstract void Dispose();

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public Task SaveChangesAsync() => DbContext.SaveChangesAsync();

        /// <summary>
        /// Forwarded to current instance
        /// </summary>
        public void SaveChanges() => DbContext.SaveChanges();

        /// <summary>
        /// Checks to see if the instance is still valid
        /// </summary>
        public abstract bool IsDisposed { get; }
    }
}