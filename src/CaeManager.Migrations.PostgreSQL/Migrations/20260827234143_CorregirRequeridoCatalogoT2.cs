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

            // Corrige de paso un defecto de T1 (#301): su UPDATE masivo puso
            // "Naturaleza" = 'RequisitoCliente' en TODAS las filas de TODOS
            // los tenants, y luego repuso los valores verificados con
            // UpdateData por Id — pero esos Id son los fijos de HasData, que
            // solo existen en la semilla del tenant #1. Cualquier otro
            // tenant (copia creada por CrearCopiasParaTenant, Id distinto)
            // se quedó con RequisitoCliente incluso en las 16 obligaciones
            // verificadas contra fuente oficial — sub-afirma, pero es falso
            // igual. Misma tabla que NaturalezaDe (TipoDocumentoSeedData.cs),
            // repuesta aquí por Nombre para alcanzar a todos los tenants.
            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Naturaleza" = 'ObligacionLegal'
                WHERE "Nombre" IN (
                    'EVR (Evaluación de Riesgos Laborales)',
                    'PAP (Planificación de la Actividad Preventiva)',
                    'Plan de Prevención',
                    'Modalidad Preventiva',
                    'Información Art. 18',
                    'Formación Art. 19'
                );
                """);

            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Naturaleza" = 'ObligacionCondicionada'
                WHERE "Nombre" IN (
                    'Apto médico laboral',
                    'Designación de Recursos Preventivos',
                    'Procedimiento de Coordinación de Actividades Empresariales',
                    'Certificado de estar al corriente con Hacienda',
                    'Comunicación de desplazamiento',
                    'Documento acreditativo de empresa extranjera',
                    'Certificado A1 de Seguridad Social'
                );
                """);

            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Naturaleza" = 'PracticaSector'
                WHERE "Nombre" IN (
                    'EPIS (firma)',
                    'Certificado de estar al corriente con la Seguridad Social',
                    'SPA (Servicio de Prevención Ajeno)'
                );
                """);
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

            // Espejo del arreglo del defecto de T1: revertir deja estas 16
            // filas donde T1 las había dejado (RequisitoCliente) para todos
            // los tenants EXCEPTO el #1 — ese ya tenía el valor correcto
            // desde el propio T1 (UpdateData por Id), y este Up solo lo
            // reafirmó; tocarlo aquí sería una regresión nueva que T1 nunca
            // tuvo, no "volver atrás".
            migrationBuilder.Sql("""
                UPDATE "TiposDocumento" SET "Naturaleza" = 'RequisitoCliente'
                WHERE "TenantId" <> '00000000-0000-0000-0000-000000000001'
                  AND "Nombre" IN (
                    'EVR (Evaluación de Riesgos Laborales)',
                    'PAP (Planificación de la Actividad Preventiva)',
                    'Plan de Prevención',
                    'Modalidad Preventiva',
                    'Información Art. 18',
                    'Formación Art. 19',
                    'Apto médico laboral',
                    'Designación de Recursos Preventivos',
                    'Procedimiento de Coordinación de Actividades Empresariales',
                    'Certificado de estar al corriente con Hacienda',
                    'Comunicación de desplazamiento',
                    'Documento acreditativo de empresa extranjera',
                    'Certificado A1 de Seguridad Social',
                    'EPIS (firma)',
                    'Certificado de estar al corriente con la Seguridad Social',
                    'SPA (Servicio de Prevención Ajeno)'
                );
                """);
        }
    }
}
