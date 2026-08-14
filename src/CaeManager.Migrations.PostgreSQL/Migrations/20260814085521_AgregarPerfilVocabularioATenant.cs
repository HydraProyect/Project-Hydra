using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarPerfilVocabularioATenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: "ClienteDirecto" y no "" — cualquier fila de Tenant
            // que ya existiera antes de esta migración (no solo el tenant #1
            // de HasData) debe quedar en un valor válido del enum, nunca en
            // una cadena vacía que reventaría al deserializar
            // PerfilVocabularioTenant la próxima vez que se lea esa fila.
            migrationBuilder.AddColumn<string>(
                name: "PerfilVocabulario",
                table: "Tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "ClienteDirecto");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerfilVocabulario",
                table: "Tenants");
        }
    }
}
