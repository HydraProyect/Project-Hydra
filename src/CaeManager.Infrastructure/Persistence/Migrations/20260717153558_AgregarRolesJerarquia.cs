using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarRolesJerarquia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EjecutivoUsuarioId",
                table: "Clientes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClienteId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CoordinadorUsuarioId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                columns: new[] { "Name", "NormalizedName" },
                values: new object[] { "CoordinadorCae", "COORDINADORCAE" });

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                columns: new[] { "Name", "NormalizedName" },
                values: new object[] { "GestorCae", "GESTORCAE" });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("30000000-0000-0000-0000-000000000005"), "30000000-0000-0000-0000-000000000005", "DireccionCae", "DIRECCIONCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000006"), "30000000-0000-0000-0000-000000000006", "Cliente", "CLIENTE" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_EjecutivoUsuarioId",
                table: "Clientes",
                column: "EjecutivoUsuarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Clientes_EjecutivoUsuarioId",
                table: "Clientes");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000006"));

            migrationBuilder.DropColumn(
                name: "EjecutivoUsuarioId",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "ClienteId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CoordinadorUsuarioId",
                table: "AspNetUsers");

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                columns: new[] { "Name", "NormalizedName" },
                values: new object[] { "Supervisor", "SUPERVISOR" });

            migrationBuilder.UpdateData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                columns: new[] { "Name", "NormalizedName" },
                values: new object[] { "EjecutivoCae", "EJECUTIVOCAE" });
        }
    }
}
