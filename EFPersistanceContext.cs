﻿using Penguin.Debugging;
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
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Penguin.Persistence.Repositories.EntityFramework
{
    /// <summary>
    /// A persistence context that uses Entity Framework as its backing for data
    /// </summary>
    /// <typeparam name="T">The type of the object contained in this context</typeparam>
    public class EFPersistenceContext<T> : PersistenceContext<T> where T : KeyedObject
    {
        private static readonly bool HasIncludes;

        private static readonly string[] Includes = IncludeStrings(typeof(T)).OrderBy(s => s.Split('.').Length - 1).ToArray();

        private readonly WriteContextBag OpenWriteContexts = new WriteContextBag();

        private bool? isValid;

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
                if (!this.isValid.HasValue)
                {
                    this.isValid = IsValidType(this.DbContext, typeof(T));
                }

                return this.isValid.Value;
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
            get => this.IsValid ? (GenerateBaseQuery(this.DbContext.Set<T>())) : new List<T>() as IQueryable<T>;
            set
            {
            }
        }

        private string Id { get; set; } = $"{Guid.NewGuid()} <{typeof(T).FullName}>";

        static EFPersistenceContext()
        {
            HasIncludes = Includes.Length > 0;
        }

        /// <summary>
        /// Creates a new instance of this persistence context
        /// </summary>
        /// <param name="dbContext">The underlying DbContext to use as the data source</param>
        /// <param name="messageBus">An optional message bus for publishing persistence events</param>
        public EFPersistenceContext(IDbContext dbContext, MessageBus messageBus = null) : base(typeof(T), null)
        {
            this.MessageBus = messageBus;
            if (StaticLogger.IsListening)
            {
                Penguin.Debugging.StaticLogger.Log($"{this.Id}: Creating context for type {typeof(T).FullName}", StaticLogger.LoggingLevel.Call);
            }

            this.DbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

            if (!IsValidType(dbContext, typeof(T)))
            {
                throw new ArgumentException($"Can not create {nameof(EFPersistenceContext<T>)}. {typeof(T).FullName} was not found on the {nameof(this.DbContext)}");
            }
        }

        /// <summary>
        /// Adds a range of objects to the underlying context
        /// </summary>
        /// <param name="o">The objects to add</param>
        public override void Add(T o)
        {
            DbSet<T> set = this.DbContext.Set<T>();

            try
            {
                set.Add(o);
            }
            catch (System.InvalidOperationException ex) when (ex.Message.Contains("has multiplicity 1 or 0..1."))
            {
                throw new InvalidOperationException("Something went wrong adding the entity to the database. Make sure any linking properties inherit from ICollection. Properties can NOT be interfaces!", ex);
            }
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

            if (!this.CheckAndUpdate(o))
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
            this.DbContext.BeginWrite(!this.WriteEnabled);

            if (!WriteContextBag.ContainsKey(this.DbContext))
            {
                WriteContextBag.TryAdd(this.DbContext, new SynchronizedCollection<IWriteContext>());
            }

            if (StaticLogger.IsListening)
            {
                StaticLogger.Log($"{this.Id}: Enabling write. Initial depth {this.OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
            }

            if (!this.OpenWriteContexts[this.DbContext].Contains(context))
            {
                this.OpenWriteContexts[this.DbContext].Add(context);
            }
        }

        /// <summary>
        /// Closes all open write contexts, and detaches all changed entities for safety
        /// </summary>
        public override void CancelWrite()
        {
            if (StaticLogger.IsListening)
            {
                StaticLogger.Log($"{this.Id}: Cancelling write. Current depth {this.OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Final);
            }

            this.DbContext.Dispose();

            WriteContextBag.TryRemove(this.DbContext, out SynchronizedCollection<IWriteContext> _);
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
                    StaticLogger.Log($"{this.Id}: Saving context changes. Current depth {this.OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
                }


                /* Unmerged change from project 'Penguin.Persistence.Repositories.EntityFramework.Local (net48)'
                Before:
                                int retryCount = 0;

                                //Why is this here?
                After:
                                int retryCount = 0;

                                //Why is this here?
                */

                /* Unmerged change from project 'Penguin.Persistence.Repositories.EntityFramework.Local (netstandard2.0)'
                Before:
                                int retryCount = 0;

                                //Why is this here?
                After:
                                int retryCount = 0;

                                //Why is this here?
                */
                int retryCount = 0;

                //Why is this here?
                bool retry = false;

                Queue<PostEntitySaveEvent> postSaveEvents = this.PreCommitMessages();

                do
                {

                    try
                    {
                        this.DbContext.SaveChanges();
                        this.PostCommitMessages(postSaveEvents);
                        break;
                    }
                    catch (Exception) when (retryCount++ < 5 && retry)
                    {
                        Task.Delay(100).Wait();
                    }
                    catch (Exception)
                    {
                        WriteContextBag.Clear(this.DbContext);

                        this.DbContext.Dispose();

                        throw;
                    }
                } while (true);
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
                    StaticLogger.Log($"{this.Id}: Async Saving context changes. Current depth {this.OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Call);
                }
                Queue<PostEntitySaveEvent> postSaveEvents = this.PreCommitMessages();
                await this.DbContext.SaveChangesAsync().ConfigureAwait(false);
                this.PostCommitMessages(postSaveEvents);
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
            this.OpenWriteContexts[this.DbContext].Remove(context);

            if (StaticLogger.IsListening)
            {
                StaticLogger.Log($"{this.Id}: Ending write at depth of {this.OpenWriteContexts[this.DbContext].Count}", StaticLogger.LoggingLevel.Final);
            }

            if (this.OpenWriteContexts[this.DbContext].Count == 0)
            {
                this.DbContext.Dispose();

                WriteContextBag.TryRemove(this.DbContext, out SynchronizedCollection<IWriteContext> _);
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

            if (!HasIncludes)
            {
                return toReturn;
            }

            IEnumerable<string> includes = Includes.Select(s => $"Root.{s}");

            Dictionary<string, List<object>> navigationProperties = new Dictionary<string, List<object>>(Includes.Length);
            Dictionary<object, DbEntityEntry> EntityEntries = new Dictionary<object, DbEntityEntry>(Includes.Length);

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
        /// Returns an immutable array of all write contexts currently open on this persistence context
        /// </summary>
        /// <returns></returns>
        public override IEnumerable<IWriteContext> GetWriteContexts()
        {
            return this.OpenWriteContexts[this.DbContext].ToList();
        }

        /// <summary>
        /// Returns a subset including only the derived type from the underlying persistence context
        /// </summary>
        /// <typeparam name="TDerived">A type derived from the persistence context type</typeparam>
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

            this.CheckAndUpdate(o);
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
            if (toGenerate is null)
            {
                throw new ArgumentNullException(nameof(toGenerate));
            }

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

        private static DbQuery<T> GenerateBaseQuery(DbSet<T> Set)
        {
            return GenerateBaseQuery<T>(Set);
        }

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
            return holder.Set(toCheck) != null;
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
                        this.MessageBus?.Send(Activator.CreateInstance(typeof(Created<>).MakeGenericType(thisType), psEvent.Entity));
                        ;
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

            List<DbEntityEntry> modifiedEntities = this.DbContext.ChangeTracker.Entries().Where(x => x.State == EntityState.Added || x.State == EntityState.Deleted || x.State == EntityState.Modified).ToList();

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
        /// <summary>
        /// Accesses contexts in this bag that are associated with the specified DbContext
        /// </summary>
        /// <param name="context">The DbContext to use when getting the WriteContexts</param>
        /// <returns></returns>
        public SynchronizedCollection<IWriteContext> this[IDbContext context] => OpenWriteContexts.TryGetValue(context, out SynchronizedCollection<IWriteContext> contexts) ? contexts : new SynchronizedCollection<IWriteContext>();

        private static readonly ConcurrentDictionary<IDbContext, SynchronizedCollection<IWriteContext>> OpenWriteContexts = new ConcurrentDictionary<IDbContext, SynchronizedCollection<IWriteContext>>();

        internal static void Clear(IDbContext dbContext)
        {
            OpenWriteContexts.TryRemove(dbContext, out _);
        }

        internal static bool ContainsKey(IDbContext dbContext)
        {
            return OpenWriteContexts.ContainsKey(dbContext);
        }

        internal static bool TryAdd(IDbContext dbContext, SynchronizedCollection<IWriteContext> synchronizedCollection)
        {
            return OpenWriteContexts.TryAdd(dbContext, synchronizedCollection);
        }

        internal static bool TryRemove(IDbContext dbContext, out SynchronizedCollection<IWriteContext> synchronizedCollection)
        {
            return OpenWriteContexts.TryRemove(dbContext, out synchronizedCollection);
        }
    }
}