using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LazyDad.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRussianJokes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Russian language support was dropped; purge its jokes.
            migrationBuilder.Sql("DELETE FROM [Jokes] WHERE [Language] = N'Russian';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible: deleted jokes cannot be restored.
        }
    }
}
