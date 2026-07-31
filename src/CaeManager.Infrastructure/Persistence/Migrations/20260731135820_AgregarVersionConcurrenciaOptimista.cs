using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarVersionConcurrenciaOptimista : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Visitas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Vehiculos",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Trabajadores",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "TarifasCliente",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Subcontratas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "RequisitosDocumentales",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Proyectos",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "MacrosRespuesta",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Incidencias",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Evaluaciones",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Empresas",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Documentos",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "ConversacionesCorreo",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Clientes",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Centros",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "TarifasCliente");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Subcontratas");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RequisitosDocumentales");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Proyectos");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "MacrosRespuesta");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Incidencias");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Evaluaciones");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Empresas");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ConversacionesCorreo");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Centros");
        }
    }
}
