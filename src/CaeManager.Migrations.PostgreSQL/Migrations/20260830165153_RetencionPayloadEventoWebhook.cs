using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RetencionPayloadEventoWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PayloadRedactado",
                table: "EventosWebhook",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_Estado_FechaRecepcionUtc_SinRedactar",
                table: "EventosWebhook",
                columns: new[] { "Estado", "FechaRecepcionUtc" },
                filter: "\"PayloadRedactado\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventosWebhook_Estado_FechaRecepcionUtc_SinRedactar",
                table: "EventosWebhook");

            migrationBuilder.DropColumn(
                name: "PayloadRedactado",
                table: "EventosWebhook");
        }
    }
}
