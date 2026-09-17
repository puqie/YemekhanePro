using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IncomeTypeCountsTowardTuition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CountsTowardTuition",
                table: "income_types",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Sahada anasinifi tahsilati zaten "Anasınıfı Ücreti" gibi bir turle giriliyor; bu turler
            // isaretli baslar ki ekran ilk acilista bos gorunmesin. Isaret Kasa > Gelir Turleri'nden
            // degistirilebilir. SQLite LIKE yalnizca ASCII'de buyuk/kucuk harf duyarsizdir; "anas" ve
            // "taksit" ASCII oldugu icin "Anasınıfı", "ANASINIFI TAKSİT" gibi adlari yakalar.
            migrationBuilder.Sql("UPDATE income_types SET CountsTowardTuition = 1 WHERE Name LIKE '%anas%' OR Name LIKE '%taksit%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountsTowardTuition",
                table: "income_types");
        }
    }
}
