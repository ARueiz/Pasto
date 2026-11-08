using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pasto.Server.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "room_code_reservations",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    reusable_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_code_reservations", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "clipboard_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_participant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    content_bytes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clipboard_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "room_participants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    room_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_participants", x => x.id);
                    table.UniqueConstraint("AK_room_participants_room_id_id", x => new { x.room_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    creator_participant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    item_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    total_bytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rooms", x => x.id);
                    table.ForeignKey(
                        name: "FK_rooms_room_code_reservations_code",
                        column: x => x.code,
                        principalTable: "room_code_reservations",
                        principalColumn: "code");
                    table.ForeignKey(
                        name: "fk_rooms_creator",
                        columns: x => new { x.id, x.creator_participant_id },
                        principalTable: "room_participants",
                        principalColumns: new[] { "room_id", "id" });
                });

            migrationBuilder.CreateIndex(
                name: "ix_items_order",
                table: "clipboard_items",
                columns: new[] { "room_id", "created_version", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_items_owner",
                table: "clipboard_items",
                columns: new[] { "room_id", "owner_participant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_reuse",
                table: "room_code_reservations",
                columns: new[] { "reusable_after", "code" });

            migrationBuilder.CreateIndex(
                name: "IX_room_participants_credential_hash",
                table: "room_participants",
                column: "credential_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rooms_code",
                table: "rooms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rooms_id_creator_participant_id",
                table: "rooms",
                columns: new[] { "id", "creator_participant_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_clipboard_items_rooms_room_id",
                table: "clipboard_items",
                column: "room_id",
                principalTable: "rooms",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_items_owner",
                table: "clipboard_items",
                columns: new[] { "room_id", "owner_participant_id" },
                principalTable: "room_participants",
                principalColumns: new[] { "room_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_room_participants_rooms_room_id",
                table: "room_participants",
                column: "room_id",
                principalTable: "rooms",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // EF Core 不表達 PostgreSQL DEFERRABLE：外鍵先建立，再改為提交時檢查。
            // Room 先寫、房主後寫必須共用交易；跨房 Creator／Owner 仍會在 commit 被拒絕。
            migrationBuilder.Sql("""
                ALTER TABLE rooms ALTER CONSTRAINT fk_rooms_creator DEFERRABLE INITIALLY DEFERRED;
                ALTER TABLE clipboard_items ALTER CONSTRAINT fk_items_owner DEFERRABLE INITIALLY DEFERRED;
                """);

            // 以固定 DDL 明確保存資料庫不變條件；同樣定義也記錄在 model snapshot。
            // 不用目前的 Domain 常數組 SQL，避免未來程式規則改動影響歷史 Migration。
            migrationBuilder.Sql("""
                ALTER TABLE room_code_reservations ADD CONSTRAINT ck_code_format CHECK (code ~ '^[A-Z0-9]{6}$');
                ALTER TABLE rooms ADD CONSTRAINT ck_rooms_expiry CHECK (expires_at > created_at);
                ALTER TABLE rooms ADD CONSTRAINT ck_rooms_version CHECK (version >= 0);
                ALTER TABLE rooms ADD CONSTRAINT ck_rooms_count CHECK (item_count BETWEEN 0 AND 300);
                ALTER TABLE rooms ADD CONSTRAINT ck_rooms_bytes CHECK (total_bytes >= 0);
                ALTER TABLE room_participants ADD CONSTRAINT ck_participants_hash CHECK (octet_length(credential_hash) = 32);
                ALTER TABLE clipboard_items ADD CONSTRAINT ck_items_bytes CHECK (
                    content_bytes BETWEEN 1 AND 8192 AND content_bytes = octet_length(content));
                ALTER TABLE clipboard_items ADD CONSTRAINT ck_items_version CHECK (created_version > 0);
                CREATE INDEX ix_rooms_expiry ON rooms(expires_at, id) WHERE deleted_at IS NULL;
                CREATE INDEX ix_rooms_deleted ON rooms(deleted_at, id) WHERE deleted_at IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 先拆除循環中的一條 FK，再依順序刪表；CHECK／索引隨表移除。
            migrationBuilder.DropForeignKey(
                name: "FK_room_participants_rooms_room_id",
                table: "room_participants");

            migrationBuilder.DropTable(
                name: "clipboard_items");

            migrationBuilder.DropTable(
                name: "rooms");

            migrationBuilder.DropTable(
                name: "room_code_reservations");

            migrationBuilder.DropTable(
                name: "room_participants");
        }
    }
}
