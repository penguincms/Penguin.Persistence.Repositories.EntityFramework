using Penguin.DependencyInjection.Abstractions.Attributes;
using Penguin.DependencyInjection.Abstractions.Enums;
using Penguin.Persistence.Repositories.EntityFramework.Abstractions.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Penguin.Persistence.Repositories.EntityFramework.Objects
{
    /// <summary>
    /// A container that generates a new dynamic context each time the previous one is closed without disposing of the originals
    /// To maintain lazyloading
    /// </summary>
    [Register(ServiceLifetime.Scoped, typeof(IDbContext))]
    public class ExtendedUseDbContextWrapper : BaseContextWrapper
    {
        /// <summary>
        /// Generated at construction to track context resolution
        /// </summary>
        public Guid Guid { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Always returns false since there is always a context
        /// </summary>
        public override bool IsDisposed => false;

        /// <summary>
        /// Accesses the current DbContext or creates a new one
        /// </summary>
        protected override DbContext DbContext
        {
            get
            {
                if (this.CurrentContext is null)
                {
                    this.CurrentContext = this.ServiceProvider.GetService(typeof(DbContext)) as DbContext;
                }

                return this.CurrentContext;
            }
        }

        /// <summary>
        /// Contains the "Disposed" contexts
        /// </summary>
        protected Collection<DbContext> GraveYard { get; }

        private DbContext CurrentContext { get; set; }

        private IServiceProvider ServiceProvider { get; set; }

        /// <summary>
        /// Populates the internal service provider so the Extended Use context knows where to request new contexts from
        /// </summary>
        /// <param name="serviceProvider">The connection info to use for the generated contexts</param>
        public ExtendedUseDbContextWrapper(IServiceProvider serviceProvider)
        {
            this.GraveYard = new Collection<DbContext>();
            this.ServiceProvider = serviceProvider;
        }

        /// <summary>
        /// Moves the existing context to the graveyard so that changes cant be saved on it
        /// </summary>
        /// <param name="newWrite">True if the context has not already been opened</param>
        public override void BeginWrite(bool newWrite)
        {
            //Only dispose of any existing context if we're not already open
            if (newWrite)
            {
                this.Dispose();
            }
        }

        /// <summary>
        /// Pushes the last context to the graveyard and sets the reference to null so a new one can be instantiated if needed
        /// </summary>
        public override void Dispose()
        {
            this.GraveYard.Add(this.CurrentContext);
            this.CurrentContext = null;
        }

        /// <summary>
        /// Saves the changes on the underlying context
        /// </summary>
        public override void SaveChanges()
        {
            try
            {
                base.SaveChanges();
            }
            catch (Exception)
            {
                this.CurrentContext = null;
                throw;
            }
        }

        /// <summary>
        /// Saves the changes on the underlying context
        /// </summary>
        /// <returns>A task for the Async</returns>
        public override Task SaveChangesAsync()
        {
            try
            {
                return base.SaveChangesAsync();
            }
            catch (Exception)
            {
                this.CurrentContext = null;
                throw;
            }
        }
    }
}