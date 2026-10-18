using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Correcciones de la revisión de <see cref="AnadeDelegacionYOficioEmpresa"/>, que ya estaba en main y no se edita:
    /// <list type="bullet">
    /// <item>SELECT del catálogo a <c>cae_app_aprovisionamiento</c>: <c>CrearTrabajadorCommand</c> es un comando de
    /// aprovisionamiento y lee la Delegación y el Oficio elegidos; sin el GRANT, 42501. Solo lectura.</item>
    /// <item>Nombres mal normalizados por la migración de datos anterior, que recortaba antes de colapsar los espacios:
    /// un tabulador en un extremo dejaba un espacio y un Oficio duplicado. Se normalizan con el criterio de
    /// <c>NombreDeCatalogo</c> (colapsar y después recortar) y los duplicados de una misma Empresa se funden en uno.</item>
    /// <item>Índice único de nombre por Empresa (<c>lower("Nombre")</c>, solo filas vivas), la red de la comprobación de
    /// los comandos de guardar; <c>CatalogoTrabajadoresEmpresaRepository</c> traduce su violación a un fallo.</item>
    /// </list>
    /// <c>CK_Trabajadores_CatalogoSoloDeEmpresa</c> ya existe (lo creó con SQL la migración anterior): aquí solo entra en
    /// el modelo (<c>TrabajadorConfiguration</c>), así que esta migración no lo vuelve a crear. Corre con la identidad del
    /// migrador, superusuario o con BYPASSRLS (ver <see cref="LineaBaseCompactada"/>).
    /// </summary>
    public partial class CorrigeDelegacionYOficioEmpresa : Migration
    {
        private static readonly (string Tabla, string Columna, string SuprimirAlFundir)[] Catalogos =
        [
            ("DelegacionesEmpresa", "DelegacionEmpresaId",
                """, "ContactoReclamacionNombre" = NULL, "ContactoReclamacionCorreo" = NULL, "ContactoReclamacionTelefono" = NULL"""),
            ("OficiosEmpresa", "OficioEmpresaId", ""),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (tabla, columna, suprimir) in Catalogos)
            {
                migrationBuilder.Sql($"""GRANT SELECT ON TABLE public."{tabla}" TO cae_app_aprovisionamiento;""");

                // Por cada (Tenant, Empresa, clave) queda una fila: la que ya tenía el nombre normalizado y, a igualdad,
                // la de menor Id. Sus Trabajadores pasan a ella, las demás se eliminan (borrado lógico; la Delegación
                // fundida pierde su contacto, dato personal que ya no se usa) y la que queda toma el nombre normalizado.
                migrationBuilder.Sql($"""
                    CREATE TEMP TABLE fusion_catalogo AS
                    SELECT n."Id", first_value(n."Id") OVER (
                               PARTITION BY n."TenantId", n."EmpresaId", lower(n.normalizado)
                               ORDER BY (n."Nombre" = n.normalizado) DESC, n."Id") AS "Destino",
                           n.normalizado
                      FROM (SELECT c."Id", c."TenantId", c."EmpresaId", c."Nombre",
                                   btrim(regexp_replace(c."Nombre", '\s+', ' ', 'g')) AS normalizado
                              FROM public."{tabla}" c
                             WHERE NOT c."EstaEliminado") n;

                    UPDATE public."Trabajadores" t
                       SET "{columna}" = f."Destino"
                      FROM fusion_catalogo f
                     WHERE t."{columna}" = f."Id" AND f."Id" <> f."Destino";

                    UPDATE public."{tabla}" c
                       SET "EstaEliminado" = true, "EliminadoEnUtc" = now(){suprimir}
                      FROM fusion_catalogo f
                     WHERE c."Id" = f."Id" AND f."Id" <> f."Destino";

                    UPDATE public."{tabla}" c
                       SET "Nombre" = f.normalizado
                      FROM fusion_catalogo f
                     WHERE c."Id" = f."Id" AND f."Id" = f."Destino" AND c."Nombre" <> f.normalizado;

                    DROP TABLE fusion_catalogo;

                    CREATE UNIQUE INDEX "UX_{tabla}_TenantId_EmpresaId_Clave"
                        ON public."{tabla}" ("TenantId", "EmpresaId", lower("Nombre")) WHERE NOT "EstaEliminado";
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // La fusión de duplicados no se deshace: las filas eliminadas siguen ahí y los Trabajadores, en la que quedó.
            foreach (var (tabla, _, _) in Catalogos)
            {
                migrationBuilder.Sql($"""
                    DROP INDEX IF EXISTS public."UX_{tabla}_TenantId_EmpresaId_Clave";
                    REVOKE SELECT ON TABLE public."{tabla}" FROM cae_app_aprovisionamiento;
                    """);
            }
        }
    }
}
