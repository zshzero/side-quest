using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Driftworld.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_handle",
                table: "users");

            migrationBuilder.AddCheckConstraint(
                name: "ck_world_states_economy_range",
                table: "world_states",
                sql: "economy BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_world_states_environment_range",
                table: "world_states",
                sql: "environment BETWEEN 0 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_world_states_participants_nonneg",
                table: "world_states",
                sql: "participants >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_world_states_stability_range",
                table: "world_states",
                sql: "stability BETWEEN 0 AND 100");

            migrationBuilder.CreateIndex(
                name: "ix_users_handle",
                table: "users",
                column: "handle",
                unique: true,
                filter: "handle IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_world_states_economy_range",
                table: "world_states");

            migrationBuilder.DropCheckConstraint(
                name: "ck_world_states_environment_range",
                table: "world_states");

            migrationBuilder.DropCheckConstraint(
                name: "ck_world_states_participants_nonneg",
                table: "world_states");

            migrationBuilder.DropCheckConstraint(
                name: "ck_world_states_stability_range",
                table: "world_states");

            migrationBuilder.DropIndex(
                name: "ix_users_handle",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_handle",
                table: "users",
                column: "handle",
                unique: true);
        }
    }
}
