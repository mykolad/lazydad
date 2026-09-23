using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LazyDad.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTopJokes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TopJokes",
                columns: table => new
                {
                    Language = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    JokeId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    JudgeModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SelectedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopJokes", x => new { x.Language, x.Rank });
                    table.ForeignKey(
                        name: "FK_TopJokes_Jokes_JokeId",
                        column: x => x.JokeId,
                        principalTable: "Jokes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TopJokes_JokeId",
                table: "TopJokes",
                column: "JokeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TopJokes");
        }
    }
}
