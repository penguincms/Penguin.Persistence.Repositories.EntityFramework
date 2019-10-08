using Penguin.Persistence.Abstractions;
using Penguin.Persistence.Repositories.EntityFramework.NetStandard.Objects;

namespace Penguin.Persistence.Repositories.EntityFramework
{
    /// <summary>
    /// A single use disposable EF context. Not thread safe.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "<Pending>")]
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