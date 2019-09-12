﻿using Penguin.Persistence.Abstractions;
using Penguin.Persistence.EntityFramework;
using Penguin.Persistence.Repositories.EntityFramework.Objects;
using System;
using System.Data.Entity;
using System.Reflection;

namespace Penguin.Persistence.Repositories.EntityFramework.NetStandard.Objects
{
    /// <summary>
    /// Wraps a DbContext so that it is disposed of when the WriteContext closes
    /// </summary>
    public class SingleUseDbContext : BaseContextWrapper
    {
        /// <summary>
        /// The backing DbContext
        /// </summary>
        protected override DbContext DbContext
        {
            get
            {
                return CurrentContext;
            }
        }

        private DbContext CurrentContext { get; set; }

        private bool PreventDispose { get; set; }

        /// <summary>
        /// Checks if the underlying context has been disposed
        /// </summary>
        public override bool IsDisposed
        {
            get
            {
                bool result = true;

                Type typeDbContext = typeof(DbContext);
                Type typeInternalContext = typeDbContext.Assembly.GetType("System.Data.Entity.Internal.InternalContext");

                FieldInfo fi_InternalContext = typeDbContext.GetField("_internalContext", BindingFlags.NonPublic | BindingFlags.Instance);
                PropertyInfo pi_IsDisposed = typeInternalContext.GetProperty("IsDisposed");

                object ic = fi_InternalContext.GetValue(this.DbContext);

                if (ic != null)
                {
                    result = (bool)pi_IsDisposed.GetValue(ic);
                }

                return result;
            }
        }

        /// <summary>
        /// Constructs a new instance of this wrapping class
        /// </summary>
        /// <param name="connectionInfo">The connection info to use with the DynamicContext</param>
        /// <param name="preventDispose">Prevents the underlying context from being dispose when Dispose() is called (in case there are lazy loaded entities)</param>
        public SingleUseDbContext(PersistenceConnectionInfo connectionInfo, bool preventDispose = false)
        {
            CurrentContext = new DynamicContext(connectionInfo);
            PreventDispose = preventDispose;
        }

        /// <summary>
        /// Constructs a new instance of this wrapping class
        /// </summary>
        /// <param name="connectionString">The connection string to use with the DynamicContext</param>
        /// <param name="preventDispose">Prevents the underlying context from being dispose when Dispose() is called (in case there are lazy loaded entities)</param>
        public SingleUseDbContext(string connectionString, bool preventDispose = false)
        {
            CurrentContext = new DynamicContext(new PersistenceConnectionInfo(connectionString));
            PreventDispose = preventDispose;
        }

        /// <summary>
        /// Requests that the underlying context be disposed
        /// </summary>
        public override void Dispose()
        {
            if (!PreventDispose)
            {
                DbContext.Dispose();
            }
        }
    }
}