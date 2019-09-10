﻿using System;
using System.Collections.ObjectModel;
using System.Data.Entity;

namespace Penguin.Persistence.Repositories.EntityFramework.Objects
{
    /// <summary>
    /// A container that generates a new dynamic context each time the previous one is closed without disposing of the originals
    /// To maintain lazyloading
    /// </summary>
    public class ExtendedUseDbContextWrapper : BaseContextWrapper
    {
        /// <summary>
        /// Contains the "Disposed" contexts
        /// </summary>
        protected Collection<DbContext> GraveYard { get; set; }

        /// <summary>
        /// Accesses the current DbContext or creates a new one
        /// </summary>
        protected override DbContext DbContext
        {
            get
            {
                if (CurrentContext is null)
                {
                    CurrentContext = ServiceProvider.GetService(typeof(DbContext)) as DbContext;
                }

                return CurrentContext;
            }
        }

        private IServiceProvider ServiceProvider { get; set; }
        private DbContext CurrentContext { get; set; }

        /// <summary>
        /// Always returns false since there is always a context
        /// </summary>
        public override bool IsDisposed => false;

        /// <summary>
        /// Populates the internal service provider so the Extended Use context knows where to request new contexts from
        /// </summary>
        /// <param name="serviceProvider">The connection info to use for the generated contexts</param>
        public ExtendedUseDbContextWrapper(IServiceProvider serviceProvider)
        {
            GraveYard = new Collection<DbContext>();
            ServiceProvider = serviceProvider;
        }

        /// <summary>
        /// Pushes the last context to the graveyard and sets the reference to null so a new one can be instantiated if needed
        /// </summary>
        public override void Dispose()
        {
            GraveYard.Add(CurrentContext);
            CurrentContext = null;
        }
    }
}