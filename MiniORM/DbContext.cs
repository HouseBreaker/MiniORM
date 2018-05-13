namespace MiniORM
{
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.ComponentModel.DataAnnotations;
	using System.ComponentModel.DataAnnotations.Schema;
	using System.Linq;
	using System.Reflection;
	using JetBrains.Annotations;

	public class DbContext
	{
		private readonly DatabaseConnection connection;

		private readonly IEnumerable<PropertyInfo> dbSetProperties;

		private static readonly Type[] AllowedSqlTypes =
		{
			typeof(string),
			typeof(int),
			typeof(uint),
			typeof(long),
			typeof(ulong),
			typeof(decimal),
			typeof(bool),
			typeof(DateTime),
		};

		public DbContext(string connectionString)
		{
			this.connection = new DatabaseConnection(connectionString);

			this.dbSetProperties = this.GetDbSetProperties();

			this.PopulateDbSets();
			this.MapRelations();
		}

		public void SaveChanges()
		{
			//var dbSets = this.dbSetProperties
			//	.Select(pi => pi.GetValue(this))
			//	.ToArray();

			//foreach (IEnumerable dbSet in dbSets)
			//{
			//	var invalidEntities = dbSet
			//		.Cast<object>()
			//		.Where(entity => !IsValid(entity))
			//		.ToArray();

			//	if (invalidEntities.Any())
			//	{
			//		throw new InvalidOperationException($"{invalidEntities.Length} Invalid Entities found in {dbSet.GetType().Name}!");
			//	}

			//}

			//foreach (DbSet dbSet in dbSets)
			//{
			//	var changeTracker = GetChangeTracker(dbSet);
			//}

			//Persist(dbSet);
		}

		private static ChangeTracker<T> GetChangeTracker<T>(DbSet<T> dbSet)
		{
			return (ChangeTracker<T>)typeof(DbSet<>)
								.GetProperty("ChangeTracker", BindingFlags.Instance | BindingFlags.NonPublic)
								.GetValue(dbSet);
		}

		private static void Persist<T>(IEnumerable dbSet)
		{
			

		}

		private void PopulateDbSets()
		{
			foreach (var dbSetProperty in this.dbSetProperties)
			{
				var dbSetType = dbSetProperty.PropertyType.GenericTypeArguments.First();

				var loadTableEntitiesGeneric = typeof(DbContext)
					.GetMethod("LoadTableEntities", BindingFlags.Instance | BindingFlags.NonPublic)
					.MakeGenericMethod(dbSetType);

				loadTableEntitiesGeneric.Invoke(this, new[] { dbSetProperty });
			}
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
					.Where(pi => pi.GetCustomAttribute<ForeignKeyAttribute>() != null)
					.ToArray();

				foreach (var foreignKey in foreignKeys)
				{
					var navigationPropertyName =
						foreignKey.GetCustomAttribute<ForeignKeyAttribute>().Name;
					var navigationProperty = entityType.GetProperty(navigationPropertyName);

					var navigationDbSet = this.GetType().GetProperties()
						.First(pi => pi.PropertyType.IsGenericType &&
									 pi.PropertyType.GenericTypeArguments.First() == navigationProperty.PropertyType)
						.GetValue(this);

					var navigationPrimaryKey = navigationProperty.PropertyType.GetProperties()
						.First(pi => pi.GetCustomAttribute<KeyAttribute>() != null);

					foreach (var entity in (IEnumerable)dbSet)
					{
						var foreignKeyValue = foreignKey.GetValue(entity);

						var navigationPropertyValue = ((IEnumerable)navigationDbSet)
							.Cast<object>()
							.First(currentNavigationProperty =>
								navigationPrimaryKey.GetValue(currentNavigationProperty).Equals(foreignKeyValue));

						navigationProperty.SetValue(entity, navigationPropertyValue);
					}
				}

				var collections = entityType
					.GetProperties()
					.Where(pi => pi.PropertyType.IsGenericType && pi.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>))
					.ToArray();

				foreach (var collection in collections)
				{
					var collectionType = collection.PropertyType.GenericTypeArguments.First();

					var primaryKeys = collectionType.GetProperties()
						.Where(pi => pi.GetCustomAttribute<KeyAttribute>() != null)
						.ToArray();

					var isManyToMany = primaryKeys.Length >= 2;

					PropertyInfo primaryKey;
					PropertyInfo foreignKey;

					if (!isManyToMany)
					{
						primaryKey = primaryKeys.First();

						foreignKey = entityType.GetProperties()
							.First(pi => pi.GetCustomAttribute<KeyAttribute>() != null);
					}
					else
					{
						primaryKey = collectionType.GetProperties()
							.First(pi => pi.GetCustomAttribute<KeyAttribute>() != null &&
										 collectionType
											 .GetProperty(pi.GetCustomAttribute<ForeignKeyAttribute>().Name)
											 .PropertyType == entityType);

						var foreignKeyPropertyName =
							primaryKey.GetCustomAttribute<ForeignKeyAttribute>().Name;
						var foreignKeyType = collectionType.GetProperty(foreignKeyPropertyName).PropertyType;

						foreignKey = foreignKeyType.GetProperties()
							.First(pi => pi.GetCustomAttribute<KeyAttribute>() != null);
					}

					var navigationDbSet = (IEnumerable)dbSets
						.First(s => s.GetType().GenericTypeArguments.First() == collectionType);

					foreach (var entity in (IEnumerable)dbSet)
					{
						var primaryKeyValue = foreignKey.GetValue(entity);

						var navigationEntities = navigationDbSet
							.Cast<object>()
							.Where(navigationEntity => primaryKey.GetValue(navigationEntity).Equals(primaryKeyValue))
							.ToArray();

						var castResult =
							InvokeStaticGenericMethod(typeof(Enumerable), "Cast", collectionType, new object[] { navigationEntities });
						var navigationEntitiesGeneric =
							InvokeStaticGenericMethod(typeof(Enumerable), "ToArray", collectionType, castResult);

						ReplaceBackingField(entity, collection.Name, navigationEntitiesGeneric);
					}
				}
			}
		}

		private static object InvokeStaticGenericMethod(Type type, string methodName, Type genericType, params object[] args)
		{
			var method = type
				.GetMethod(methodName)
				.MakeGenericMethod(genericType);

			var invokeResult = method.Invoke(null, args);
			return invokeResult;
		}

		private static bool IsValid(object e)
		{
			var validationContext = new ValidationContext(e);
			var validationErrors = new List<ValidationResult>();

			var validationResult =
				Validator.TryValidateObject(e, validationContext, validationErrors, validateAllProperties: true);
			return validationResult;
		}

		[UsedImplicitly]
		private void LoadTableEntities<T>(PropertyInfo dbSet)
		{
			var table = dbSet.PropertyType.GenericTypeArguments.First();

			var columns = GetEntityColumnNames(table);

			var tableName = ((TableAttribute)Attribute.GetCustomAttribute(table, typeof(TableAttribute)))?.Name ?? dbSet.Name;

			var fetchedRows = this.connection.FetchResultSet<T>(tableName, columns).ToArray();

			var dbSetInstance = new DbSet<T>(fetchedRows);
			ReplaceBackingField(this, dbSet.Name, dbSetInstance);
		}

		private IEnumerable<PropertyInfo> GetDbSetProperties()
		{
			var dbSets = this.GetType().GetProperties()
				.Where(pi => pi.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
				.ToArray();

			return dbSets;
		}

		private static void ReplaceBackingField(object sourceObj, string propertyName, object targetObj)
		{
			var backingField = sourceObj.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
				.First(fi => fi.Name == $"<{propertyName}>k__BackingField");

			backingField.SetValue(sourceObj, targetObj);
		}

		private static string[] GetEntityColumnNames(Type table)
		{
			var columns = table.GetProperties()
				.Where(pi => !pi.GetCustomAttributes(typeof(NotMappedAttribute), false).Any() &&
							 AllowedSqlTypes.Contains(pi.PropertyType))
				.Select(pi => pi.Name)
				.ToArray();

			return columns;
		}
	}
}