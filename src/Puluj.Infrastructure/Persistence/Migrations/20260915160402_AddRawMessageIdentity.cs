using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Puluj.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P04 (ADR-0003): raw identity `(source_id, source_message_key, source_revision)`. The columns are backfilled from the
    /// legacy `source_message_id` (`{id}:e{editUnix}` → key `{id}` + revision `e{editUnix}`; everything else → revision `0`,
    /// the same rule as SourceIdentity.FromLegacy and fixtures/identity-cases.json) in one UPDATE — fine for the current
    /// volume, a batched backfill would be needed for a very large table. The content hash stops being unique: it is a
    /// similarity index, a new post with the same text is still a new post. Down drops the new columns and index but
    /// deliberately does NOT restore the unique hash — rows with equal content may exist by then (rollback of code, not
    /// of data); the legacy `(source_id, source_message_id)` unique index is untouched in both directions. Rolling back
    /// the image without Down is refused by the schema: the new columns have no default (see BackfillSql).
    /// </summary>
    public partial class AddRawMessageIdentity : Migration
    {
        private const string BackfillSql = """
            UPDATE raw_messages SET
                source_message_key = COALESCE((regexp_match(source_message_id, '^(.+):(e[0-9]+)$'))[1], source_message_id),
                source_revision = COALESCE((regexp_match(source_message_id, '^(.+):(e[0-9]+)$'))[2], '0')
            WHERE source_message_key = '' OR source_revision = '';
            -- No default after the backfill: a writer that does not know the columns (an image from before P04 after Up
            -- without Down) must fail loudly on NOT NULL instead of storing every post under the identity ('', '') and
            -- silently losing all but the first through ON CONFLICT DO NOTHING.
            ALTER TABLE raw_messages ALTER COLUMN source_message_key DROP DEFAULT, ALTER COLUMN source_revision DROP DEFAULT;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_raw_messages_hash",
                table: "raw_messages");

            migrationBuilder.AddColumn<string>(
                name: "source_message_key",
                table: "raw_messages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "source_revision",
                table: "raw_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.CreateIndex(
                name: "ix_raw_messages_hash",
                table: "raw_messages",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ix_raw_messages_source_id_source_message_key_source_revision",
                table: "raw_messages",
                columns: new[] { "source_id", "source_message_key", "source_revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_raw_messages_hash",
                table: "raw_messages");

            migrationBuilder.DropIndex(
                name: "ix_raw_messages_source_id_source_message_key_source_revision",
                table: "raw_messages");

            migrationBuilder.DropColumn(
                name: "source_message_key",
                table: "raw_messages");

            migrationBuilder.DropColumn(
                name: "source_revision",
                table: "raw_messages");

            // Non-unique on purpose: see the class summary.
            migrationBuilder.CreateIndex(
                name: "ix_raw_messages_hash",
                table: "raw_messages",
                column: "hash");
        }
    }
}
