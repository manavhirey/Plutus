using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plutus.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministratorSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdministratorSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PasswordHashFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdministratorSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdministratorSessions_ExpiresAt_RevokedAt",
                table: "AdministratorSessions",
                columns: new[] { "ExpiresAt", "RevokedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdministratorSessions");
        }
    }
}
