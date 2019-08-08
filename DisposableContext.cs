using Penguin.Persistence.Abstractions;
using Penguin.Persistence.Abstractions.Models.Base;
using Penguin.Persistence.EntityFramework;

namespace Penguin.Persistence.Repositories.EntityFramework
{
    /// <summary>
    /// A single use disposable EF context. Not thread safe.
    /// </summary>
    public class DisposableContext<T> : EFPersistenceContext<T> where T : KeyedObject
    {
        #region Constructors

        /// <summary>
        /// Creates a new instance of this context
        /// </summary>
        /// <param name="connectionString">The connection string to use during construction</param>
        public DisposableContext(string connectionString) : base(new DynamicContext(new PersistenceConnectionInfo(connectionString)))
        {
        }

        #endregion Constructors
    }
}