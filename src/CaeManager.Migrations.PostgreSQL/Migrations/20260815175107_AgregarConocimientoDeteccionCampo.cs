using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarConocimientoDeteccionCampo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConocimientosDeteccionCampo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EtiquetaNormalizada = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FuenteDatoCandidata = table.Column<string>(type: "text", nullable: false),
                    Prioridad = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConocimientosDeteccionCampo", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ConocimientosDeteccionCampo",
                columns: new[] { "Id", "EtiquetaNormalizada", "FuenteDatoCandidata", "Prioridad" },
                values: new object[,]
                {
                    { new Guid("6a000000-0000-0000-0000-000000000001"), "razon social", "EmpresaRazonSocial", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000002"), "empresa", "EmpresaRazonSocial", 50 },
                    { new Guid("6a000000-0000-0000-0000-000000000003"), "nombre de la empresa", "EmpresaRazonSocial", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000004"), "cif", "EmpresaCif", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000005"), "cif empresa", "EmpresaCif", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000006"), "nif", "EmpresaCif", 80 },
                    { new Guid("6a000000-0000-0000-0000-000000000007"), "nombre y apellidos", "TrabajadorNombreCompleto", 100 },
                    { new Guid("6a000000-0000-0000-0000-000000000008"), "nombre completo", "TrabajadorNombreCompleto", 90 },
                    { new Guid("6a000000-0000-0000-0000-000000000009"), "trabajador", "TrabajadorNombreCompleto", 50 },
                    { new Guid("6a00000a-0000-0000-0000-000000000001"), "nombre del trabajador", "TrabajadorNombreCompleto", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000002"), "dni", "TrabajadorDni", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000003"), "dni trabajador", "TrabajadorDni", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000004"), "documento de identidad", "TrabajadorDni", 80 },
                    { new Guid("6a00000a-0000-0000-0000-000000000005"), "puesto", "TrabajadorPuesto", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000006"), "puesto de trabajo", "TrabajadorPuesto", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000007"), "categoria profesional", "TrabajadorPuesto", 70 },
                    { new Guid("6a00000a-0000-0000-0000-000000000008"), "centro de trabajo", "CentroNombre", 100 },
                    { new Guid("6a00000a-0000-0000-0000-000000000009"), "centro", "CentroNombre", 60 },
                    { new Guid("6a00000b-0000-0000-0000-000000000001"), "direccion del centro", "CentroDireccion", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000002"), "direccion", "CentroDireccion", 60 },
                    { new Guid("6a00000b-0000-0000-0000-000000000003"), "cliente", "ClienteRazonSocial", 50 },
                    { new Guid("6a00000b-0000-0000-0000-000000000004"), "fecha", "DocumentoFechaGeneracion", 60 },
                    { new Guid("6a00000b-0000-0000-0000-000000000005"), "responsable de prl", "EmpresaResponsablePrl", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000006"), "responsable prl", "EmpresaResponsablePrl", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000007"), "representante legal", "EmpresaRepresentanteLegal", 100 },
                    { new Guid("6a00000b-0000-0000-0000-000000000008"), "contacto cae", "EmpresaContactoCae", 100 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConocimientosDeteccionCampo_EtiquetaNormalizada",
                table: "ConocimientosDeteccionCampo",
                column: "EtiquetaNormalizada");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConocimientosDeteccionCampo");
        }
    }
}
