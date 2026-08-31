using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task053NotificationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RelatedEntityType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RelatedEntityId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RelatedRoute = table.Column<string>(type: "TEXT", maxLength: 250, nullable: true),
                    RouteParametersJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AudiencePermission = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AudienceUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeduplicationKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    LatestAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notifications_users_AudienceUserId",
                        column: x => x.AudienceUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_receipts",
                columns: table => new
                {
                    NotificationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_receipts", x => new { x.NotificationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_notification_receipts_notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_receipts_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_receipts_UserId_ReadAt",
                table: "notification_receipts",
                columns: new[] { "UserId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_AudienceUserId",
                table: "notifications",
                column: "AudienceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_DeduplicationKey",
                table: "notifications",
                column: "DeduplicationKey");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_LatestAt_Id",
                table: "notifications",
                columns: new[] { "LatestAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_RetainUntil",
                table: "notifications",
                column: "RetainUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_receipts");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
