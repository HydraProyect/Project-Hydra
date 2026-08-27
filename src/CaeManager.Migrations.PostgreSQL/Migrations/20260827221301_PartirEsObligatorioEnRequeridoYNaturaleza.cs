using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PartirEsObligatorioEnRequeridoYNaturaleza : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ORDEN INVERTIDO respecto al scaffold, y no es cosmético: EF
            // generó el DropColumn ANTES de crear las columnas nuevas, o sea
            // que el dato de origen desaparecía antes de poder traducirlo.
            // Las filas del catálogo semilla las repone el HasData de más
            // abajo, pero los tipos que un tenant creó por su cuenta NO están
            // en ese HasData: se habrían quedado todos en el valor por
            // defecto, perdiendo en silencio qué se pedía y qué no.
            migrationBuilder.AddColumn<string>(
                name: "Naturaleza",
                table: "TiposDocumento",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Requerido",
                table: "TiposDocumento",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            // Traducción de los datos existentes, tenant por tenant. El eje
            // "requerido" sí tiene equivalencia mecánica con el booleano
            // viejo. La "naturaleza" no la tiene: para lo que no está en el
            // catálogo verificado, RequisitoCliente es la afirmación más
            // débil y la única segura — dice "alguien lo pide", que es cierto
            // de todo el catálogo, y no atribuye ninguna ley que nadie haya
            // comprobado.
            migrationBuilder.Sql("""
                UPDATE "TiposDocumento"
                SET "Requerido" = CASE WHEN "EsObligatorio" THEN 'Si' ELSE 'No' END,
                    "Naturaleza" = 'RequisitoCliente';
                """);

            migrationBuilder.DropColumn(
                name: "EsObligatorio",
                table: "TiposDocumento");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionCondicionada", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "PracticaSector", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionLegal", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionLegal", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "PracticaSector", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionCondicionada", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "PracticaSector", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionLegal", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionLegal", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionLegal", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionCondicionada", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionCondicionada", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionLegal", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionCondicionada", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionCondicionada", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "ObligacionCondicionada", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000016-0000-0000-0000-000000000016"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "Si" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000001"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000002"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000005"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000006"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000001"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000002"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000003"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000004"),
                columns: new[] { "Naturaleza", "Requerido" },
                values: new object[] { "RequisitoCliente", "No" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mismo cuidado que en Up, en espejo: crear primero, traducir, y
            // solo entonces borrar. Con el orden del scaffold, revertir dejaba
            // todos los tipos de los tenants en EsObligatorio=false — es
            // decir, la vuelta atrás perdía silenciosamente qué se pedía.
            // La naturaleza sí se pierde: el booleano no puede representarla,
            // y eso es una consecuencia inherente de volver al modelo viejo,
            // no un descuido.
            migrationBuilder.AddColumn<bool>(
                name: "EsObligatorio",
                table: "TiposDocumento",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "EsObligatorio" = ("Requerido" = 'Si');
                """);

            migrationBuilder.DropColumn(
                name: "Naturaleza",
                table: "TiposDocumento");

            migrationBuilder.DropColumn(
                name: "Requerido",
                table: "TiposDocumento");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000016-0000-0000-0000-000000000016"),
                column: "EsObligatorio",
                value: true);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000005"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000006"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000001"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000002"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000003"),
                column: "EsObligatorio",
                value: false);

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("70000000-0000-0000-0000-000000000004"),
                column: "EsObligatorio",
                value: false);
        }
    }
}
