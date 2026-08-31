using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Yemekhane.Application.Common;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TurkishSearchNormalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                table: "students",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                table: "student_groups",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                table: "holidays",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                table: "classes",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_students_search_name",
                table: "students",
                column: "SearchName");

            migrationBuilder.CreateIndex(
                name: "ix_student_groups_search_name",
                table: "student_groups",
                column: "SearchName");

            migrationBuilder.CreateIndex(
                name: "ix_holidays_search_name",
                table: "holidays",
                column: "SearchName");

            migrationBuilder.CreateIndex(
                name: "ix_classes_search_name",
                table: "classes",
                column: "SearchName");

            Backfill(migrationBuilder, "students", "FirstName", "LastName");
            Backfill(migrationBuilder, "classes", "Name");
            Backfill(migrationBuilder, "student_groups", "Name");
            Backfill(migrationBuilder, "holidays", "Name");
        }

        /// <summary>
        /// Mevcut satırları doldurur. Dönüşüm SQLite'ın <c>UPPER()</c> fonksiyonuyla yapılamaz:
        /// o yalnızca ASCII harfleri çevirir ve Türkçe İ/ı kurallarını bilmez. Bu yüzden Türkçe'ye
        /// özgü harfler <c>REPLACE</c> zinciriyle önce eşlenir, kalan ASCII kısmı <c>UPPER()</c>'a bırakılır.
        /// Backfill olmadan mevcut tüm kayıtlar aramada görünmez olurdu.
        /// </summary>
        private static void Backfill(MigrationBuilder migrationBuilder, string table, params string[] columns)
        {
            var source = string.Join(" || ' ' || ", columns.Select(x => $"COALESCE(\"{x}\", '')"));

            // Türkçe küçük -> büyük eşlemesi. 'i' ve 'ı' aramada birleştirildiği için ikisi de 'I' olur.
            var expression = source;
            foreach (var (lower, upper) in new[]
                     {
                         ("ı", "I"), ("i", "I"), ("İ", "I"),
                         ("ş", "Ş"), ("ğ", "Ğ"), ("ç", "Ç"), ("ö", "Ö"), ("ü", "Ü")
                     })
            {
                expression = $"REPLACE({expression}, '{lower}', '{upper}')";
            }

            migrationBuilder.Sql($"""
                UPDATE "{table}" SET "SearchName" = SUBSTR(TRIM(UPPER({expression})), 1, 200);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_students_search_name",
                table: "students");

            migrationBuilder.DropIndex(
                name: "ix_student_groups_search_name",
                table: "student_groups");

            migrationBuilder.DropIndex(
                name: "ix_holidays_search_name",
                table: "holidays");

            migrationBuilder.DropIndex(
                name: "ix_classes_search_name",
                table: "classes");

            migrationBuilder.DropColumn(
                name: "SearchName",
                table: "students");

            migrationBuilder.DropColumn(
                name: "SearchName",
                table: "student_groups");

            migrationBuilder.DropColumn(
                name: "SearchName",
                table: "holidays");

            migrationBuilder.DropColumn(
                name: "SearchName",
                table: "classes");
        }
    }
}
