using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Aranabilir metni ASCII'ye indirger: Ş->S, Ç->C, Ö->O, Ü->U, Ğ->G.
    ///
    /// Ölçüldü: 423 öğrencinin 288'i (%68) yalnızca büyük harfe çevrildiğinde ASCII
    /// yazımla bulunamıyordu — personel "simsek" yazınca ŞİMŞEK gelmiyordu. Okul
    /// personeli hızlı veri girerken Türkçe karakter kullanmaz.
    ///
    /// ŞEMA DEĞİŞMEZ: yalnızca mevcut satırların SearchName içeriği yeniden yazılır.
    /// Backfill olmadan yalnızca yeni/değişen kayıtlar doğru olur, mevcut tüm kayıtlar
    /// aramada eski davranışta kalırdı.
    /// </summary>
    public partial class TurkishSearchAsciiFolding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Rewrite(migrationBuilder, AsciiFolding, "students", "FirstName", "LastName");
            Rewrite(migrationBuilder, AsciiFolding, "classes", "Name");
            Rewrite(migrationBuilder, AsciiFolding, "student_groups", "Name");
            Rewrite(migrationBuilder, AsciiFolding, "holidays", "Name");
        }

        /// <summary>Yeni kural: Türkçe harfler ASCII karşılığına indirgenir.</summary>
        private static readonly (string From, string To)[] AsciiFolding =
        [
            ("ı", "I"), ("i", "I"), ("İ", "I"),
            ("ş", "S"), ("ğ", "G"), ("ç", "C"), ("ö", "O"), ("ü", "U"),
            ("Ş", "S"), ("Ğ", "G"), ("Ç", "C"), ("Ö", "O"), ("Ü", "U")
        ];

        /// <summary>Eski kural: Türkçe harfler korunur, yalnızca büyük harfe çevrilir.</summary>
        private static readonly (string From, string To)[] TurkishUpper =
        [
            ("ı", "I"), ("i", "I"), ("İ", "I"),
            ("ş", "Ş"), ("ğ", "Ğ"), ("ç", "Ç"), ("ö", "Ö"), ("ü", "Ü")
        ];

        /// <summary>
        /// SQLite'ın UPPER()'ı yalnızca ASCII harfleri çevirir ve Türkçe İ/ı kurallarını
        /// bilmez; Türkçe harfler REPLACE zinciriyle önce eşlenir. Sonuç
        /// TurkishSearchText.Normalize ile birebir aynı olmalıdır --
        /// TurkishSearchBackfillTests bunu doğrular.
        /// </summary>
        private static void Rewrite(MigrationBuilder migrationBuilder,
            (string From, string To)[] map, string table, params string[] columns)
        {
            var expression = string.Join(" || ' ' || ", columns.Select(x => $"COALESCE(\"{x}\", '')"));
            foreach (var (from, to) in map) expression = $"REPLACE({expression}, '{from}', '{to}')";
            migrationBuilder.Sql($"""
                UPDATE "{table}" SET "SearchName" = SUBSTR(TRIM(UPPER({expression})), 1, 200);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Rewrite(migrationBuilder, TurkishUpper, "students", "FirstName", "LastName");
            Rewrite(migrationBuilder, TurkishUpper, "classes", "Name");
            Rewrite(migrationBuilder, TurkishUpper, "student_groups", "Name");
            Rewrite(migrationBuilder, TurkishUpper, "holidays", "Name");
        }
    }
}
