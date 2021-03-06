﻿using Penguin.Persistence.Abstractions;

namespace Penguin.Persistence.Repositories.EntityFramework
{
    /// <summary>
    /// A single use disposable EF context. Not thread safe.
    /// </summary>
    public class DisposableContext<T> : EFPersistenceContext<T> where T : KeyedObject
    {
        /// <summary>
        /// Creates a new instance of this context
        /// </summary>
        /// <param name="connectionString">The connection string to use during construction</param>
        public DisposableContext(string connectionString) : base(new SingleUseDbContext(new PersistenceConnectionInfo(connectionString)))
        {
        }
    }
}