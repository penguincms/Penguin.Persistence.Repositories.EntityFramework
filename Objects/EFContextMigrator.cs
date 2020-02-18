using Penguin.DependencyInjection.Abstractions.Attributes;
using Penguin.DependencyInjection.Abstractions.Enums;
using Penguin.Extensions.Collections;
using Penguin.Extensions.Strings;
using Penguin.Persistence.Abstractions;
using Penguin.Persistence.Abstractions.Interfaces;
using Penguin.Reflection;
using System;
using System.Collections.Generic;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Data.SqlClient;
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
        public bool IsConfigured => !string.IsNullOrWhiteSpace(PersistenceConnectionInfo.ConnectionString);
        private PersistenceConnectionInfo PersistenceConnectionInfo { get; set; }

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
            try
            {
                IsServerConnected(PersistenceConnectionInfo.ConnectionString);
            }
            catch (Exception ex)
            {
                Dictionary<string, string> settings = PersistenceConnectionInfo.ConnectionString.ToDictionary();
                string dbName = settings["Initial Catalog"];
                settings["Initial Catalog"] = "master";

                string masterString = settings.ToFormattedString();

                using (SqlConnection connection = new SqlConnection(masterString))
                {
                    using (SqlCommand command = new SqlCommand($"CREATE DATABASE {dbName}", connection))
                    {
                        command.Connection.Open();
                        command.ExecuteNonQuery();
                    }
                }

                IsServerConnected(PersistenceConnectionInfo.ConnectionString);
            }

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

        /// <summary>
        /// Test that the server is connected
        /// </summary>
        /// <param name="connectionString">The connection string</param>
        /// <returns>true if the connection is opened</returns>
        private static void IsServerConnected(string connectionString)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
            }
        }
    }
}