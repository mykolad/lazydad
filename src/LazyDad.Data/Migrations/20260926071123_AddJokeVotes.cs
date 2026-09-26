using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LazyDad.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJokeVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Down",
                table: "Jokes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Up",
                table: "Jokes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Jokes_GeneratedAt_Id",
                table: "Jokes",
                columns: new[] { "GeneratedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jokes_GeneratedAt_Id",
                table: "Jokes");

            migrationBuilder.DropColumn(
                name: "Down",
                table: "Jokes");

            migrationBuilder.DropColumn(
                name: "Up",
                table: "Jokes");
        }
    }
}
