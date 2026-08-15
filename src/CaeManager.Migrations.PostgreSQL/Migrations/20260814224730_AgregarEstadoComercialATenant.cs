using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarEstadoComercialATenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue explícito a "SinSuscripcion" (el valor por
            // defecto del dominio, ver Tenant.cs) y no "" — el generador
            // scaffolda "" para cualquier columna de referencia no nullable
            // sin default declarado, pero "" no es ninguno de los valores
            // válidos de EstadoComercialTenant: cualquier tenant existente
            // más allá del seed de HasData (p. ej. el segundo tenant de
            // SegundoTenantSeeder) habría quedado con un valor que
            // Enum.Parse no puede convertir en la siguiente lectura.
            migrationBuilder.AddColumn<string>(
                name: "EstadoComercial",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "SinSuscripcion");

            migrationBuilder.AddColumn<DateTime>(
                name: "EstadoComercialActualizadoEnUtc",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "Tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeSubscriptionId",
                table: "Tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                columns: new[] { "EstadoComercial", "EstadoComercialActualizadoEnUtc", "StripeCustomerId", "StripeSubscriptionId" },
                values: new object[] { "SinSuscripcion", null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_StripeSubscriptionId",
                table: "Tenants",
                column: "StripeSubscriptionId",
                filter: "\"StripeSubscriptionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_StripeSubscriptionId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "EstadoComercial",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "EstadoComercialActualizadoEnUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StripeSubscriptionId",
                table: "Tenants");
        }
    }
}
