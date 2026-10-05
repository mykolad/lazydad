using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LazyDad.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJokeTextHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "TextHash",
                table: "Jokes",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jokes_Language_TextHash",
                table: "Jokes",
                columns: new[] { "Language", "TextHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jokes_Language_TextHash",
                table: "Jokes");

            migrationBuilder.DropColumn(
                name: "TextHash",
                table: "Jokes");
        }
    }
}
