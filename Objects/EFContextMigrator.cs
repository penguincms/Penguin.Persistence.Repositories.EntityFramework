using Penguin.DependencyInjection.Abstractions.Attributes;
using Penguin.DependencyInjection.Abstractions.Enums;
using Penguin.Persistence.Abstractions;
using Penguin.Persistence.Abstractions.Interfaces;
using Penguin.Reflection;
using System;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Penguin.Persistence.Repositories.EntityFramework.Objects
{
    /// <summary>
    /// A class designed to initialize and update the database schema
    /// </summary>
    [Register(ServiceLifetime.Transient, typeof(IPersistenceContextMigrator))]
    public class EFContextMigrator : IPersistenceContextMigrator
    {
        PersistenceConnectionInfo PersistenceConnectionInfo { get; set; }

        /// <summary>
        /// Constructs a new instance of the migrator using the provided connection string
        /// </summary>
        /// <param name="connectionInfo"></param>
        public EFContextMigrator(PersistenceConnectionInfo connectionInfo)
        {
            Contract.Requires(connectionInfo != null);

            PersistenceConnectionInfo = connectionInfo;
        }

        /// <summary>
        /// Updates the database to reflect the current EntityFramework Schema
        /// </summary>
        public void Migrate()
        {
            

            foreach (Type t in TypeFactory.GetDerivedTypes(typeof(DbMigrationsConfiguration)).Where(t => !t.ContainsGenericParameters))
            {
                //Allow for overridden migration configs via inheritence
                Type derived = TypeFactory.GetMostDerivedType(t);

                if (derived is null || derived == t)
                {
                    DbMigrationsConfiguration configuration = Activator.CreateInstance(t) as DbMigrationsConfiguration;

                    if (configuration.AutomaticMigrationsEnabled)
                    {
                        configuration.TargetDatabase = new DbConnectionInfo(PersistenceConnectionInfo.ConnectionString, PersistenceConnectionInfo.ProviderName);

                        DbMigrator migrator = new DbMigrator(configuration);

                        migrator.Update();
                    }
                }
            }
        }
    }
}
