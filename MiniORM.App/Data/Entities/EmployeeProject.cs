namespace MiniORM.App.Data.Entities
{
	using System.ComponentModel.DataAnnotations;
	using System.ComponentModel.DataAnnotations.Schema;

	public class EmployeeProject
	{
		[Key]
		[ForeignKey("Employee")]
		public int EmployeeId { get; set; }

		[Key]
		[ForeignKey("Project")]
		public int ProjectId { get; set; }

		public Employee Employee { get; set; }

		public Project Project { get; set; }
	}
}