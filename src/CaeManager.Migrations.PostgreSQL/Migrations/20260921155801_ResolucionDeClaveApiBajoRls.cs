using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// <b>La API pública no podía autenticar bajo el rol de tráfico.</b>
    /// <c>ApiKeyAuthenticationHandler</c> resuelve una petición de
    /// <c>/api/v1</c> buscando la fila de <c>ClavesApi</c> cuyo hash coincide
    /// con la clave recibida. Eso ocurre ANTES de que haya ningún tenant
    /// resuelto —es precisamente lo que esa consulta determina—, así que
    /// <c>TenantRlsConnectionInterceptor</c> ha fijado <c>app.tenant_id</c> a
    /// cadena vacía, y la política <c>aislamiento_tenant</c> de
    /// <c>ClavesApi</c> (20260802165602_HabilitarRlsClavesApi, con
    /// <c>FORCE ROW LEVEL SECURITY</c>) compara <c>"TenantId"</c> contra
    /// <c>NULL</c>: cero filas, siempre. <c>IgnoreQueryFilters()</c> quitaba el
    /// filtro global de EF, que es la primera línea; nunca quitó la segunda.
    ///
    /// <para>
    /// No se notó porque todo el instrumental conectaba como propietario
    /// (<c>postgres</c>, superusuario: RLS no se le aplica). Producción conecta
    /// con <c>cae_app_runtime</c> (<c>NOSUPERUSER NOBYPASSRLS</c>) desde
    /// 2026-08-14, donde la consulta devuelve <c>null</c> y el handler responde
    /// 401 a toda clave, vigente o no. Reproducido por HTTP antes de tocar nada
    /// (<c>ApiPublicaBajoRolRuntimeTests</c>): 401 bajo el rol de tráfico, 200
    /// bajo el propietario, misma clave y mismo arnés.
    /// </para>
    ///
    /// <para>
    /// <b>Qué NO se hace.</b> No se toca la política de <c>ClavesApi</c>, no se
    /// le retira <c>FORCE</c>, y el tráfico sigue conectando con el rol
    /// restringido. Debilitar RLS para que una consulta encaje es exactamente
    /// lo que el protocolo prohíbe: si una operación necesita cruzar la
    /// frontera, tiene que existir un contrato explícito que lo permita — esta
    /// función es ese contrato, y su superficie es de un solo dato.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué devuelve solo el <c>TenantId</c>.</b> Tercera función
    /// <c>SECURITY DEFINER</c> del repositorio, tras las dos de
    /// 20260916203428_RlsConcesionPorAdminDePlataforma, y sigue su mismo patrón.
    /// Lo único que el llamador no puede averiguar por sí mismo es de qué
    /// Tenant es la clave; en cuanto lo sabe, entra en
    /// <c>AmbitoTenantExplicito</c> y la fila entera se lee por la vía normal,
    /// con el filtro global Y la política RLS aplicándose. El resultado es más
    /// estricto que antes, no menos: la fila que acaba autenticando la petición
    /// se leyó bajo la política de su propio Tenant, cosa que
    /// <c>IgnoreQueryFilters()</c> no hacía. Devolver la fila completa habría
    /// ahorrado un viaje a costa de sacar todas sus columnas fuera de RLS.
    /// </para>
    ///
    /// <para>
    /// <b>Qué se puede aprender llamándola.</b> Solo esto: "existe una clave no
    /// eliminada con ESTE hash, y pertenece a este Tenant". El hash es SHA-256
    /// de un secreto de 256 bits generado por el propio sistema
    /// (<c>GenerarClaveApiCommand</c>), así que presentarlo ya es prueba de
    /// posesión de la clave — no hay enumeración posible, y quien la posee iba
    /// a obtener ese mismo <c>TenantId</c> como claim un microsegundo después.
    /// Las claves revocadas SÍ resuelven a propósito: quien decide sobre
    /// <c>EstaActiva</c> es el handler, sobre la entidad real, no esta función.
    /// Las eliminadas no resuelven, igual que antes.
    /// </para>
    ///
    /// <para>
    /// <c>search_path</c> fijado a <c>pg_catalog, pg_temp</c> —sin
    /// <c>public</c>— y la relación cualificada <c>public."ClavesApi"</c> en el
    /// cuerpo, por el mismo motivo que la migración de PD-A3: omitir
    /// <c>pg_temp</c> del <c>search_path</c> no lo excluye, lo pone el PRIMERO,
    /// y cualquiera con <c>TEMP</c> sobre la base (PUBLIC la tiene por defecto)
    /// podría crear una <c>"ClavesApi"</c> temporal con el hash que quisiera y
    /// hacer que esta función devolviera el Tenant que le conviniera. Doble
    /// cinturón: el orden de búsqueda no decide nada aunque alguien edite la
    /// línea. <c>EXECUTE</c> se revoca de <c>PUBLIC</c> y solo se concede a
    /// <c>cae_app_runtime</c>, que es el único rol que autentica tráfico.
    /// </para>
    ///
    /// <para>
    /// Pura DDL de servidor: no cambia el modelo de EF, así que no hay diffs en
    /// el snapshot.
    /// </para>
    /// </summary>
    public partial class ResolucionDeClaveApiBajoRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION app_tenant_de_clave_api(hash_clave text) RETURNS uuid
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT c.""TenantId"" FROM public.""ClavesApi"" c
    WHERE c.""HashClave"" = hash_clave AND c.""EstaEliminado"" = false;
$$;
REVOKE ALL ON FUNCTION app_tenant_de_clave_api(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_tenant_de_clave_api(text) TO cae_app_runtime;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP FUNCTION IF EXISTS app_tenant_de_clave_api(text);
");
        }
    }
}
