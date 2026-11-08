using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class MigrationSchemaTests
{
    [Fact]
    public async Task Migration_PreservesCompositeForeignKeysAndCascadeDefinitions()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        // 查實際 PostgreSQL catalog，不能僅由 EF mapping 推論 deferred／cascade 已生效。
        var constraints = await context.Database.SqlQuery<ConstraintDefinition>($"""
            SELECT c.conname AS "Name", pg_get_constraintdef(c.oid) AS "Definition",
                   c.condeferrable AS "Deferrable", c.condeferred AS "InitiallyDeferred",
                   c.confdeltype::text AS "DeleteAction"
            FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = {database.SchemaName} AND c.contype = 'f'
            """).ToDictionaryAsync(x => x.Name);

        Assert.Equal(5, constraints.Count);
        AssertForeignKey(constraints["fk_rooms_creator"],
            "FOREIGN KEY (id, creator_participant_id) REFERENCES room_participants(room_id, id) DEFERRABLE INITIALLY DEFERRED", true, "a");
        AssertForeignKey(constraints["fk_items_owner"],
            "FOREIGN KEY (room_id, owner_participant_id) REFERENCES room_participants(room_id, id) DEFERRABLE INITIALLY DEFERRED", true, "a");
        AssertForeignKey(constraints["FK_room_participants_rooms_room_id"],
            "FOREIGN KEY (room_id) REFERENCES rooms(id) ON DELETE CASCADE", false, "c");
        AssertForeignKey(constraints["FK_clipboard_items_rooms_room_id"],
            "FOREIGN KEY (room_id) REFERENCES rooms(id) ON DELETE CASCADE", false, "c");
        AssertForeignKey(constraints["FK_rooms_room_code_reservations_code"],
            "FOREIGN KEY (code) REFERENCES room_code_reservations(code)", false, "a");
    }

    [Fact]
    public async Task Migration_CreatesChecksAndRequiredIndexes()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var checks = await context.Database.SqlQuery<NamedDefinition>($"""
            SELECT c.conname AS "Name", pg_get_constraintdef(c.oid) AS "Definition"
            FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = {database.SchemaName} AND c.contype = 'c'
            """).ToDictionaryAsync(x => x.Name, x => x.Definition);
        Assert.Equal(8, checks.Count);
        Assert.Contains("^[A-Z0-9]{6}$", checks["ck_code_format"]);
        Assert.Contains("expires_at > created_at", checks["ck_rooms_expiry"]);
        Assert.Contains("version >= 0", checks["ck_rooms_version"]);
        Assert.Contains("item_count >= 0", checks["ck_rooms_count"]);
        Assert.Contains("item_count <= 300", checks["ck_rooms_count"]);
        Assert.Contains("total_bytes >= 0", checks["ck_rooms_bytes"]);
        Assert.Contains("octet_length(credential_hash) = 32", checks["ck_participants_hash"]);
        Assert.Contains("content_bytes >= 1", checks["ck_items_bytes"]);
        Assert.Contains("content_bytes <= 8192", checks["ck_items_bytes"]);
        Assert.Contains("content_bytes = octet_length(content)", checks["ck_items_bytes"]);
        Assert.Contains("created_version > 0", checks["ck_items_version"]);

        var indexes = await context.Database.SqlQuery<NamedDefinition>($"""
            SELECT indexname AS "Name", indexdef AS "Definition"
            FROM pg_indexes WHERE schemaname = {database.SchemaName}
            """).ToDictionaryAsync(x => x.Name, x => x.Definition);
        // 包含 EF 額外建立的 Creator FK 索引與 history PK；索引增減必須明確審查。
        string[] expectedIndexes =
        [
            "PK_room_code_reservations", "PK_rooms", "PK_room_participants", "PK_clipboard_items",
            "AK_room_participants_room_id_id", "IX_room_participants_credential_hash", "IX_rooms_code",
            "IX_rooms_id_creator_participant_id", "ix_items_order", "ix_items_owner", "ix_code_reuse",
            "ix_rooms_deleted", "ix_rooms_expiry", "PK___EFMigrationsHistory"
        ];
        Assert.Equal(expectedIndexes.Order(StringComparer.Ordinal), indexes.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("(expires_at, id) WHERE (deleted_at IS NULL)", indexes["ix_rooms_expiry"]);
        Assert.Contains("(deleted_at, id) WHERE (deleted_at IS NOT NULL)", indexes["ix_rooms_deleted"]);
        Assert.Contains("(room_id, created_version DESC, id DESC)", indexes["ix_items_order"]);
        Assert.Contains("(room_id, owner_participant_id)", indexes["ix_items_owner"]);
        Assert.Contains("(reusable_after, code)", indexes["ix_code_reuse"]);
        Assert.Contains("UNIQUE INDEX", indexes["IX_rooms_code"]);
        Assert.Contains("UNIQUE INDEX", indexes["IX_room_participants_credential_hash"]);
        Assert.Contains("(room_id, id)", indexes["AK_room_participants_room_id_id"]);
    }

    [Fact]
    public async Task Migration_CanReapplyAndRollbackWithoutModelDrift()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        Assert.False(context.Database.HasPendingModelChanges());
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Single(applied);
        // 重跑不應再建表；Down 必須能解除循環 FK，之後 Up 仍能重新建立。
        await context.Database.MigrateAsync();
        Assert.Equal(applied, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
        await context.GetService<IMigrator>().MigrateAsync("0");
        var tables = await context.Database.SqlQuery<string>($"""
            SELECT tablename AS "Value" FROM pg_tables WHERE schemaname = {database.SchemaName}
            """).ToListAsync();
        Assert.Equal(["__EFMigrationsHistory"], tables);
        await context.Database.MigrateAsync();
        Assert.Equal(applied, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
    }

    [Fact]
    public async Task MigrationHistory_IsIsolatedPerTestSchema()
    {
        await using var first = await PostgresTestDatabase.CreateAsync();
        await using var second = await PostgresTestDatabase.CreateAsync();
        Assert.NotEqual(first.SchemaName, second.SchemaName);
        await using var firstContext = first.CreateContext();
        await using var secondContext = second.CreateContext();
        // 在兩個 schema 都已存在的情況下，只回退其中一個，另一個必須仍保有四表與紀錄。
        await firstContext.GetService<IMigrator>().MigrateAsync("0");
        Assert.Single(await secondContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await secondContext.Rooms.CountAsync());
        Assert.Equal(0, await secondContext.Participants.CountAsync());
        Assert.Equal(0, await secondContext.Items.CountAsync());
        Assert.Equal(0, await secondContext.CodeReservations.CountAsync());
    }

    private static void AssertForeignKey(ConstraintDefinition actual, string definition, bool deferred, string deleteAction)
    {
        Assert.Equal(definition, actual.Definition);
        Assert.Equal(deferred, actual.Deferrable);
        Assert.Equal(deferred, actual.InitiallyDeferred);
        Assert.Equal(deleteAction, actual.DeleteAction);
    }

    public sealed class NamedDefinition
    {
        public required string Name { get; set; }
        public required string Definition { get; set; }
    }

    public sealed class ConstraintDefinition
    {
        public required string Name { get; set; }
        public required string Definition { get; set; }
        public bool Deferrable { get; set; }
        public bool InitiallyDeferred { get; set; }
        public required string DeleteAction { get; set; }
    }
}
