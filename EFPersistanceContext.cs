using Penguin.Debugging;
using Penguin.Extensions.Strings;
using Penguin.Messaging.Core;
using Penguin.Messaging.Persistence.Messages;
using Penguin.Persistence.Abstractions;
using Penguin.Persistence.Abstractions.Attributes.Control;
using Penguin.Persistence.Abstractions.Interfaces;
using Penguin.Persistence.Repositories.EntityFramework.Abstractions.Interfaces;
using Penguin.Persistence.Repositories.EntityFramework.Objects;
using Penguin.Reflection;
using Penguin.Reflection.Abstractions;
using Penguin.Reflection.Extensions;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Diagnostics.Contracts;
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
        public IDbContext DbContext { get; }

        /// <summary>
        /// If true, this context has access to a valid DbSet T  on the underlying provider
        /// </summary>
        public override bool IsValid
        {
            get
            {
                if (!isValid.HasValue)
                {
                    isValid = IsValidType(this.DbContext, typeof(T));
                }

                return isValid.Value;
            }
        }

        /// <summary>
        /// If true, this context has a valid open WriteContext
        /// </summary>
        public virtual bool WriteEnabled => this.GetWriteContexts().Any();

        /// <summary>
        /// The optionally provided message bus for sending persistence messages over
        /// </summary>
        protected MessageBus MessageBus { get; set; }

        /// <summary>
        /// Returns an IQueriable that accesses the current DbSet
        /// </summary>
        protected override IQueryable<T> PrimaryDataSource
        {
            get
            {
                return IsValid ? (GenerateBaseQuery(DbContext.Set<T>())) : new List<T>() as IQueryable<T>;
            }
            set
            {
            }
        }

        private string Id { get; set; } = $"{Guid.NewGuid()} <{typeof(T).FullName}>";
        private bool? isValid { get; set; }
        private WriteContextBag OpenWriteContexts { get; set; } = new WriteContextBag();

        /// <summary>
        /// Creates a new instance of this persistence context
        /// </summary>
        /// <param name="dbContext">The underlying DbContext to use as the data source</param>
        /// <param name="messageBus">An optional message bus for publishing persistence events</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "<Pending>")]
        public EFPersistenceContext(IDbContext dbContext, MessageBus messageBus = null) : base(typeof(T), null)
        {
            Contract.Requires(dbContext != null);

            MessageBus = messageBus;
            if (StaticLogger.IsListening)
            {
                Penguin.Debugging.StaticLogger.Log($"{Id}: Creating context for type {typeof(T).FullName}", StaticLogger.LoggingLevel.Call);
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
        public override void Add(T o)
        {
            DbSet<T> set = this.DbContext.Set<T>();

            set.Add(o);
        }

        /// <summary>
        /// Adds or updates a range of objects to the underlying context
        /// </summary>
        /// <param name="o">The objects to add or update</param>
        public override void AddOrUpdate(T o)
        {
            if (o is null)
            {
                throw new ArgumentNullException(nameof(o), "Can not add or update null object");
            }

            if (!CheckAndUpdate(o))
            {
                DbSet<T> set = this.DbContext.Set<T>();
                set.Add(o);
            }
        }

        /// <summary>
        /// Calls AddRange on the underlying context
        /// </summary>
        /// <param name="o">The objects to add</param>
        public override void AddRange(IEnumerable<T> o)
        {
            this.DbContext.Set<T>().AddRange(o);
        }

        /// <summary>
        /// Takes the specified WriteContext and registers it, then enables data persistence. If this is the first open context, all entities retrieved before this point are detached to prevent accidental saves
        /// </summary>
        /// <param name="context">The write context to open this persistence context with</param>
        public override void BeginWrite(IWriteContext context)
        {
            DbContext.BeginWrite(!WriteEnabled);

            if (!OpenWriteContexts.ContainsKey(this.DbContext))
            {
                OpenWriteContexts.TryAdd(DbContext, new SynchronizedCollection<IWriteContext>());
            }

            if (StaticLogger.IsListening)
            {
                StaticLogger.Log($"{Id}: Enabling write. Initial depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
            }

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
                StaticLogger.Log($"{Id}: Cancelling write. Current depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Final);
            }

            DbContext.Dispose();

            OpenWriteContexts.TryRemove(this.DbContext, out SynchronizedCollection<IWriteContext> _);
        }

        /// <summary>
        /// Commits all changed entities to the database. requires a valid open write context to ensure operations are being performed in the correct scope
        /// </summary>
        /// <param name="writeContext">Any valid open write context</param>
        public override void Commit(IWriteContext writeContext)
        {
            //We only actually want to call commit if this is the ONLY open context (Top Level in nested call)
            if (this.GetWriteContexts().Count() == 1 && this.GetWriteContexts().Single() == writeContext)
            {
                if (StaticLogger.IsListening)
                {
                    StaticLogger.Log($"{Id}: Saving context changes. Current depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
                }

                int retryCount = 0;
                Exception lastException;
                bool retry = false;

                Queue<PostEntitySaveEvent> postSaveEvents = PreCommitMessages();

                do
                {
                    lastException = null;

                    try
                    {
                        this.DbContext.SaveChanges();
                        PostCommitMessages(postSaveEvents);
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        Task.Delay(100).Wait();
                    }
                } while (retry && retryCount++ < 5 && lastException != null);

                if (lastException != null)
                {
                    this.OpenWriteContexts.Clear(this.DbContext);

                    this.DbContext.Dispose();

                    ExceptionDispatchInfo.Capture(lastException).Throw();
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
            if (this.GetWriteContexts().Count() == 1 && this.GetWriteContexts().Single() == writeContext)
            {
                if (StaticLogger.IsListening)
                {
                    StaticLogger.Log($"{Id}: Async Saving context changes. Current depth {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
                }
                Queue<PostEntitySaveEvent> postSaveEvents = PreCommitMessages();
                await this.DbContext.SaveChangesAsync().ConfigureAwait(false);
                PostCommitMessages(postSaveEvents);
            }
        }

        /// <summary>
        /// Deletes an of entities from the underlying context. If auditableEntities, simply sets the date deleted
        /// </summary>
        /// <param name="o">the object to delete</param>
        public override void Delete(T o)
        {
            this.DbContext.Set<T>().Remove(o);
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
                StaticLogger.Log($"{Id}: Ending write at depth of {OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Final);
            }

            if (OpenWriteContexts[this.DbContext].Count == 0)
            {
                DbContext.Dispose();

                OpenWriteContexts.TryRemove(this.DbContext, out SynchronizedCollection<IWriteContext> _);
            }
        }

        /// <summary>
        /// Gets an object by its primary key
        /// </summary>
        /// <param name="Key">The key to search for</param>
        /// <returns>An object (or null) with a matching key</returns>
        public override T Find(object Key)
        {
            T toReturn = this.DbContext.Set<T>().Find(Key);

            if (toReturn is null)
            {
                return null;
            }

            List<string> includes = IncludeStrings(typeof(T)).OrderBy(s => s.Split('.').Length - 1).Select(s => $"Root.{s}").ToList();

            Dictionary<string, List<object>> navigationProperties = new Dictionary<string, List<object>>(includes.Count);
            Dictionary<object, DbEntityEntry> EntityEntries = new Dictionary<object, DbEntityEntry>(includes.Count);

            navigationProperties.Add("Root", new List<object>() { toReturn });
            EntityEntries.Add(toReturn, this.DbContext.Entry(toReturn));

            foreach (string include in includes)
            {
                if (!navigationProperties.TryGetValue(include.ToLast('.'), out List<object> objects))
                {
                    StaticLogger.Log($"EFPersistenceContext: {include.ToLast('.')} was not found in the objects dictionary when attempting to lazy load");
                    continue;
                }

                foreach (object o in objects)
                {
                    if (!EntityEntries.TryGetValue(include.ToLast('.'), out DbEntityEntry thisEntry))
                    {
                        thisEntry = this.DbContext.Entry(o);
                    }

                    DbMemberEntry memberEntry = thisEntry.Member(include.FromLast('.'));

                    if (memberEntry is DbCollectionEntry dbCollectionEntry)
                    {
                        dbCollectionEntry.Load();

                        if (memberEntry.CurrentValue != null)
                        {
                            if (!navigationProperties.TryGetValue(include, out List<object> collection))
                            {
                                collection = new List<object>();
                                navigationProperties.Add(include, collection);
                            }

                            foreach (object io in memberEntry.CurrentValue as IEnumerable)
                            {
                                collection.Add(io);
                            }
                        }
                    }
                    else if (memberEntry is DbReferenceEntry dbReferenceEntry)
                    {
                        dbReferenceEntry.Load();

                        if (memberEntry.CurrentValue != null)
                        {
                            if (!navigationProperties.TryGetValue(include, out List<object> collection))
                            {
                                collection = new List<object>();
                                navigationProperties.Add(include, collection);
                            }

                            collection.Add(memberEntry.CurrentValue);
                        }
                    }
                }
            }

            return toReturn;
        }

        /// <summary>
        /// Returns an immutable array of all writecontexts currently open on this persistence context
        /// </summary>
        /// <returns></returns>
        public override IEnumerable<IWriteContext> GetWriteContexts()
        {
            return this.OpenWriteContexts[this.DbContext].ToList();
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
        /// Updates a list of objects on the underlying DbContext
        /// </summary>
        /// <param name="o">The object list to update</param>
        public override void Update(T o)
        {
            if (o is null)
            {
                throw new ArgumentNullException(nameof(o), "Can not update null object");
            }

            CheckAndUpdate(o);
        }

        /// <summary>
        /// Generates a new WriteContext capable of opening this PersistenceContext
        /// </summary>
        /// <returns>A new WriteContext capable of opening this PersistenceContext</returns>
        public override IWriteContext WriteContext()
        {
            return new WriteContext(this);
        }

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
            Contract.Requires(toGenerate != null);

            List<string> ToReturn = new List<string>();

            if (depth == 0)
            { return ToReturn; }

            List<PropertyInfo> EagerLoadProperties = TypeCache.GetProperties(toGenerate.Peek()).Where(p => TypeCache.HasAttribute<EagerLoadAttribute>(p)).ToList();

            foreach (PropertyInfo toEagerLoad in EagerLoadProperties)
            {
                string thisProp = NameSpace + toEagerLoad.Name;
                ToReturn.Add(thisProp);

                if (Recursive)
                {
                    int? thisPropDepth = depth ?? TypeCache.GetAttribute<EagerLoadAttribute>(toEagerLoad).Depth;

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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "I dont know of any other way to get the value but to test for an error")]
        private static bool IsValidType(IDbContext holder, Type toCheck)
        {
            //try
            //{
            return holder.Set(toCheck) != null;
            //}
            //catch (Exception)
            //{
            //    return false;
            //}
        }

        private bool CheckAndUpdate(T o)
        {
            DbSet<T> set = this.DbContext.Set<T>();

            int Key = o._Id;
            T old;
            if (Key == 0 || (old = set.Find(Key)) == null)
            {
                return false;
            }
            else
            {
                o.Populate(old);
                return true;
            }
        }

        private void PostCommitMessages(Queue<PostEntitySaveEvent> PostSaveEvents)
        {
            if (this.MessageBus is null)
            {
                return;
            }

            while (PostSaveEvents.Any())
            {
                PostEntitySaveEvent psEvent = PostSaveEvents.Dequeue();

                Type thisType = psEvent.Entity.GetType();

                switch (psEvent.EntityState)
                {
                    case EntityState.Added:
                        this.MessageBus?.Send(Activator.CreateInstance(typeof(Created<>).MakeGenericType(thisType), psEvent.Entity)); ;
                        goto case EntityState.Modified;

                    case EntityState.Modified:
                        this.MessageBus?.Send(Activator.CreateInstance(typeof(Updated<>).MakeGenericType(thisType), psEvent.Entity, psEvent));
                        break;

                    case EntityState.Deleted:
                        this.MessageBus?.Send(Activator.CreateInstance(typeof(Deleted<>).MakeGenericType(thisType), psEvent.Entity));
                        break;

                    default:
                        break;
                }
            }

            this.MessageBus.Send(new PostCommit());
        }

        private Queue<PostEntitySaveEvent> PreCommitMessages()
        {
            if (this.MessageBus is null)
            {
                return new Queue<PostEntitySaveEvent>();
            }

            List<DbEntityEntry> modifiedEntities = DbContext.ChangeTracker.Entries().Where(x => x.State == EntityState.Added || x.State == EntityState.Deleted || x.State == EntityState.Modified).ToList();

            HashSet<object> processed = new HashSet<object>();

            Queue<PostEntitySaveEvent> PostSaveEvents = new Queue<PostEntitySaveEvent>();

            foreach (DbEntityEntry nextEntry in modifiedEntities)
            {
                Type thisType = TypeFactory.GetType(nextEntry.Entity);
                // For some reason items are/were being added more than once, so we need to make sure
                // that we dont process the same entity more than once
                if (processed.Contains(nextEntry.Entity))
                { continue; }

                PostEntitySaveEvent thisEvent = new PostEntitySaveEvent()
                {
                    Entity = nextEntry.Entity,
                    EntityState = nextEntry.State
                };

                PostSaveEvents.Enqueue(thisEvent);

                IEnumerable<string> propertySource = nextEntry.State == EntityState.Deleted ? nextEntry.OriginalValues.PropertyNames : nextEntry.CurrentValues.PropertyNames;

                foreach (string propertyName in propertySource)
                {
                    if (nextEntry.Property(propertyName).IsModified)
                    {
                        thisEvent.NewValues.Add(propertyName, nextEntry.Property(propertyName).CurrentValue);
                        thisEvent.OldValues.Add(propertyName, nextEntry.Property(propertyName).OriginalValue);
                    }
                    else if (nextEntry.State == EntityState.Added)
                    {
                        thisEvent.NewValues.Add(propertyName, nextEntry.Property(propertyName).CurrentValue);
                        thisEvent.OldValues.Add(propertyName, null);
                    }
                    else if (nextEntry.State == EntityState.Deleted)
                    {
                        thisEvent.NewValues.Add(propertyName, null);
                        thisEvent.OldValues.Add(propertyName, nextEntry.Property(propertyName).OriginalValue);
                    }
                }

                switch (nextEntry.State)
                {
                    case EntityState.Added:
                        this.MessageBus.Send(Activator.CreateInstance(typeof(Creating<>).MakeGenericType(thisType), nextEntry.Entity));
                        goto case EntityState.Modified;

                    case EntityState.Modified:
                        this.MessageBus.Send(Activator.CreateInstance(typeof(Updating<>).MakeGenericType(thisType), nextEntry.Entity, thisEvent));
                        break;

                    case EntityState.Deleted:
                        this.MessageBus.Send(Activator.CreateInstance(typeof(Deleting<>).MakeGenericType(thisType), nextEntry.Entity));
                        break;

                    default:
                        break;
                }

                processed.Add(nextEntry.Entity);
            }

            this.MessageBus.Send(new PreCommit());

            return PostSaveEvents;
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
        private static readonly ConcurrentDictionary<IDbContext, SynchronizedCollection<IWriteContext>> OpenWriteContexts = new ConcurrentDictionary<IDbContext, SynchronizedCollection<IWriteContext>>();

        /// <summary>
        /// Accesses contexts in this bag that are associated with the specified DbContext
        /// </summary>
        /// <param name="context">The DbContext to use when getting the WriteContexts</param>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers", Justification = "<Pending>")]
        public SynchronizedCollection<IWriteContext> this[IDbContext context]
        {
            get
            {
                return OpenWriteContexts.TryGetValue(context, out SynchronizedCollection<IWriteContext> contexts) ? contexts : new SynchronizedCollection<IWriteContext>();
            }
        }

        internal void Clear(IDbContext dbContext)
        {
            OpenWriteContexts.TryRemove(dbContext, out _);
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
    }
}