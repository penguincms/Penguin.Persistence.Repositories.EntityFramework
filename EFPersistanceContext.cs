using Penguin.Debugging;
using Penguin.Entities;
using Penguin.Messaging.Core;
using Penguin.Persistence.Abstractions;
using Penguin.Persistence.Abstractions.Attributes.Control;
using Penguin.Persistence.Abstractions.Interfaces;
using Penguin.Persistence.Abstractions.Models.Base;
using Penguin.Persistence.Repositories.EntityFramework.Abstractions.Interfaces;
using Penguin.Reflection;
using Penguin.Reflection.Abstractions;
using Penguin.Reflection.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Penguin.Persistence.Repositories.EntityFramework
{
    /// <summary>
    /// A persistence context that uses Entity Framework as its backing for data
    /// </summary>
    /// <typeparam name="T">The type of the object contained in this context</typeparam>
    public class EFPersistenceContext<T> : PersistenceContext<T> where T : KeyedObject
    {
        /// <summary>
        /// The backing Entity Framework DbContext for this context
        /// </summary>
        public IDbContext DbContext { get; set; }

        /// <summary>
        /// If true, this context has access to a valid DbSet T  on the underlying provider
        /// </summary>
        public override bool IsValid
        {
            get
            {
                return IsValidType(this.DbContext, typeof(T));
            }
        }

        /// <summary>
        /// If true, this context has a valid open WriteContext
        /// </summary>
        public virtual bool WriteEnabled { get; set; }

        /// <summary>
        /// Creates a new instance of this persistence context
        /// </summary>
        /// <param name="dbContext">The underlying DbContext to use as the data source</param>
        /// <param name="messageBus">An optional message bus for publishing persistence events</param>
        public EFPersistenceContext(IDbContext dbContext, MessageBus messageBus = null) : base(typeof(T), IsValidType(dbContext, typeof(T)) ? (GenerateBaseQuery(dbContext.Set<T>())) : (new List<T>() as IQueryable<T>))
        {
            MessageBus = messageBus;
            if (StaticLogger.IsListening)
            {
                Penguin.Debugging.StaticLogger.Log(Id.ToString() + $": Creating context for type {typeof(T).FullName}", StaticLogger.LoggingLevel.Call);
            }

            this.DbContext = dbContext;

            if (!IsValidType(dbContext, typeof(T)))
            {
                throw new ArgumentException($"Can not create {nameof(EFPersistenceContext<T>)}. {typeof(T).FullName} was not found on the {nameof(DbContext)}");
            }
        }

        /// <summary>
        /// Adds a range of objects to the underlying context
        /// </summary>
        /// <param name="o">The objects to add</param>
        public void Add(params T[] o)
        {
            UpdateTimestamps(o);

            DbSet<T> set = this.DbContext.Set<T>();

            set.AddRange(o);
        }

        /// <summary>
        /// Adds a range of objects to the underlying context
        /// </summary>
        /// <param name="o">The objects to add</param>
        public override void Add(params object[] o)
        {
            UpdateTimestamps(o);

            DbSet set = this.DbContext.Set(this.BaseType);

            set.AddRange(o);
        }

        /// <summary>
        /// Adds or updates a range of objects to the underlying context
        /// </summary>
        /// <param name="o">The objects to add or update</param>
        public override void AddOrUpdate(params T[] o)
        {
            UpdateTimestamps(o);

            DbSet<T> set = this.DbContext.Set<T>();

            List<T> newObjects = new List<T>(o.Length);

            foreach(T k in o)
            {
                int Key = k._Id;
                T old;
                if(Key == 0 || (old = set.Find(Key)) == null)
                {
                    newObjects.Add(k);
                } else
                {
                    k.Populate(old);
                }
            }

            set.AddOrUpdate(newObjects.ToArray());
        }

        /// <summary>
        /// Takes the specified WriteContext and registers it, then enables data persistence. If this is the first open context, all entities retrieved before this point are detached to prevent accidental saves
        /// </summary>
        /// <param name="context">The write context to open this persistence context with</param>
        public override void BeginWrite(IWriteContext context)
        {
            if (!OpenWriteContexts.ContainsKey(this.DbContext))
            {
                OpenWriteContexts.TryAdd(DbContext, new SynchronizedCollection<IWriteContext>());
            }

            if (StaticLogger.IsListening)
            {
                StaticLogger.Log(Id.ToString() + $": Enabling write. Initial depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
            }

            this.WriteEnabled = true;

            if (!OpenWriteContexts[this.DbContext].Contains(context))
            {
                OpenWriteContexts[this.DbContext].Add(context);
            }
        }

        /// <summary>
        /// Closes all open write contexts, and detaches all changed entities for safety
        /// </summary>
        public override void CancelWrite()
        {
            if (StaticLogger.IsListening)
            {
                StaticLogger.Log(Id.ToString() + $": Cancelling write. Current depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Final);
            }

            DbContext.Dispose();

            this.WriteEnabled = false;

            OpenWriteContexts.TryRemove(this.DbContext, out SynchronizedCollection<IWriteContext> _);
        }

        /// <summary>
        /// Commits all changed entities to the database. requires a valid open write context to ensure operations are being performed in the correct scope
        /// </summary>
        /// <param name="writeContext">Any valid open write context</param>
        public override void Commit(IWriteContext writeContext)
        {
            //We only actuall want to call commit if this is the ONLY open context (Top Level in nested call)
            if (this.GetWriteContexts().Length == 1 && this.GetWriteContexts().Single() == writeContext)
            {
                if (this.WriteEnabled)
                {
                    if (StaticLogger.IsListening)
                    {
                        StaticLogger.Log(Id.ToString() + $": Saving context changes. Current depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
                    }

                    try
                    {
                        this.DbContext.SaveChanges();
                    } catch(Exception ex)
                    {
                        this.OpenWriteContexts.Clear(this.DbContext);

                        this.DbContext.Dispose();

                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }
                }
                else
                {
                    if (StaticLogger.IsListening)
                    {
                        StaticLogger.Log(Id.ToString() + $": Write not enabled. Can not save changes", StaticLogger.LoggingLevel.Final);
                    }
                    throw new UnauthorizedAccessException("This context has not been enabled for writing");
                }
            }
        }

        /// <summary>
        /// Commits all changed entities to the database. requires a valid open write context to ensure operations are being performed in the correct scope
        /// </summary>
        /// <param name="writeContext">Any valid open write context</param>
        public override async Task CommitASync(IWriteContext writeContext)
        {
            //We only actually want to call commit if this is the ONLY open context (Top Level in nested call)
            if (this.GetWriteContexts().Length == 1 && this.GetWriteContexts().Single() == writeContext)
            {
                if (this.WriteEnabled)
                {
                    if (StaticLogger.IsListening)
                    {
                        StaticLogger.Log(Id.ToString() + $": Async Saving context changes. Current depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
                    }
                    await this.DbContext.SaveChangesAsync();
                }
                else
                {
                    throw new UnauthorizedAccessException("This context has not been enabled for writing");
                }
            }
        }

        /// <summary>
        /// Deletes a collection of entities from the underlying context. If auditableEntities, simply sets the date deleted
        /// </summary>
        /// <param name="o">the objects to delete</param>
        public void Delete(params T[] o)
        {
            DbSet<T> set = this.DbContext.Set<T>();

            foreach (T t in o)
            {
                UpdateTimestamps(o);

                if (t is AuditableEntity)
                {
                    (t as AuditableEntity).DateDeleted = (t as AuditableEntity).DateDeleted ?? DateTime.Now;
                }
                else
                {
                    set.Remove(t);
                }
            }
        }

        /// <summary>
        /// Deletes a collection of entities from the underlying context. If auditableEntities, simply sets the date deleted
        /// </summary>
        /// <param name="o">the objects to delete</param>
        public override void Delete(params object[] o)
        {
            DbSet set = this.DbContext.Set(this.BaseType);

            foreach (object t in o)
            {
                UpdateTimestamps(o);

                if (t is AuditableEntity)
                {
                    (t as AuditableEntity).DateDeleted = (t as AuditableEntity).DateDeleted ?? DateTime.Now;
                }
                else
                {
                    set.Remove(t);
                }
            }
        }

        /// <summary>
        /// Closes the provided writecontext, and persists changes if it was the last open context (then detaches entities)
        /// </summary>
        /// <param name="context"></param>
        public override void EndWrite(IWriteContext context)
        {
            OpenWriteContexts[this.DbContext].Remove(context);

            if (StaticLogger.IsListening)
            {
                StaticLogger.Log(Id.ToString() + $": Ending write at depth of {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Final);
            }

            if (OpenWriteContexts[this.DbContext].Count == 0)
            {
                DbContext.Dispose();

                this.WriteEnabled = false;

                OpenWriteContexts.TryRemove(this.DbContext, out SynchronizedCollection<IWriteContext> _);
            }
        }

        /// <summary>
        /// Returns an immutable array of all writecontexts currently open on this persistence context
        /// </summary>
        /// <returns></returns>
        public override IWriteContext[] GetWriteContexts()
        {
            return this.OpenWriteContexts[this.DbContext].ToArray();
        }

        /// <summary>
        /// Updates a list of objects on the underlying DbContext
        /// </summary>
        /// <param name="o">The object list to update</param>
        public void Update(params T[] o) => this.Update(o.ToArray());

        /// <summary>
        /// Updates a list of objects on the underlying DbContext
        /// </summary>
        /// <param name="o">The object list to update</param>
        public override void Update(params object[] o) => this.Add(o.Cast<KeyedObject>().Where(t => t._Id == 0).ToArray());

        /// <summary>
        /// Generates a new WriteContext capable of opening this PersistenceContext
        /// </summary>
        /// <returns>A new WriteContext capable of opening this PersistenceContext</returns>
        public override IWriteContext WriteContext()
        {
            return new WriteContext(this);
        }

        /// <summary>
        /// Returns a subset including only the derived type from the underlying persistence context
        /// </summary>
        /// <typeparam name="TDerived">A type derived from the persitence context type</typeparam>
        /// <returns>A subset including only the derived type from the underlying persistence context</returns>
        public override IQueryable<TDerived> OfType<TDerived>()
        {
            return GenerateBaseQuery(this.DbContext.Set<TDerived>());
        }

        /// <summary>
        /// The optionally provided message bus for sending persistence messages over
        /// </summary>
        protected MessageBus MessageBus { get; set; }

        /// <summary>
        /// Generates a list of strings to Include while accessing the database, using the EagerLoad attributes found on the properties
        /// </summary>
        /// <param name="toGenerate">The type to generate the strings for</param>
        /// <param name="NameSpace">The current namespace formatted property list representing where we are in our recursive hierarchy</param>
        /// <param name="Recursive">Whether or not we should continue recursing into child properties</param>
        /// <returns>A list of strings to Include while accessing the database</returns>
        protected static List<string> IncludeStrings(Type toGenerate, string NameSpace = "", bool Recursive = true)
        {
            Stack<Type> typeStack = new Stack<Type>();

            typeStack.Push(toGenerate);

            return IncludeStrings(typeStack, NameSpace, Recursive);
        }

        /// <summary>
        /// Generates a list of strings to Include while accessing the database, using the EagerLoad attributes found on the properties
        /// </summary>
        /// <param name="toGenerate">The type to generate the strings for</param>
        /// <param name="NameSpace">The current namespace formatted property list representing where we are in our recursive hierarchy</param>
        /// <param name="Recursive">Whether or not we should continue recursing into child properties</param>
        /// <param name="depth">Any depth that has already been specified (as non infinite) to be used when capping off the recursion</param>
        /// <returns>A list of strings to Include while accessing the database</returns>
        protected static List<string> IncludeStrings(Stack<Type> toGenerate, string NameSpace = "", bool Recursive = true, int? depth = null)
        {
            List<string> ToReturn = new List<string>();

            if (depth == 0)
            { return ToReturn; }

            List<PropertyInfo> EagerLoadProperties = Cache.GetProperties(toGenerate.Peek()).Where(p => Cache.HasAttribute<EagerLoad>(p)).ToList();

            foreach (PropertyInfo toEagerLoad in EagerLoadProperties)
            {
                string thisProp = NameSpace + toEagerLoad.Name;
                ToReturn.Add(thisProp);

                if (Recursive)
                {
                    int? thisPropDepth = depth ?? Cache.GetAttribute<EagerLoad>(toEagerLoad).Depth;

                    if (thisPropDepth != null)
                    {
                        thisPropDepth--;
                    }

                    Type ToLoad = toEagerLoad.PropertyType.GetCoreType() == CoreType.Collection ? toEagerLoad.PropertyType.GetCollectionType() : toEagerLoad.PropertyType;

                    bool recurse = !toGenerate.Contains(ToLoad) && thisPropDepth != 0;

                    toGenerate.Push(ToLoad);

                    ToReturn.AddRange(IncludeStrings(toGenerate, thisProp + ".", recurse, thisPropDepth));

                    toGenerate.Pop();
                }
            }

            return ToReturn;
        }

        /// <summary>
        /// If the object is an AuditableEntity, this updates the timestamps during saves
        /// </summary>
        /// <param name="o">The objects to update</param>
        protected void UpdateTimestamps(params object[] o)
        {
            foreach (object i in o)
            {
                if (i is AuditableEntity)
                {
                    (i as AuditableEntity).DateCreated = (i as AuditableEntity).DateCreated ?? DateTime.Now;
                    (i as AuditableEntity).DateModified = DateTime.Now;
                }
            }
        }

        private string Id { get; set; } = $"{Guid.NewGuid()} <{typeof(T).FullName}>";

        private WriteContextBag OpenWriteContexts { get; set; } = new WriteContextBag();

        private static DbQuery<T> GenerateBaseQuery(DbSet<T> Set) => GenerateBaseQuery<T>(Set);

        private static DbQuery<TDerived> GenerateBaseQuery<TDerived>(DbSet<TDerived> Set) where TDerived : T
        {
            List<string> includes = IncludeStrings(typeof(TDerived));

            DbQuery<TDerived> dbQuery = Set;

            foreach (string toEagerLoad in includes)
            {
                dbQuery = dbQuery.Include(toEagerLoad);
            }

            return dbQuery;
        }

        private static bool IsValidType(IDbContext holder, Type toCheck)
        {
            try
            {
                return holder.Set(toCheck) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // To detect redundant calls
        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~PersistenceContext() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // TODO: uncomment the following line if the finalizer is overridden above.// GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Generic types dont share static properties so this holder
    /// exists to provide all context types with access to the same bag through a shared class
    /// </summary>
    public class WriteContextBag
    {
        /// <summary>
        /// Accesses contexts in this bag that are associated with the specified DbContext
        /// </summary>
        /// <param name="context">The DbContext to use when getting the WriteContexts</param>
        /// <returns></returns>
        public SynchronizedCollection<IWriteContext> this[IDbContext context]
        {
            get
            {
                return OpenWriteContexts[context];
            }
        }

        internal bool ContainsKey(IDbContext dbContext)
        {
            return OpenWriteContexts.ContainsKey(dbContext);
        }

        internal bool TryAdd(IDbContext dbContext, SynchronizedCollection<IWriteContext> synchronizedCollection)
        {
            return OpenWriteContexts.TryAdd(dbContext, synchronizedCollection);
        }

        internal bool TryRemove(IDbContext dbContext, out SynchronizedCollection<IWriteContext> synchronizedCollection)
        {
            return OpenWriteContexts.TryRemove(dbContext, out synchronizedCollection);
        }

        internal void Clear(IDbContext dbContext)
        {
            OpenWriteContexts.TryRemove(dbContext, out _);
        }

        private static ConcurrentDictionary<IDbContext, SynchronizedCollection<IWriteContext>> OpenWriteContexts = new ConcurrentDictionary<IDbContext, SynchronizedCollection<IWriteContext>>();
    }
}