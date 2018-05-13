namespace MiniORM.App
{
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using System.Data.SqlTypes;
	using System.Linq;
	using Data;
	using Data.Entities;

	public class StartUp
	{
		public static void Main(string[] args)
		{
			var connectionString = "Server=.;Database=MiniORM;Integrated Security=True";

			var context = new SoftUniDbContext(connectionString);

			context.Employees.Add(new Employee
			{
				FirstName = "Gosho",
				LastName = "Ivanov",
				Department = context.Departments.First(),
				IsEmployed = true,
			});

			context.SaveChanges();
		}
	}
}