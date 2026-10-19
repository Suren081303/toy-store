using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Customers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CustomerMessageId",
                table: "OutboxEmails",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustomerDiscountPercent",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CustomerDiscountPercent",
                table: "OrderItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "BlockedAt",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PersonalDiscountPercent",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PersonalDiscountUntil",
                table: "AspNetUsers",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Culture = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    SentByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SentByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerMessages_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_CustomerMessageId",
                table: "OutboxEmails",
                column: "CustomerMessageId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_PersonalDiscountPercent",
                table: "AspNetUsers",
                sql: "[PersonalDiscountPercent] IS NULL OR ([PersonalDiscountPercent] >= 1 AND [PersonalDiscountPercent] <= 50)");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerMessages_UserId_CreatedAt",
                table: "CustomerMessages",
                columns: new[] { "UserId", "CreatedAt" });

            // Orders placed before this change that got the registered-customer price got the standard 10%.
            migrationBuilder.Sql("UPDATE OrderItems SET CustomerDiscountPercent = 10 WHERE CustomerDiscountApplied = 1");
            migrationBuilder.Sql("UPDATE Orders SET CustomerDiscountPercent = 10 WHERE CustomerDiscountApplied = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxEmails_CustomerMessageId",
                table: "OutboxEmails");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_PersonalDiscountPercent",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CustomerMessageId",
                table: "OutboxEmails");

            migrationBuilder.DropColumn(
                name: "CustomerDiscountPercent",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerDiscountPercent",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "BlockedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PersonalDiscountPercent",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PersonalDiscountUntil",
                table: "AspNetUsers");
        }
    }
}
