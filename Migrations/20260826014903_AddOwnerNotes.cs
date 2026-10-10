using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Comments_TicketId",
                table: "Comments");

            migrationBuilder.AddColumn<bool>(
                name: "ShortLoopReview",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Acknowledgement",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAt",
                table: "Comments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NoteAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CommentId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    StoredFile = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    IsImage = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NoteAttachments_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TicketId_AcknowledgedAt",
                table: "Comments",
                columns: new[] { "TicketId", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NoteAttachments_CommentId",
                table: "NoteAttachments",
                column: "CommentId");

            // Close the door this migration would otherwise leave open.
            //
            // "Unanswered" is defined as AcknowledgedAt IS NULL, and every human note ever written
            // before this feature existed is about to satisfy that. Left alone, the upgrade turns
            // years of bulletin-board posts into outstanding directives all at once and refuses the
            // next hand-off on every ticket that has ever carried one - the pipeline stopping dead,
            // for reasons no run could act on even if it wanted to. Those notes were never delivered
            // to anybody and nothing was ever going to enforce them.
            //
            // Backfilled as answered with no acknowledgement text, which is the honest record: the
            // drawer renders them as ordinary history rather than claiming a run replied to them.
            // Author is stored as its enum NAME (see DashboardDbContext), so this matches on 'Human'.
            migrationBuilder.Sql(
                "UPDATE Comments SET AcknowledgedAt = strftime('%Y-%m-%d %H:%M:%f', 'now') "
              + "WHERE Author = 'Human' AND AcknowledgedAt IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteAttachments");

            migrationBuilder.DropIndex(
                name: "IX_Comments_TicketId_AcknowledgedAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ShortLoopReview",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AcknowledgedAt",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "Acknowledgement",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "Comments");

            migrationBuilder.CreateIndex(
                name: "IX_Comments_TicketId",
                table: "Comments",
                column: "TicketId");
        }
    }
}
