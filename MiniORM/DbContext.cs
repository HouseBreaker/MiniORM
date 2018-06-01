namespace MiniORM
{
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.ComponentModel.DataAnnotations;
	using System.ComponentModel.DataAnnotations.Schema;
	using System.Data.SqlClient;
	using System.Linq;
	using System.Reflection;
	using JetBrains.Annotations;

	public class DbContext
	{
		private readonly DatabaseConnection connection;

		private readonly IEnumerable<PropertyInfo> dbSetProperties;

		internal static readonly Type[] AllowedSqlTypes =
		{
			typeof(string),
			typeof(int),
			typeof(uint),
			typeof(long),
			typeof(ulong),
			typeof(decimal),
			typeof(bool),
			typeof(DateTime)
		};

		public DbContext(string connectionString)
		{
			this.connection = new DatabaseConnection(connectionString);

			this.dbSetProperties = this.GetDbSetProperties();

			using (new ConnectionManager(connection))
			{
				this.InitializeDbSets();
			}

			this.MapAllRelations();
		}

		public void SaveChanges()
		{
			var dbSets = this.dbSetProperties
				.Select(pi => pi.GetValue(this))
				.ToArray();

			foreach (IEnumerable dbSet in dbSets)
			{
				var invalidEntities = ((IEnumerable<object>) dbSet)
					.Where(entity => !IsObjectValid(entity))
					.ToArray();

				if (invalidEntities.Any())
				{
					throw new InvalidOperationException(
						$"{invalidEntities.Length} Invalid Entities found in {dbSet.GetType().Name}!");
				}
			}

			using (new ConnectionManager(connection))
			{
				using (var transaction = this.connection.StartTransaction())
				{
					foreach (IEnumerable dbSet in dbSets)
					{
						var dbSetType = dbSet.GetType().GetGenericArguments().First();

						var persistMethod = typeof(DbContext)
							.GetMethod("Persist", BindingFlags.Instance | BindingFlags.NonPublic)
							.MakeGenericMethod(dbSetType);

						try
						{
							persistMethod.Invoke(this, new object[] {dbSet, transaction});
						}
						catch (TargetInvocationException tie)
						{
							throw tie.InnerException;
						}
						catch (InvalidOperationException)
						{
							transaction.Rollback();
							throw;
						}
						catch (SqlException)
						{
							transaction.Rollback();
							throw;
						}
					}

					transaction.Commit();
				}
			}
		}

		[UsedImplicitly]
		private void Persist<T>(DbSet<T> dbSet, SqlTransaction transaction)
			where T : class, new()
		{
			var tableName = GetTableName(typeof(T));

			var columns = this.connection.FetchColumnNames(tableName).ToArray();

			if (dbSet.ChangeTracker.Added.Any())
			{
				this.connection.InsertEntities(dbSet.ChangeTracker.Added, tableName, columns);
			}

			var modifiedEntities = dbSet.ChangeTracker.GetModifiedEntities(dbSet).ToArray();
			if (modifiedEntities.Any())
			{
				this.connection.UpdateEntities(modifiedEntities, tableName, columns);
			}

			if (dbSet.ChangeTracker.Removed.Any())
			{
				this.connection.DeleteEntities(dbSet.ChangeTracker.Removed, tableName, columns);
			}
		}

		private void InitializeDbSets()
		{
			foreach (var dbSetProperty in this.dbSetProperties)
			{
				var dbSetType = dbSetProperty.PropertyType.GenericTypeArguments.First();

				var populateDbSetGeneric = typeof(DbContext)
					.GetMethod("PopulateDbSet", BindingFlags.Instance | BindingFlags.NonPublic)
					.MakeGenericMethod(dbSetType);

				populateDbSetGeneric.Invoke(this, new object[] {dbSetProperty});
			}
		}

		private void MapAllRelations()
		{
			foreach (var dbSetProperty in this.dbSetProperties)
			{
				var dbSetType = dbSetProperty.PropertyType.GenericTypeArguments.First();

				var mapRelationsGeneric = typeof(DbContext)
					.GetMethod("MapRelations", BindingFlags.Instance | BindingFlags.NonPublic)
					.MakeGenericMethod(dbSetType);

				var dbSet = dbSetProperty.GetValue(this);

				mapRelationsGeneric.Invoke(this, new[] {dbSet});
			}
		}

		[UsedImplicitly]
		private void PopulateDbSet<T>(PropertyInfo dbSet)
			where T : class, new()
		{
			var entities = LoadTableEntities<T>(dbSet);

			var dbSetInstance = new DbSet<T>(entities);
			ReflectionHelper.ReplaceBackingField(this, dbSet.Name, dbSetInstance);
		}

		[UsedImplicitly]
		private void MapRelations<T>(DbSet<T> dbSet)
			where T : class, new()
		{
			var entityType = typeof(T);

			MapNavigationProperties(dbSet);

			var collections = entityType
				.GetProperties()
				.Where(pi =>
					pi.PropertyType.IsGenericType &&
					pi.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>))
				.ToArray();

			foreach (var collection in collections)
			{
				var collectionType = collection.PropertyType.GenericTypeArguments.First();

				var mapCollectionMethod = typeof(DbContext)
					.GetMethod("MapCollection", BindingFlags.Instance | BindingFlags.NonPublic)
					.MakeGenericMethod(entityType, collectionType);

				mapCollectionMethod.Invoke(this, new object[] {dbSet, collection});
			}
		}

		[UsedImplicitly]
		private void MapCollection<TDbSet, TCollection>(DbSet<TDbSet> dbSet, PropertyInfo collectionProperty)
			where TDbSet : class, new() where TCollection : class, new()
		{
			var entityType = typeof(TDbSet);
			var collectionType = typeof(TCollection);

			var primaryKeys = collectionType.GetProperties()
				.Where(ReflectionHelper.HasAttribute<KeyAttribute>)
				.ToArray();

			var isManyToMany = primaryKeys.Length >= 2;

			var primaryKey = primaryKeys.First();
			var foreignKey = entityType.GetProperties()
				.First(ReflectionHelper.HasAttribute<KeyAttribute>);

			if (isManyToMany)
			{
				primaryKey = collectionType.GetProperties()
					.First(pi => collectionType
						             .GetProperty(pi.GetCustomAttribute<ForeignKeyAttribute>().Name)
						             .PropertyType == entityType);
			}

			var navigationDbSet = (DbSet<TCollection>) this.dbSetProperties
				.First(s => s.PropertyType.GenericTypeArguments.First() == collectionType)
				.GetValue(this);

			foreach (var entity in dbSet)
			{
				var primaryKeyValue = foreignKey.GetValue(entity);

				var navigationEntities = navigationDbSet
					.Where(navigationEntity => primaryKey.GetValue(navigationEntity).Equals(primaryKeyValue))
					.ToArray();

				ReflectionHelper.ReplaceBackingField(entity, collectionProperty.Name, navigationEntities);
			}
		}

		private void MapNavigationProperties<T>(DbSet<T> dbSet) where T : class, new()
		{
			var entityType = typeof(T);

			var foreignKeys = entityType.GetProperties()
				.Where(ReflectionHelper.HasAttribute<ForeignKeyAttribute>)
				.ToArray();

			foreach (var foreignKey in foreignKeys)
			{
				var navigationPropertyName =
					foreignKey.GetCustomAttribute<ForeignKeyAttribute>().Name;
				var navigationProperty = entityType.GetProperty(navigationPropertyName);

				var navigationDbSet = this.GetDbSet(navigationProperty.PropertyType)
					.GetValue(this);

				var navigationPrimaryKey = navigationProperty.PropertyType.GetProperties()
					.First(ReflectionHelper.HasAttribute<KeyAttribute>);

				foreach (var entity in dbSet)
				{
					var foreignKeyValue = foreignKey.GetValue(entity);

					var navigationPropertyValue = ((IEnumerable<object>) navigationDbSet)
						.First(currentNavigationProperty =>
							navigationPrimaryKey.GetValue(currentNavigationProperty).Equals(foreignKeyValue));

					navigationProperty.SetValue(entity, navigationPropertyValue);
				}
			}
		}

		private static bool IsObjectValid(object e)
		{
			var validationContext = new ValidationContext(e);
			var validationErrors = new List<ValidationResult>();

			var validationResult =
				Validator.TryValidateObject(e, validationContext, validationErrors, validateAllProperties: true);
			return validationResult;
		}

		private IEnumerable<T> LoadTableEntities<T>(PropertyInfo dbSet)
			where T : class
		{
			var table = dbSet.PropertyType.GenericTypeArguments.First();

			var columns = GetEntityColumnNames(table);

			var tableName = GetTableName(table);

			var fetchedRows = this.connection.FetchResultSet<T>(tableName, columns).ToArray();

			return fetchedRows;
		}

		private string GetTableName(Type tableType)
		{
			var tableName = ((TableAttribute) Attribute.GetCustomAttribute(tableType, typeof(TableAttribute)))?.Name;

			if (tableName == null)
			{
				tableName = this.GetDbSet(tableType).Name;
			}

			return tableName;
		}

		private PropertyInfo GetDbSet(Type tableType)
		{
			return this.GetDbSetProperties().First(pi => pi.PropertyType.GetGenericArguments().First() == tableType);
		}

		private IEnumerable<PropertyInfo> GetDbSetProperties()
		{
			var dbSets = this.GetType().GetProperties()
				.Where(pi => pi.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
				.ToArray();

			return dbSets;
		}

		private string[] GetEntityColumnNames(Type table)
		{
			var tableName = this.GetTableName(table);
			var dbColumns =
				this.connection.FetchColumnNames(tableName);

			var columns = table.GetProperties()
				.Where(pi => dbColumns.Contains(pi.Name) &&
				             !ReflectionHelper.HasAttribute<NotMappedAttribute>(pi) &&
				             AllowedSqlTypes.Contains(pi.PropertyType))
				.Select(pi => pi.Name)
				.ToArray();

			return columns;
		}
	}
}