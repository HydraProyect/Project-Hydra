using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// RLS para RegistrosAccesoDocumentoSensible, en tanda separada de la que
    /// la crea — mismo criterio que HabilitarRlsSolicitudCertificacionTgss:
    /// no repetir el hallazgo P1-3 (una tabla EntidadConTenant nueva sin su
    /// política). El filtro global de EF ya la protege; esto es la segunda
    /// línea (RLS sobre cae_app_runtime, ver RUNBOOK-RLS.md) — DEC-36
    /// (REC-099) la exige explícitamente para este rastro.
    ///
    /// <para>
    /// <b>Por qué esta tabla necesita la política canónica y no una variante
    /// propia.</b> Lleva <c>TenantId</c> y hereda de <c>EntidadConTenant</c>,
    /// así que cae en la categoría 1 de <c>CoberturaRlsDelModeloTests</c>:
    /// RLS + FORCE + <b>exactamente</b> <c>aislamiento_tenant</c> con
    /// <c>USING</c> y <c>WITH CHECK</c>, y ninguna otra política. Un rastro de
    /// quién abrió un reconocimiento médico es, él mismo, dato sensible: sin
    /// la política, un rol restringido no filtraría por tenant —lo negaría
    /// todo— y el propietario no quedaría acotado en absoluto.
    /// </para>
    ///
    /// <para>
    /// <b>Qué se corrigió, y qué protegía de verdad lo que se quitó.</b> La
    /// primera versión de esta migración creaba dos políticas por verbo
    /// —<c>aislamiento_tenant_select</c> y <c>aislamiento_tenant_insert</c>,
    /// sin ninguna para UPDATE/DELETE— con la intención de que el carácter
    /// append-only de DEC-36 («no permitir modificación ni borrado
    /// ordinario») quedara escrito también en la base y no solo en el dominio.
    /// La intención era correcta; el mecanismo elegido, no:
    /// <list type="number">
    /// <item>Rompía el invariante del modelo entero. Los dos trinquetes que lo
    /// vigilan —<c>PoliticasRlsCubrenModeloTests</c> y
    /// <c>CoberturaRlsDelModeloTests</c>— exigen una política llamada
    /// <c>aislamiento_tenant</c> y ninguna más, y las señalaban a la vez como
    /// «falta la política» y «hay políticas de más». La regla que vigila lo
    /// segundo —las PERMISSIVE se combinan con OR, así que una de más ensancha
    /// el acceso— es cierta en general, aunque <b>no</b> describa lo que
    /// pasaba aquí: <c>FOR SELECT</c> + <c>FOR INSERT</c> sin política de
    /// UPDATE/DELETE era, en la capa de RLS aislada, más estrecho que una
    /// única política para todos los verbos. Conviene decirlo para que nadie
    /// lea este arreglo como un estrechamiento: en RLS es un ensanchamiento,
    /// y lo que lo compensa es el REVOKE del punto siguiente.</item>
    /// <item><b>No era lo que sostenía el append-only frente al tráfico.</b>
    /// El rechazo que comprueba
    /// <c>RegistroAccesoDocumentoSensibleServiceTests.Cae_app_runtime_no_puede_actualizar_ni_borrar_filas_por_sql_directo</c>
    /// es <c>42501</c> (<i>insufficient_privilege</i>), y ese código lo produce
    /// el <c>REVOKE</c> de abajo, no la ausencia de política: la comprobación
    /// de ACL ocurre al arrancar el ejecutor, y un UPDATE que RLS no autoriza
    /// no lanza excepción, afecta a cero filas en silencio. Para
    /// <c>cae_app_runtime</c> las dos políticas por verbo no añadían nada
    /// sobre el REVOKE, y frente a un propietario con DDL tampoco: quien puede
    /// retirar la política en una sentencia no queda atado por ella.</item>
    /// </list>
    /// <b>Lo que sí se pierde, dicho en voz alta.</b> Para un tercer rol que
    /// no esté exento de RLS y que tenga UPDATE por el
    /// <c>ALTER DEFAULT PRIVILEGES</c> de <c>HabilitarRlsPostgres</c>, la
    /// forma antigua bloqueaba la escritura (cero filas) y la nueva le permite
    /// modificar dentro de su tenant. Hoy no existe tal rol —<c>cae_app_soporte</c>
    /// es de solo lectura, ver <c>RolSoporteSoloLectura</c>— así que no hay
    /// regresión viva; pero si mañana se crea uno con escritura, el
    /// append-only de esta tabla hay que volver a mirarlo, y su sitio será un
    /// REVOKE explícito como el de abajo, no una política.
    /// Así que la garantía append-only vive donde de verdad muerde —el
    /// <c>REVOKE UPDATE, DELETE</c> sobre <c>cae_app_runtime</c>, necesario
    /// porque <c>ALTER DEFAULT PRIVILEGES</c> (ver <c>HabilitarRlsPostgres</c>)
    /// le concede los cuatro verbos sobre toda tabla nueva— y el aislamiento
    /// por tenant vuelve a la forma que el resto del modelo comparte. Las dos
    /// mitades quedan separadas en vez de una intentando hacer el trabajo de
    /// la otra.
    /// </para>
    ///
    /// <para>
    /// Los <c>DROP POLICY IF EXISTS</c> de los dos nombres antiguos se
    /// mantienen a propósito, pero <b>no</b> porque una base ya migrada
    /// pudiera conservarlos: <c>__EFMigrationsHistory</c> impide que este
    /// <c>Up</c> se re-ejecute, y la suite crea una base con nombre único por
    /// clase. El caso que sí los necesita es un <c>Down</c> seguido de
    /// <c>Up</c> en un entorno que llegó a aplicar la versión anterior de esta
    /// migración —hoy solo bases de desarrollo de la rama de REC-099—, porque
    /// aquel <c>Down</c> borraba los dos nombres por verbo y el de ahora solo
    /// borra el canónico. Cuestan nada y cierran esa ventana.
    /// </para>
    /// </summary>
    public partial class HabilitarRlsRegistrosAccesoDocumentoSensible : Migration
    {
        private const string Tabla = "RegistrosAccesoDocumentoSensible";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
ALTER TABLE ""{Tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" FORCE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
DROP POLICY IF EXISTS aislamiento_tenant_select ON ""{Tabla}"";
DROP POLICY IF EXISTS aislamiento_tenant_insert ON ""{Tabla}"";

CREATE POLICY aislamiento_tenant ON ""{Tabla}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

REVOKE UPDATE, DELETE ON ""{Tabla}"" FROM cae_app_runtime;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El GRANT va guardado por la existencia del rol y el REVOKE del
            // Up no, y la asimetría es deliberada — misma convención que
            // RolSoporteSoloLectura y HabilitarRlsPostgres. En el Up, un
            // clúster sin cae_app_runtime es un contrato incumplido y tiene
            // que romper siempre. En el Down no: deshacer no puede exigir una
            // precondición que deshacer existe para abandonar, y un rollback
            // que falla con 42704 deja el entorno a medias.
            migrationBuilder.Sql($@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_runtime') THEN
        GRANT UPDATE, DELETE ON ""{Tabla}"" TO cae_app_runtime;
    END IF;
END $$;

DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
ALTER TABLE ""{Tabla}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
