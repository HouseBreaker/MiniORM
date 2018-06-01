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

			this.MapRelations();
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
				this.connection.InsertEntities(dbSet.ChangeTracker.Added, tableName, columns, transaction);
			}

			var modifiedEntities = dbSet.ChangeTracker.GetModifiedEntities(dbSet).ToArray();
			if (modifiedEntities.Any())
			{
				this.connection.UpdateEntities(modifiedEntities, tableName, columns, transaction);
			}

			if (dbSet.ChangeTracker.Removed.Any())
			{
				this.connection.DeleteEntities(dbSet.ChangeTracker.Removed, tableName, columns, transaction);
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

		[UsedImplicitly]
		private void PopulateDbSet<T>(PropertyInfo dbSet)
			where T : class, new()
		{
			var entities = LoadTableEntities<T>(dbSet);

			var dbSetInstance = new DbSet<T>(entities);
			ReflectionHelper.ReplaceBackingField(this, dbSet.Name, dbSetInstance);
		}

		private void MapRelations()
		{
			var dbSets = this.dbSetProperties
				.Select(pi => pi.GetValue(this))
				.ToArray();

			foreach (var dbSet in dbSets)
			{
				var entityType = dbSet.GetType().GenericTypeArguments.First();

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

					foreach (var entity in (IEnumerable) dbSet)
					{
						var foreignKeyValue = foreignKey.GetValue(entity);

						var navigationPropertyValue = ((IEnumerable<object>) navigationDbSet)
							.First(currentNavigationProperty =>
								navigationPrimaryKey.GetValue(currentNavigationProperty).Equals(foreignKeyValue));

						navigationProperty.SetValue(entity, navigationPropertyValue);
					}
				}

				var collections = entityType
					.GetProperties()
					.Where(pi =>
						pi.PropertyType.IsGenericType &&
						pi.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>))
					.ToArray();

				foreach (var collection in collections)
				{
					var collectionType = collection.PropertyType.GenericTypeArguments.First();

					var primaryKeys = collectionType.GetProperties()
						.Where(ReflectionHelper.HasAttribute<KeyAttribute>)
						.ToArray();

					var isManyToMany = primaryKeys.Length >= 2;

					PropertyInfo primaryKey;
					PropertyInfo foreignKey;

					if (!isManyToMany)
					{
						primaryKey = primaryKeys.First();

						foreignKey = entityType.GetProperties()
							.First(ReflectionHelper.HasAttribute<KeyAttribute>);
					}
					else
					{
						primaryKey = collectionType.GetProperties()
							.First(pi => ReflectionHelper.HasAttribute<KeyAttribute>(pi) &&
							             collectionType
								             .GetProperty(pi.GetCustomAttribute<ForeignKeyAttribute>().Name)
								             .PropertyType == entityType);

						var foreignKeyPropertyName =
							primaryKey.GetCustomAttribute<ForeignKeyAttribute>().Name;
						var foreignKeyType = collectionType.GetProperty(foreignKeyPropertyName).PropertyType;

						foreignKey = foreignKeyType.GetProperties()
							.First(ReflectionHelper.HasAttribute<KeyAttribute>);
					}

					var navigationDbSet = (IEnumerable) dbSets
						.First(s => s.GetType().GenericTypeArguments.First() == collectionType);

					foreach (var entity in (IEnumerable) dbSet)
					{
						var primaryKeyValue = foreignKey.GetValue(entity);

						var navigationEntities = ((IEnumerable<object>) navigationDbSet)
							.Where(navigationEntity => primaryKey.GetValue(navigationEntity).Equals(primaryKeyValue))
							.ToArray();

						var castResult =
							ReflectionHelper.InvokeStaticGenericMethod(typeof(Enumerable), "Cast", collectionType,
								new object[] {navigationEntities});
						var navigationEntitiesGeneric =
							ReflectionHelper.InvokeStaticGenericMethod(typeof(Enumerable), "ToArray", collectionType,
								castResult);

						ReflectionHelper.ReplaceBackingField(entity, collection.Name, navigationEntitiesGeneric);
					}
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