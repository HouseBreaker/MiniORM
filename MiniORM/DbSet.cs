namespace MiniORM
{
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Linq;
	using JetBrains.Annotations;

	public class DbSet<T> : ICollection<T> 
		where T : class, new()
	{
		internal DbSet([NotNull] IEnumerable<T> entities)
		{
			this.Entities = entities.ToList();

			this.ChangeTracker = new ChangeTracker<T>(entities);
		}

		internal ChangeTracker<T> ChangeTracker { get; set; }

		internal IList<T> Entities { get; set; }

		public void Add(T item)
		{
			if (item == null)
			{
				throw new ArgumentNullException(nameof(item), "Item cannot be null!");
			}

			this.Entities.Add(item);

			this.ChangeTracker.Add(item);
		}

		public void Clear()
		{
			while (this.Entities.Any())
			{
				var entity = this.Entities.First();
				this.Remove(entity);
			}
		}

		public bool Contains(T item) => this.Entities.Contains(item);

		public void CopyTo(T[] array, int arrayIndex) => this.Entities.CopyTo(array, arrayIndex);

		public int Count => this.Entities.Count;

		public bool IsReadOnly => this.Entities.IsReadOnly;

		public bool Remove(T item)
		{
			if (item == null)
			{
				throw new ArgumentNullException(nameof(item), "item cannot be null!");
			}

			var removedSuccessfully = this.Entities.Remove(item);

			if (removedSuccessfully)
			{
				this.ChangeTracker.Remove(item);
			}

			return removedSuccessfully;
		}

		public IEnumerator<T> GetEnumerator()
		{
			return this.Entities.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return this.GetEnumerator();
		}

		public void RemoveRange(IEnumerable<T> entities)
		{
			foreach (var entity in entities.ToArray())
			{
				this.Remove(entity);
			}
		}
	}
}