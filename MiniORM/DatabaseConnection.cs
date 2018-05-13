namespace MiniORM
{
	using System;
	using System.Collections.Generic;
	using System.Data.SqlClient;
	using System.Linq;

	internal class DatabaseConnection
	{
		private readonly SqlConnection connection;

		public DatabaseConnection(string connectionString)
		{
			this.connection = new SqlConnection(connectionString);
		}

		private SqlCommand CreateCommand(string queryText, params string[] parameters)
		{
			var command = new SqlCommand(queryText, this.connection);

			foreach (var param in parameters)
			{
				command.Parameters.Add(param);
			}

			return command;
		}

		public int ExecuteQuery(string queryText, params string[] parameters)
		{
			using (var query = this.CreateCommand(queryText, parameters))
			{

				return (int)query.ExecuteScalar();
			}
		}

		public IEnumerable<T> FetchResultSet<T>(string tableName, params string[] columnNames)
		{
			var rows = new List<T>();

			var escapedColumns = string.Join(", ", columnNames.Select(c => $"[{c}]"));
			var queryText = $@"SELECT {escapedColumns} FROM {tableName}";

			using (var query = this.CreateCommand(queryText))
			{
				this.connection.Open();

				using (var reader = query.ExecuteReader())
				{
					while (reader.Read())
					{
						var columnValues = new object[reader.FieldCount];
						reader.GetValues(columnValues);

						var obj = MapColumnsToObject<T>(columnNames, columnValues);
						rows.Add(obj);
					}
				}


				this.connection.Close();
			}

			return rows;
		}

		private static T MapColumnsToObject<T>(string[] columnNames, object[] columns)
		{
			var obj = Activator.CreateInstance<T>();

			for (var i = 0; i < columns.Length; i++)
			{
				var columnName = columnNames[i];
				var columnValue = columns[i];
				
				if (columnValue is DBNull)
				{
					columnValue = null;
				}

				var property = obj.GetType().GetProperty(columnName);
				property.SetValue(obj, columnValue);
			}

			return obj;
		}
	}
}