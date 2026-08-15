using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Horizonte 2.7 del plan de negocio ("verificar con EXPLAIN, no asumir"):
    /// los 13 índices GIN trigram de
    /// <see cref="RendimientoBusquedasYCheckXorDocumento"/> (P1-14) estaban
    /// sobre la columna cruda (<c>"RazonSocial" gin_trgm_ops</c>), pero las
    /// ~32 búsquedas <c>Contains</c> de la capa Application filtran con
    /// <c>c.Columna.ToUpper().Contains(termino)</c>, que EF Core traduce a
    /// <c>upper("Columna") LIKE '%TERMINO%'</c> — una expresión distinta de
    /// la que cubre el índice. Postgres no puede usar un índice sobre
    /// <c>"Columna"</c> para una condición sobre <c>upper("Columna")</c>, así
    /// que las 32 búsquedas hacían seq scan siempre, con el índice creado e
    /// intacto pero inútil.
    ///
    /// Verificado con EXPLAIN ANALYZE contra Postgres real (300.000 filas
    /// sintéticas en Clientes): el patrón real de la aplicación
    /// (<c>upper("RazonSocial") LIKE '%ACME%'</c>) forzaba
    /// <c>Parallel Seq Scan</c> (~134 ms); el mismo predicado con el índice
    /// funcional de esta migración pasa a <c>Bitmap Index Scan</c> sobre
    /// <c>IX_Clientes_RazonSocial_Trgm</c> (~0,1 ms).
    ///
    /// La alternativa de tocar la capa Application (<c>EF.Functions.ILike</c>
    /// sobre la columna cruda, sin <c>ToUpper()</c>) se descartó: ese método
    /// vive en el paquete <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>, que
    /// Application no referencia a propósito
    /// (<c>FronterasDeCapaTests.Application_no_depende_de_Npgsql</c>) — el
    /// índice funcional corrige el problema íntegramente en la capa de datos,
    /// sin tocar ninguna Query de Application.
    ///
    /// Se sustituye el índice en el sitio (DROP + CREATE con el mismo nombre)
    /// en vez de mantener los dos: el índice sobre la columna cruda no lo usa
    /// ninguna consulta real hoy, y mantenerlo solo duplicaría el coste de
    /// escritura sin beneficio. Igual que P1-14, todo en SQL crudo fuera del
    /// modelo Fluent (ver el comentario de esa migración).
    /// </summary>
    public partial class IndicesTrigramFuncionalesSobreUpper : Migration
    {
        private static readonly (string Tabla, string Columna)[] Indices =
        [
            ("Centros", "Nombre"),
            ("Trabajadores", "Nombre"),
            ("Trabajadores", "Apellidos"),
            ("Trabajadores", "Dni"),
            ("Trabajadores", "Alias"),
            ("Vehiculos", "Nombre"),
            ("Vehiculos", "Modelo"),
            ("Vehiculos", "NumeroPlaca"),
            ("Clientes", "RazonSocial"),
            ("Empresas", "RazonSocial"),
            ("Subcontratas", "RazonSocial"),
            // La tabla se llamaba "ConversacionesCorreo" cuando P1-14 creó este
            // índice; RenombrarConversacionMensaje la renombró a "Conversaciones"
            // (y el índice a IX_Conversaciones_Asunto_Trgm) más tarde — usamos
            // el nombre actual.
            ("Conversaciones", "Asunto"),
            ("Incidencias", "Descripcion"),
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (tabla, columna) in Indices)
            {
                migrationBuilder.Sql($"DROP INDEX IF EXISTS \"IX_{tabla}_{columna}_Trgm\";");
                migrationBuilder.Sql(
                    $"""
                    CREATE INDEX IF NOT EXISTS "IX_{tabla}_{columna}_Trgm"
                    ON "{tabla}" USING gin (upper("{columna}") gin_trgm_ops);
                    """);
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (tabla, columna) in Indices.Reverse())
            {
                migrationBuilder.Sql($"DROP INDEX IF EXISTS \"IX_{tabla}_{columna}_Trgm\";");
                migrationBuilder.Sql(
                    $"""
                    CREATE INDEX IF NOT EXISTS "IX_{tabla}_{columna}_Trgm"
                    ON "{tabla}" USING gin ("{columna}" gin_trgm_ops);
                    """);
            }
        }
    }
}
