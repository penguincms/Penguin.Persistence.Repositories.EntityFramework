using Penguin.Messaging.Persistence.Interfaces;
using System.Collections.Generic;
using System.Data.Entity;

namespace Penguin.Persistence.Repositories.EntityFramework.Objects
{
    internal class PostEntitySaveEvent : IUpdated
    {
        public object Entity { get; set; }
        public EntityState EntityState { get; set; }
        public Dictionary<string, object> NewValues { get; } = new Dictionary<string, object>();
        public Dictionary<string, object> OldValues { get; } = new Dictionary<string, object>();
    }
}