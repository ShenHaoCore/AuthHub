using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthHub.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCimdRefreshLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CimdRefreshLeases",
                columns: table => new
                {
                    ClientId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    HolderToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CimdRefreshLeases", x => x.ClientId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CimdRefreshLeases");
        }
    }
}
