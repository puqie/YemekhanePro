using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MealPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meal_type_price_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MealTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PriceCents = table.Column<long>(type: "INTEGER", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PreviousPriceCents = table.Column<long>(type: "INTEGER", nullable: true),
                    ChangedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meal_type_price_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_meal_type_price_history_meal_types_MealTypeId",
                        column: x => x.MealTypeId,
                        principalTable: "meal_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_meal_type_price_history_MealTypeId_EffectiveFrom",
                table: "meal_type_price_history",
                columns: new[] { "MealTypeId", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meal_type_price_history");
        }
    }
}
