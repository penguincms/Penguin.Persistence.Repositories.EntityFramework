using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Threading.Tasks;

namespace Penguin.Persistence.Repositories.EntityFramework.Interfaces
{
    /// <summary>
    /// An interface ensuring standard access for any type attempting to fill the role of a DbContext
    /// </summary>
    public interface IDbContext
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        DbChangeTracker ChangeTracker { get; }

        void BeginWrite(bool NewWrite);

        DbSet<T> Set<T>() where T : class;

        DbSet Set(Type toCheck);

        void Dispose();

        Task SaveChangesAsync();

        void SaveChanges();

        bool IsDisposed { get; }

        DbEntityEntry Entry(object entity);

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }
}