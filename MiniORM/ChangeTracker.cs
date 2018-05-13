namespace MiniORM
{
	using System.Collections.Generic;

	internal class ChangeTracker<T>
	{
		private readonly List<T> added;

		private readonly List<T> removed;

		public ChangeTracker()
		{
			this.added = new List<T>();
			this.removed = new List<T>();
		}

		public IReadOnlyCollection<T> Added => this.added.AsReadOnly();

		public IReadOnlyCollection<T> Removed => this.removed.AsReadOnly();

		public void Add(T item) => this.added.Add(item);

		public void Remove(T item) => this.removed.Add(item);
	}
}