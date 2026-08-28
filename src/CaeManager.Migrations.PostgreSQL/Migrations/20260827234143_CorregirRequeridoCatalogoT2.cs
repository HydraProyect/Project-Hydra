using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class CorregirRequeridoCatalogoT2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Por NOMBRE, no por Id: el scaffold de EF solo tocaba los tres
            // Id fijos del catálogo semilla (tenant #1, vía HasData). Cada
            // tenant provisionado después tiene su propia copia de estos
            // tipos con un Id distinto (TipoDocumentoSeedData.CrearCopiasParaTenant),
            // así que sin este UPDATE por nombre se habrían quedado en el
            // valor viejo para todos los tenants salvo el primero — perdiendo
            // en silencio la corrección para el resto de la cartera.
            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Requerido" = 'No'
                WHERE "Nombre" = 'Mutua';
                """);

            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Requerido" = 'Condicional'
                WHERE "Nombre" IN (
                    'Designación de Recursos Preventivos',
                    'Procedimiento de Coordinación de Actividades Empresariales'
                );
                """);

            // "Traducción jurada" no se toca: ya estaba en 'No' desde la
            // traducción mecánica de T1 (EsObligatorio=false) — la tabla
            // verificada de la taxonomía coincide con el valor actual, así
            // que no hay UPDATE que hacer para ese nombre.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Requerido" = 'Si'
                WHERE "Nombre" = 'Mutua';
                """);

            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Requerido" = 'Si'
                WHERE "Nombre" IN (
                    'Designación de Recursos Preventivos',
                    'Procedimiento de Coordinación de Actividades Empresariales'
                );
                """);
        }
    }
}
